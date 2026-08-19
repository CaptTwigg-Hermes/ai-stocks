using System.Net;
using System.Net.Sockets;
using System.Text;
using AiStocks.Research.Decisions;
using AiStocks.Research.Evidence;

namespace AiStocks.Research.Tests;

public sealed class EvidenceVerifierTests
{
    [Fact]
    public async Task VerifyAsync_IndependentlyFetchesVisibleClaimPublicationTimeAndHash()
    {
        const string html = "<html><head><meta property=\"article:published_time\" content=\"2026-08-08T09:00:00Z\"></head><body><script>Exact catalyst text</script><p>Exact <b>catalyst</b> text</p></body></html>";
        var transport = new FakeTransport(Response(HttpStatusCode.OK, html));
        var verifier = new EvidenceVerifier(new FakeDns(IPAddress.Parse("93.184.216.34")), transport, TestOptions());
        var claim = new EvidenceClaim(new Uri("https://example.com/news"), DateTimeOffset.Parse("2026-08-08T09:00:00Z"), "Exact catalyst text");

        var verified = await verifier.VerifyAsync(claim, CancellationToken.None);

        Assert.Equal("https://example.com/news", verified.FinalUrl.AbsoluteUri);
        Assert.Equal(claim.PublishedAt, verified.PublishedAt);
        Assert.Equal(64, verified.ContentSha256.Length);
        Assert.Equal(claim.ExactExcerpt, verified.ExactExcerpt);
        Assert.Equal("example.com", transport.Requests.Single().Uri.Host);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), transport.Requests.Single().Addresses.Single());
    }

    [Fact]
    public async Task VerifyAsync_RejectsClaimOnlyPresentInScriptOrMarkup()
    {
        const string html = "<html><head><meta property=\"article:published_time\" content=\"2026-08-08T09:00:00Z\"></head><body><script>hidden claim</script><p>Other text</p></body></html>";
        var verifier = Verifier(Response(HttpStatusCode.OK, html));
        var claim = Claim("https://example.com", "hidden claim");

        await Assert.ThrowsAsync<EvidenceVerificationException>(() => verifier.VerifyAsync(claim, CancellationToken.None));
    }

    [Theory]
    [InlineData("<p hidden>Exact catalyst text</p>")]
    [InlineData("<p aria-hidden=\"true\">Exact catalyst text</p>")]
    [InlineData("<p style=\"display:none\">Exact catalyst text</p>")]
    [InlineData("<p style=\"visibility: hidden\">Exact catalyst text</p>")]
    [InlineData("<style>.concealed { display: none }</style><p class=\"concealed\">Exact catalyst text</p>")]
    [InlineData("<template><p>Exact catalyst text</p></template>")]
    [InlineData("<p hidden><b>Exact catalyst text</p><p>Other text</b>")]
    public async Task VerifyAsync_RejectsHiddenAndMalformedDomClaims(string body)
    {
        var html = ValidHtml.Replace("Exact catalyst text", body);
        var verifier = Verifier(Response(HttpStatusCode.OK, html));

        await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            verifier.VerifyAsync(Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));
    }

    [Theory]
    [InlineData("<dialog>Exact catalyst text</dialog>")]
    [InlineData("<details><summary>Other text</summary><p>Exact catalyst text</p></details>")]
    [InlineData("<details open><summary>Outer</summary><details><summary>Inner</summary><p>Exact catalyst text</p></details></details>")]
    public async Task VerifyAsync_RejectsClaimsHiddenByClosedNativeDisclosureState(string body)
    {
        var html = ValidHtml.Replace("Exact catalyst text", body);

        await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            Verifier(Response(HttpStatusCode.OK, html)).VerifyAsync(
                Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));
    }

    [Theory]
    [InlineData("<dialog open>Exact catalyst text</dialog>")]
    [InlineData("<dialog open=\"false\">Exact catalyst text</dialog>")]
    [InlineData("<details open><summary>Other text</summary><p>Exact catalyst text</p></details>")]
    [InlineData("<details open=\"false\"><summary>Other text</summary><p>Exact catalyst text</p></details>")]
    [InlineData("<details><summary>Exact catalyst text</summary><p>Other text</p></details>")]
    public async Task VerifyAsync_AcceptsClaimsVisibleUnderNativeOpenAttributeSemantics(string body)
    {
        var html = ValidHtml.Replace("Exact catalyst text", body);

        var verified = await Verifier(Response(HttpStatusCode.OK, html)).VerifyAsync(
            Claim("https://example.com", "Exact catalyst text"), CancellationToken.None);

        Assert.Equal("Exact catalyst text", verified.ExactExcerpt);
    }

    [Theory]
    [InlineData("<div popover>Exact catalyst text</div>")]
    [InlineData("<select><option>Other text</option><option>Exact catalyst text</option></select>")]
    [InlineData("<datalist><option>Exact catalyst text</option></datalist>")]
    public async Task VerifyAsync_FailsClosedForUnsupportedNativeVisibilityElements(string body)
    {
        var html = ValidHtml.Replace("Exact catalyst text", body);

        var exception = await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            Verifier(Response(HttpStatusCode.OK, html)).VerifyAsync(
                Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));

        Assert.Contains("native visibility", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<p style=\"font-size:0\">Exact catalyst text</p>")]
    [InlineData("<p style=\"color:transparent\">Exact catalyst text</p>")]
    [InlineData("<div style=\"color:transparent\"><p style=\"color:currentColor;opacity:.01\">Exact catalyst text</p></div>")]
    [InlineData("<p style=\"clip-path:inset(100%)\">Exact catalyst text</p>")]
    [InlineData("<p style=\"width:0;height:0;overflow:hidden\">Exact catalyst text</p>")]
    [InlineData("<p style=\"position:absolute;left:-99999px\">Exact catalyst text</p>")]
    [InlineData("<style>@media screen { .concealed { display:none } }</style><p class=\"concealed\">Exact catalyst text</p>")]
    public async Task VerifyAsync_RejectsAmbiguousCssVisibility(string body)
    {
        var html = ValidHtml.Replace("Exact catalyst text", body);

        var exception = await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            Verifier(Response(HttpStatusCode.OK, html)).VerifyAsync(
                Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));

        Assert.Contains("CSS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_RejectsHtmlWhoseVisibilityDependsOnExternalCss()
    {
        var html = ValidHtml.Replace("<head>", "<head><link rel=\"stylesheet\" href=\"/hidden.css\">")
            .Replace("Exact catalyst text", "<p class=\"concealed\">Exact catalyst text</p>");
        var transport = new FakeTransport(Response(HttpStatusCode.OK, html));
        var verifier = new EvidenceVerifier(new FakeDns(IPAddress.Parse("93.184.216.34")), transport, TestOptions());

        var exception = await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            verifier.VerifyAsync(Claim("https://example.com/news", "Exact catalyst text"), CancellationToken.None));

        Assert.Contains("external stylesheet", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task VerifyAsync_AcceptsExactNewsArticleDescriptionFromFetchedStructuredMetadata()
    {
        const string html = """
            <html><head>
            <link rel="stylesheet" href="/site.css">
            <meta property="article:published_time" content="2026-08-08T09:00:00Z">
            <script type="application/ld+json">
            {"@context":"https://schema.org","@type":"NewsArticle","datePublished":"2026-08-08T09:00:00Z","description":"Exact catalyst text"}
            </script></head><body><p class="concealed">Other text</p></body></html>
            """;

        var verified = await Verifier(Response(HttpStatusCode.OK, html)).VerifyAsync(
            Claim("https://example.com/news", "Exact catalyst text"), CancellationToken.None);

        Assert.Equal("Exact catalyst text", verified.ExactExcerpt);
    }

    [Fact]
    public async Task VerifyAsync_RejectsStructuredExcerptChangedByHtmlDecoding()
    {
        const string html = """
            <html><head>
            <link rel="stylesheet" href="/site.css">
            <meta property="article:published_time" content="2026-08-08T09:00:00Z">
            <script type="application/ld+json">
            {"@context":"https://schema.org","@type":"NewsArticle","datePublished":"2026-08-08T09:00:00Z","description":"A &amp; B"}
            </script></head><body><p class="concealed">Other text</p></body></html>
            """;

        await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            Verifier(Response(HttpStatusCode.OK, html)).VerifyAsync(
                Claim("https://example.com/news", "A &amp; B"), CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_RejectsExcerptSpanningSeparateVisibleBlocks()
    {
        var html = ValidHtml.Replace("Exact catalyst text", "<p>Exact catalyst</p><p>text</p>");
        var verifier = Verifier(Response(HttpStatusCode.OK, html));

        await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            verifier.VerifyAsync(Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));
    }

    [Theory]
    [InlineData("https://singlelabel/news")]
    [InlineData("https://example.com:8443/news")]
    [InlineData("https://example.com/news#fragment")]
    public async Task VerifyAsync_RejectsNonPublicUrlForms(string url)
    {
        var transport = new FakeTransport(Response(HttpStatusCode.OK, ValidHtml));
        var verifier = new EvidenceVerifier(new FakeDns(IPAddress.Parse("93.184.216.34")), transport, TestOptions());

        await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            verifier.VerifyAsync(Claim(url, "Exact catalyst text"), CancellationToken.None));
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.9")]
    [InlineData("192.88.99.1")]
    [InlineData("192.31.196.1")]
    [InlineData("192.52.193.1")]
    [InlineData("192.175.48.1")]
    [InlineData("198.51.100.1")]
    [InlineData("240.0.0.1")]
    [InlineData("::2")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("100::1")]
    [InlineData("2001::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:10::1")]
    [InlineData("2001:db8::1")]
    [InlineData("3fff::1")]
    public async Task VerifyAsync_RejectsNonPublicDnsAnswers(string address)
    {
        var transport = new FakeTransport(Response(HttpStatusCode.OK, ValidHtml));
        var verifier = new EvidenceVerifier(new FakeDns(IPAddress.Parse(address)), transport, TestOptions());

        await Assert.ThrowsAsync<EvidenceVerificationException>(() => verifier.VerifyAsync(Claim("https://research.example", "Exact catalyst text"), CancellationToken.None));
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task VerifyAsync_RejectsIfAnyDnsAnswerIsPrivate()
    {
        var verifier = new EvidenceVerifier(
            new FakeDns(IPAddress.Parse("93.184.216.34"), IPAddress.Loopback),
            new FakeTransport(Response(HttpStatusCode.OK, ValidHtml)), TestOptions());

        await Assert.ThrowsAsync<EvidenceVerificationException>(() => verifier.VerifyAsync(Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_ValidatesEveryRedirectAndPreservesOriginalTlsHostname()
    {
        var transport = new FakeTransport(
            Response(HttpStatusCode.Redirect, "", new Dictionary<string, string> { ["Location"] = "https://cdn.example.org/item" }),
            Response(HttpStatusCode.OK, ValidHtml));
        var dns = new HostDns(new Dictionary<string, IPAddress>
        {
            ["example.com"] = IPAddress.Parse("93.184.216.34"),
            ["cdn.example.org"] = IPAddress.Parse("1.1.1.1")
        });
        var verifier = new EvidenceVerifier(dns, transport, TestOptions());

        var result = await verifier.VerifyAsync(Claim("https://example.com/start", "Exact catalyst text"), CancellationToken.None);

        Assert.Equal("cdn.example.org", result.FinalUrl.Host);
        Assert.Equal(new[] { "example.com", "cdn.example.org" }, transport.Requests.Select(x => x.Uri.Host));
        Assert.Equal("https://example.com/start", result.OriginalUrl.AbsoluteUri);
        Assert.Equal(2, result.Hops.Length);
        Assert.Equal("93.184.216.34", result.Hops[0].PinnedAddress.ToString());
        Assert.Equal("1.1.1.1", result.Hops[1].PinnedAddress.ToString());
        Assert.Equal("https://cdn.example.org/item", result.Hops[0].RedirectTarget?.AbsoluteUri);
        Assert.Equal(Encoding.UTF8.GetBytes(ValidHtml), result.ImmutableContent);
        Assert.Contains(result.ResponseHeaders.Keys, key => key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyAsync_RejectsHttpsDowngradeTooManyRedirectsAndOversizedBody()
    {
        var downgrade = Verifier(Response(HttpStatusCode.Redirect, "", new Dictionary<string, string> { ["Location"] = "http://example.com/item" }));
        await Assert.ThrowsAsync<EvidenceVerificationException>(() => downgrade.VerifyAsync(Claim("https://example.com", "x"), CancellationToken.None));

        var redirects = new FakeTransport(
            Response(HttpStatusCode.Redirect, "", new Dictionary<string, string> { ["Location"] = "/1" }),
            Response(HttpStatusCode.Redirect, "", new Dictionary<string, string> { ["Location"] = "/2" }));
        var lowRedirectVerifier = new EvidenceVerifier(new FakeDns(IPAddress.Parse("93.184.216.34")), redirects, TestOptions() with { MaximumRedirects = 1 });
        await Assert.ThrowsAsync<EvidenceVerificationException>(() => lowRedirectVerifier.VerifyAsync(Claim("https://example.com", "x"), CancellationToken.None));

        var oversized = Verifier(Response(HttpStatusCode.OK, new string('x', 101)), TestOptions() with { MaximumResponseBytes = 100 });
        await Assert.ThrowsAsync<EvidenceVerificationException>(() => oversized.VerifyAsync(Claim("https://example.com", "x"), CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_RejectsMissingOrMismatchedIndependentlyParsedPublicationTime()
    {
        var missing = Verifier(Response(HttpStatusCode.OK, "<html><body>Exact catalyst text</body></html>"));
        await Assert.ThrowsAsync<EvidenceVerificationException>(() => missing.VerifyAsync(Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));

        var mismatchHtml = ValidHtml.Replace("2026-08-08T09:00:00Z", "2026-08-08T08:00:00Z");
        var mismatch = Verifier(Response(HttpStatusCode.OK, mismatchHtml));
        await Assert.ThrowsAsync<EvidenceVerificationException>(() => mismatch.VerifyAsync(Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));
    }

    [Theory]
    [InlineData("image/png", null)]
    [InlineData("text/html", "gzip")]
    public async Task VerifyAsync_RejectsNonTextOrEncodedRepresentations(string contentType, string? contentEncoding)
    {
        var headers = new Dictionary<string, string> { ["Content-Type"] = contentType };
        if (contentEncoding is not null) headers["Content-Encoding"] = contentEncoding;
        var verifier = Verifier(Response(HttpStatusCode.OK, ValidHtml, headers));

        await Assert.ThrowsAsync<EvidenceVerificationException>(() =>
            verifier.VerifyAsync(Claim("https://example.com", "Exact catalyst text"), CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_EnforcesOverallDeadline()
    {
        var transport = new DelayingTransport();
        var verifier = new EvidenceVerifier(new FakeDns(IPAddress.Parse("93.184.216.34")), transport, TestOptions() with { Deadline = TimeSpan.FromMilliseconds(30) });

        await Assert.ThrowsAsync<EvidenceVerificationException>(() => verifier.VerifyAsync(Claim("https://example.com", "text"), CancellationToken.None));
    }

    private const string ValidHtml = "<html><head><meta name=\"datePublished\" content=\"2026-08-08T09:00:00Z\"></head><body>Exact catalyst text</body></html>";
    private static EvidenceClaim Claim(string url, string excerpt) => new(new Uri(url), DateTimeOffset.Parse("2026-08-08T09:00:00Z"), excerpt);
    private static EvidenceVerifier Verifier(EvidenceHttpResponse response, EvidenceVerificationOptions? options = null) =>
        new(new FakeDns(IPAddress.Parse("93.184.216.34")), new FakeTransport(response), options ?? TestOptions());
    private static EvidenceVerificationOptions TestOptions() => new() { Deadline = TimeSpan.FromSeconds(2), MaximumResponseBytes = 10_000, MaximumRedirects = 3 };
    private static EvidenceHttpResponse Response(HttpStatusCode status, string content, IReadOnlyDictionary<string, string>? headers = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/html; charset=utf-8" };
        if (headers is not null)
            foreach (var pair in headers) values[pair.Key] = pair.Value;
        return new EvidenceHttpResponse(status, values, new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    private sealed class FakeDns(params IPAddress[] addresses) : IHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class HostDns(IReadOnlyDictionary<string, IPAddress> addresses) : IHostResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IPAddress>>([addresses[host]]);
    }

    private sealed class FakeTransport(params EvidenceHttpResponse[] responses) : IEvidenceHttpTransport
    {
        private int _next;
        public List<(Uri Uri, IReadOnlyList<IPAddress> Addresses)> Requests { get; } = [];
        public Task<EvidenceHttpResponse> SendAsync(Uri uri, IReadOnlyList<IPAddress> approvedAddresses, CancellationToken cancellationToken)
        {
            Requests.Add((uri, approvedAddresses));
            return Task.FromResult(responses[_next++]);
        }
    }

    private sealed class DelayingTransport : IEvidenceHttpTransport
    {
        public async Task<EvidenceHttpResponse> SendAsync(Uri uri, IReadOnlyList<IPAddress> approvedAddresses, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
