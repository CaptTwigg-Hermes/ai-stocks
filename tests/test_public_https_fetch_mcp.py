import importlib.util
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest


MODULE_PATH = Path(__file__).parents[1] / "scripts" / "public_https_fetch_mcp.py"


def load_module():
    spec = importlib.util.spec_from_file_location("public_https_fetch_mcp", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_url_validation_rejects_non_https_credentials_and_private_literals():
    module = load_module()
    for url in (
        "http://example.com/news",
        "https://user:pass@example.com/news",
        "https://127.0.0.1/news",
        "https://[::1]/news",
        "https://[::ffff:127.0.0.1]/news",
        "https://[64:ff9b::7f00:1]/news",
        "https://[64:ff9b::a9fe:a9fe]/news",
        "https://localhost/news",
        "https://singlelabel/news",
        "https://example.com/news#fragment",
        "https://foo_bar.example/news",
        "https://93.184.216.34/news",
        "https://[2606:4700:4700::1111]/news",
        "https://127.1/news",
        "https://0x7f000001/news",
        "https://0177.0.0.1/news",
        "https://0x7f.0x0.0x0.0x1/news",
    ):
        with pytest.raises(ValueError):
            module.validate_url(url)

    with pytest.raises(ValueError):
        module.validate_url("https://example.com/" + "x" * 2029)
    with pytest.raises(ValueError):
        module.validate_url("https://example.com/" + "😀" * 1015)


def test_visible_text_and_metadata_are_bounded_and_script_free():
    module = load_module()
    html = b'''<html><head><meta property="article:published_time" content="2026-08-19T08:00:00Z">
    <script type="application/ld+json">{"@context":"https://schema.org","@type":"NewsArticle","description":"Revenue rose 10 percent."}</script></head>
    <body><script>steal()</script><h1>Issuer result</h1><p>Revenue rose 10 percent.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html; charset=utf-8", html)
    assert "Issuer result" in result["visible_text"]
    assert "Revenue rose 10 percent." in result["visible_text"]
    assert "steal()" not in result["visible_text"]
    assert result["publication_metadata"] == ["2026-08-19T08:00:00Z"]
    assert result["structured_article_text"] == ["Revenue rose 10 percent."]
    assert len(result["visible_text"]) <= module.MAXIMUM_VISIBLE_CHARACTERS


def test_resolution_rejects_mixed_public_private_and_special_use(monkeypatch):
    module = load_module()
    monkeypatch.setattr(module.socket, "getaddrinfo", lambda *_args, **_kwargs: [
        (module.socket.AF_INET, module.socket.SOCK_STREAM, 6, "", ("93.184.216.34", 443)),
        (module.socket.AF_INET, module.socket.SOCK_STREAM, 6, "", ("127.0.0.1", 443)),
    ])
    with pytest.raises(ValueError, match="non-public"):
        module.resolve_public_addresses("example.com")


def test_redirect_is_revalidated_and_limits_and_compression_fail_closed(monkeypatch):
    module = load_module()
    monkeypatch.setattr(module, "_read_response", lambda _url, *_args: (
        302, {"location": "https://127.0.0.1/private"}, b""))
    with pytest.raises(ValueError, match="IP-literal"):
        module._fetch_public_https_in_process("https://example.com/start")

    calls = []
    monkeypatch.setattr(module, "_read_response", lambda url, *_args: (
        calls.append(url) or (302, {"location": "https://example.com/next"}, b"")))
    with pytest.raises(ValueError, match="Redirect limit"):
        module._fetch_public_https_in_process("https://example.com/start")
    assert len(calls) == module.MAXIMUM_REDIRECTS + 1

    monkeypatch.setattr(module, "_read_response", lambda _url, *_args: (
        200, {"content-type": "text/html", "content-encoding": "gzip"}, b"compressed"))
    with pytest.raises(ValueError, match="Compressed"):
        module._fetch_public_https_in_process("https://example.com/news")


def test_transport_pins_resolved_address_and_enforces_response_byte_bound(monkeypatch):
    module = load_module()
    connections = []

    class FakeSocket:
        def sendall(self, _request):
            pass

        def settimeout(self, _timeout):
            pass

        def close(self):
            pass

    class FakeContext:
        def wrap_socket(self, raw, server_hostname):
            assert server_hostname == "example.com"
            return raw

    class FakeResponse:
        status = 200

        def __init__(self, _wrapped):
            pass

        def begin(self):
            pass

        def getheaders(self):
            return [("Content-Type", "text/plain")]

        def read(self, amount):
            assert amount == module.MAXIMUM_RESPONSE_BYTES + 1
            return b"x" * amount

    monkeypatch.setattr(module, "resolve_public_addresses", lambda _host, *_args: ["93.184.216.34"])
    monkeypatch.setattr(module.socket, "create_connection", lambda endpoint, timeout: (
        connections.append((endpoint, timeout)) or FakeSocket()))
    monkeypatch.setattr(module.ssl, "create_default_context", lambda: FakeContext())
    monkeypatch.setattr(module.http.client, "HTTPResponse", FakeResponse)

    with pytest.raises(ValueError, match="size limit"):
        module._read_response("https://example.com/news")
    assert connections == [(("93.184.216.34", 443), 5)]


def test_transport_joins_duplicate_response_headers_like_verifier(monkeypatch):
    module = load_module()

    class FakeSocket:
        def sendall(self, _request):
            pass

        def settimeout(self, _timeout):
            pass

        def close(self):
            pass

    class FakeContext:
        def wrap_socket(self, raw, server_hostname):
            assert server_hostname == "example.com"
            return raw

    class FakeResponse:
        status = 200

        def __init__(self, _wrapped):
            pass

        def begin(self):
            pass

        def getheaders(self):
            return [
                ("Content-Type", "text/html"),
                ("Content-Encoding", "gzip"),
                ("Content-Encoding", "identity"),
            ]

        def read(self, _amount):
            return b"document"

    monkeypatch.setattr(module, "resolve_public_addresses", lambda _host, _deadline=None: ["93.184.216.34"])
    monkeypatch.setattr(module.socket, "create_connection", lambda _endpoint, timeout: FakeSocket())
    monkeypatch.setattr(module.ssl, "create_default_context", lambda: FakeContext())
    monkeypatch.setattr(module.http.client, "HTTPResponse", FakeResponse)

    _status, headers, _body = module._read_response(
        "https://example.com/news", module.time.monotonic() + 10)

    assert headers["content-encoding"] == "gzip,identity"


def test_redirects_share_one_total_operation_deadline(monkeypatch):
    module = load_module()
    now = [100.0]
    calls = []

    monkeypatch.setattr(module.time, "monotonic", lambda: now[0])

    def delayed_redirect(url, _deadline):
        calls.append(url)
        now[0] += module.MAXIMUM_OPERATION_SECONDS + 1
        return 302, {"location": "https://example.com/next"}, b""

    monkeypatch.setattr(module, "_read_response", delayed_redirect)

    with pytest.raises(ValueError, match="deadline"):
        module._fetch_public_https_in_process("https://example.com/start")
    assert calls == ["https://example.com/start"]


def test_fetch_subprocess_enforces_total_deadline_without_leaking_worker(monkeypatch):
    module = load_module()
    monkeypatch.setattr(module, "MAXIMUM_OPERATION_SECONDS", 0.01)
    monkeypatch.setattr(module, "_fetch_child_command", lambda _url: [
        module.sys.executable, "-c", "import time; time.sleep(30)"])

    with pytest.raises(ValueError, match="deadline"):
        module.fetch_public_https("https://example.com/news")


def test_document_extraction_cannot_finish_after_total_operation_deadline(monkeypatch):
    module = load_module()
    now = [100.0]
    monkeypatch.setattr(module.time, "monotonic", lambda: now[0])
    monkeypatch.setattr(module, "_read_response", lambda _url, _deadline: (
        200, {"content-type": "text/plain"}, b"document"))

    def delayed_extract(*_args):
        now[0] += module.MAXIMUM_OPERATION_SECONDS + 1
        return {"verifier_eligible": False}

    monkeypatch.setattr(module, "extract_document", delayed_extract)

    with pytest.raises(ValueError, match="deadline"):
        module._fetch_public_https_in_process("https://example.com/news")


def test_structured_output_has_one_aggregate_character_bound():
    module = load_module()
    first = "a" * 20_000
    second = "b" * 20_000
    html = (
        '<script type="application/ld+json">'
        '{"@type":"NewsArticle","description":"' + first + '","articleBody":"' + second + '"}'
        '</script>'
    ).encode()
    result = module.extract_document("https://example.com/news", "text/html", html)
    assert sum(map(len, result["structured_article_text"])) == 8_000
    assert all(len(value.encode("utf-16-le")) // 2 <= 4_000
               for value in result["structured_article_text"])


@pytest.mark.parametrize("candidate", ["x" * 4_001, "😀" * 2_001])
def test_each_evidence_candidate_fits_decision_parser_utf16_bound(candidate):
    module = load_module()
    payload = json.dumps({
        "@type": "NewsArticle",
        "datePublished": "2026-08-19T08:00:00Z",
        "description": candidate,
    })
    document = (
        '<html><head><link rel="stylesheet" href="/site.css">'
        f'<script type="application/ld+json">{payload}</script></head></html>'
    ).encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["verifier_eligible"] is True
    assert len(result["evidence_candidates"]) == 1
    assert len(result["evidence_candidates"][0].encode("utf-16-le")) // 2 <= 4_000


def test_external_css_body_text_is_discovery_only():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <meta property="article:published_time" content="2026-08-19T08:00:00Z"></head>
    <body><p>Ordinary body catalyst.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert "Ordinary body catalyst." in result["discovery_text"]
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False
    assert result["ineligibility_reason"] == "external-stylesheet-without-structured-article-text"


def test_external_css_structured_article_is_verifier_eligible():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>
    </head><body><p>Discovery only.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == ["Structured catalyst."]
    assert result["verifier_publication_time"] == "2026-08-19T08:00:00Z"
    assert result["verifier_eligible"] is True
    assert result["ineligibility_reason"] is None


def test_conflicting_verifier_timestamps_make_source_ineligible():
    module = load_module()
    document = b'''<html><head>
    <meta property="article:published_time" content="2026-08-19T08:00:00Z">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T09:00:00Z","description":"Structured catalyst."}</script>
    </head><body><p>Discovery only.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_publication_time"] is None
    assert result["verifier_eligible"] is False
    assert result["ineligibility_reason"] == "ambiguous-publication-time"


def test_html_without_css_exposes_verifier_aligned_visible_text():
    module = load_module()
    document = b'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><p>Visible catalyst.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["discovery_text"] == "Visible catalyst."
    assert result["evidence_candidates"] == ["Visible catalyst."]
    assert result["verifier_publication_time"] == "2026-08-19T08:00:00Z"
    assert result["verifier_eligible"] is True
    assert result["ineligibility_reason"] is None


def test_html_without_css_normalizes_verifier_whitespace():
    module = load_module()
    document = b'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><p>Visible\n catalyst.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == ["Visible catalyst."]
    assert result["verifier_eligible"] is True


def test_html_head_text_is_never_verifier_eligible_evidence():
    module = load_module()
    document = b'''<html><head><title>Head-only catalyst</title>
    <meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><p>Body catalyst.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == ["Body catalyst."]
    assert "Head-only catalyst" not in result["discovery_text"]


def test_title_text_is_never_verifier_eligible_even_when_misplaced():
    module = load_module()
    document = b'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><title>Misplaced title catalyst</title><p>Body catalyst.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == ["Body catalyst."]


def test_plain_html_rejects_double_encoded_entity_but_allows_literal_ampersand():
    module = load_module()
    metadata = '<meta name="datePublished" content="2026-08-19T08:00:00Z">'
    unstable = module.extract_document(
        "https://example.com/news", "text/html",
        f"<html><head>{metadata}</head><body><p>A &amp;amp; B</p></body></html>".encode(),
    )
    assert unstable["evidence_candidates"] == []
    assert unstable["verifier_eligible"] is False

    stable = module.extract_document(
        "https://example.com/news", "text/html",
        f"<html><head>{metadata}</head><body><p>A &amp; B</p></body></html>".encode(),
    )
    assert stable["evidence_candidates"] == ["A & B"]
    assert stable["verifier_eligible"] is True


def test_all_publication_metadata_is_checked_before_output_bounding():
    module = load_module()
    instant = datetime(2026, 8, 19, 8, tzinfo=timezone.utc)
    equivalent = "".join(
        f'<meta name="datePublished" content="{instant.astimezone(timezone(timedelta(hours=offset))).isoformat()}">'
        for offset in range(-9, 11)
    )
    document = (
        '<html><head>' + equivalent +
        '<meta name="datePublished" content="2026-08-20T08:00:00Z"></head>'
        '<body><p>Visible catalyst.</p></body></html>'
    ).encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert len(result["publication_metadata"]) == 20
    assert result["verifier_eligible"] is False
    assert result["ineligibility_reason"] == "ambiguous-publication-time"


def test_html_without_css_rejects_transform_unstable_visible_text():
    module = load_module()
    document = b'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><p>A\x1cB</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


@pytest.mark.parametrize("concealed", [
    '<p aria-hidden="true">Concealed catalyst.</p>',
    '<dialog>Concealed catalyst.</dialog>',
    '<details>Concealed catalyst.</details>',
])
def test_other_native_hidden_states_are_excluded(concealed):
    module = load_module()
    document = f'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body>{concealed}<p>Visible catalyst.</p></body></html>'''.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


@pytest.mark.parametrize("ancestor", [
    '<div hidden><div>nested hidden</div>LEAKED HIDDEN TEXT</div>',
    '<div aria-hidden="true"><div>nested hidden</div>LEAKED HIDDEN TEXT</div>',
    '<dialog><dialog open>nested hidden</dialog>LEAKED HIDDEN TEXT</dialog>',
    '<details><details open>nested hidden</details>LEAKED HIDDEN TEXT</details>',
])
def test_same_tag_descendant_cannot_end_hidden_ancestor(ancestor):
    module = load_module()
    document = f'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body>{ancestor}<p>Visible catalyst.</p></body></html>'''.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_unmatched_end_tag_cannot_end_hidden_ancestor():
    module = load_module()
    document = b'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><div hidden></span>LEAKED HIDDEN TEXT</div><p>Visible catalyst.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_out_of_scope_end_tag_cannot_create_eligible_evidence():
    module = load_module()
    document = b'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body><div hidden><table></div>LEAKED HIDDEN TEXT</table></div></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_malformed_recognized_json_ld_fails_closed():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <meta name="datePublished" content="2026-08-19T08:00:00Z">
    <script type="application/ld+json">{malformed</script></head><body>Visible catalyst.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False
    assert result["ineligibility_reason"] == "malformed-structured-metadata"


def test_css_rel_tokenization_is_not_broader_than_verifier():
    module = load_module()
    document = b'''<html><head><link rel="alternate\tstylesheet" href="/site.css">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


@pytest.mark.parametrize("markup", [
    '<link rel="alternate" rel="stylesheet" href="/site.css">',
    '<link rel="stylesheet" href="/site.css"><script type="text/plain" type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>',
])
def test_duplicate_html_attributes_keep_first_value_like_verifier(markup):
    module = load_module()
    structured = '' if '<script' in markup else '<script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>'
    document = f'<html><head>{markup}{structured}</head><body>Discovery only.</body></html>'.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


@pytest.mark.parametrize("head", [
    '<link rel rel="stylesheet" href="/site.css"><script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>',
    '<link rel="stylesheet" href="/site.css"><script type type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>',
    '<link rel="stylesheet" href="/site.css"><meta property name="datePublished" content="2026-08-19T08:00:00Z"><script type="application/ld+json">{"@type":"NewsArticle","description":"Structured catalyst."}</script>',
])
def test_valueless_first_duplicate_attribute_matches_verifier(head):
    module = load_module()
    document = f'<html><head>{head}</head><body>Discovery only.</body></html>'.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_meta_key_precedence_matches_verifier_null_coalescing():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <meta property="" name="datePublished" content="2026-08-19T08:00:00Z">
    <script type="application/ld+json">{"@type":"NewsArticle","description":"Structured catalyst."}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


@pytest.mark.parametrize("published", [
    "2026-08-19T08:00:00.12345678Z",
    "2026-08-19T08:00:00+15:00",
])
def test_publication_time_parser_is_a_conservative_verifier_subset(published):
    module = load_module()
    document = f'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="application/ld+json">{{"@type":"NewsArticle","datePublished":"{published}","description":"Structured catalyst."}}</script>
    </head><body>Discovery only.</body></html>'''.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_distinct_seventh_fractional_digits_remain_ambiguous():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <meta name="datePublished" content="2026-08-19T08:00:00.1234567Z">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00.1234568Z","description":"Structured catalyst."}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_structured_json_text_with_html_entity_is_not_evidence_eligible():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"A &amp; B"}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_structured_json_text_with_python_only_whitespace_is_not_evidence_eligible():
    module = load_module()
    document = br'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"A\u001cB"}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


def test_python_only_whitespace_at_candidate_edge_is_not_stripped_into_eligibility():
    module = load_module()
    document = br'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"\u001cA"}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False


@pytest.mark.parametrize(("content_type", "body"), [
    (
        "text/html; charset=utf-8",
        b'<link rel="stylesheet"><script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"A\xffB"}</script>',
    ),
    (
        "text/html; charset=definitely-not-an-encoding",
        b'<link rel="stylesheet"><script type="application/ld+json">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"AB"}</script>',
    ),
])
def test_invalid_or_unsupported_declared_encoding_fails_closed(content_type, body):
    module = load_module()
    with pytest.raises(ValueError, match="encoding is invalid or unsupported"):
        module.extract_document("https://example.com/news", content_type, body)


@pytest.mark.parametrize("payload", [
    '{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst.","bad":NaN}',
    '{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst.","deep":' + ('[' * 70) + '0' + (']' * 70) + '}',
    '{"@type":"NewsArticle","datePublished":"2026-08-19T07:00:00Z","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}',
])
def test_nonstandard_or_overdeep_json_ld_fails_closed(payload):
    module = load_module()
    document = f'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="application/ld+json">{payload}</script></head><body>Discovery only.</body></html>'''.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["evidence_candidates"] == []
    assert result["verifier_eligible"] is False
    assert result["ineligibility_reason"] == "malformed-structured-metadata"


@pytest.mark.parametrize("markup", [
    '<div popover>Text</div>',
    '<div style>Text</div>',
])
def test_valueless_visibility_attributes_fail_closed(markup):
    module = load_module()
    document = f'''<html><head><meta name="datePublished" content="2026-08-19T08:00:00Z"></head>
    <body>{markup}</body></html>'''.encode()
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["verifier_eligible"] is False
    assert result["evidence_candidates"] == []


def test_json_ld_mime_matching_is_not_broader_than_verifier():
    module = load_module()
    document = b'''<html><head><link rel="stylesheet" href="/site.css">
    <script type="APPLICATION/LD+JSON">{"@type":"NewsArticle","datePublished":"2026-08-19T08:00:00Z","description":"Structured catalyst."}</script>
    </head><body>Discovery only.</body></html>'''
    result = module.extract_document("https://example.com/news", "text/html", document)
    assert result["verifier_eligible"] is False
    assert result["evidence_candidates"] == []