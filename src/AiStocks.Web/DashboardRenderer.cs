using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace AiStocks.Web;

internal static class DashboardRenderer
{
    public static string Render(DashboardSnapshot data, ClaimsPrincipal user, AntiforgeryTokenSet tokens)
    {
        var output = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>AI Stocks — private dashboard</title><link rel=\"stylesheet\" href=\"/assets/dashboard.css\"></head><body><header><div><p class=\"eyebrow\">Private paper-trading contest</p><h1>AI Stocks</h1></div><div class=\"status\"><span class=\"dot\"></span>");
        Text(output, data.Status);
        output.Append(" · as of <time>"); Text(output, data.AsOf.ToString("u", CultureInfo.InvariantCulture)); output.Append("</time></div></header><main>");
        output.Append("<section><h2>Leaderboard</h2><div class=\"table-scroll\"><table><thead><tr><th>Rank</th><th>Model</th><th>Value</th><th>Return</th></tr></thead><tbody>");
        foreach (var row in data.Leaderboard) { output.Append("<tr><td>").Append(row.Rank).Append("</td><th scope=\"row\">"); Text(output, row.ModelId); output.Append("</th><td>").Append(Money(row.ValueSek)).Append("</td><td>").Append(row.ReturnPercent.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)).Append("%</td></tr>"); }
        output.Append("</tbody></table></div></section><section><h2>Performance chart</h2><div class=\"chart\" role=\"img\" aria-label=\"Portfolio value history in SEK\"><svg viewBox=\"0 0 600 180\" preserveAspectRatio=\"none\"><path class=\"grid\" d=\"M0 30H600M0 90H600M0 150H600\"/>");
        var index = 0;
        foreach (var row in data.Leaderboard) { var points = ChartPoints(row.History); output.Append("<polyline class=\"series s").Append(index++ % 4).Append("\" points=\""); Text(output, points); output.Append("\"><title>"); Text(output, row.ModelId); output.Append("</title></polyline>"); }
        output.Append("</svg></div></section>");
        SectionStart(output, "Portfolios"); foreach (var portfolio in data.Portfolios) { output.Append("<article class=\"portfolio\"><h3>"); Text(output, portfolio.ModelId); output.Append("</h3><p class=\"metric\">").Append(Money(portfolio.ValueSek)).Append("</p><p>Cash ").Append(Money(portfolio.CashSek)).Append("</p><ul>"); foreach (var holding in portfolio.Holdings) { output.Append("<li><strong>"); Text(output, holding.Symbol); output.Append("</strong> ").Append(holding.Quantity).Append(" shares · ").Append(Money(holding.ValueSek)).Append("</li>"); } output.Append("</ul></article>"); } SectionEnd(output);
        SectionStart(output, "Queued orders"); foreach (var row in data.QueuedOrders) Card(output, $"{row.Side} {row.Quantity} {row.Symbol}", row.ModelId, row.QueuedAt); SectionEnd(output);
        SectionStart(output, "Evidence timeline"); foreach (var row in data.Evidence) { output.Append("<article class=\"timeline-card\"><h3>"); Text(output, row.Symbol); output.Append(" — "); Text(output, row.Catalyst); output.Append("</h3><p>"); Text(output, row.ModelId); output.Append(" · <a rel=\"noreferrer noopener\" href=\""); Text(output, row.SourceUrl.AbsoluteUri); output.Append("\">source evidence</a></p><time>"); Text(output, row.DecisionAt.ToString("u", CultureInfo.InvariantCulture)); output.Append("</time></article>"); } SectionEnd(output);
        SectionStart(output, "Fees"); foreach (var row in data.Fees) Card(output, $"{Money(row.AmountSek)} · {row.Tier}", row.ModelId, row.At); SectionEnd(output);
        SectionStart(output, "Dividends"); foreach (var row in data.Dividends) Card(output, $"{row.Symbol} · {Money(row.AmountSek)}", row.ModelId, row.PaidAt); SectionEnd(output);
        SectionStart(output, "Failures"); foreach (var row in data.Failures) Card(output, $"{row.Code}: {row.Message}", row.ModelId, row.At); SectionEnd(output);
        SectionStart(output, "Audit history"); foreach (var row in data.Audit) Card(output, $"{row.Action}: {row.Reason}", row.Actor, row.At); SectionEnd(output);
        if (user.IsInRole("owner")) RenderControls(output, tokens, data);
        output.Append("</main><footer>Read-only contest reporting · paper trading only</footer></body></html>");
        return output.ToString();
    }

    private static void RenderControls(StringBuilder output, AntiforgeryTokenSet tokens, DashboardSnapshot data)
    {
        output.Append("<section class=\"controls\"><h2>Owner controls</h2><p>Lifecycle controls only. This dashboard cannot place, cancel, or edit trades or portfolios.</p><div class=\"control-grid\">");
        foreach (var (action, label, enabled) in new[] { ("start", "Start", data.Status.Equals("draft", StringComparison.OrdinalIgnoreCase)), ("pause", "Pause", !data.Paused), ("resume", "Resume", data.Paused), ("pre-start-reset", "Pre-start reset", data.Status.Equals("draft", StringComparison.OrdinalIgnoreCase)) })
        {
            output.Append("<form method=\"post\" action=\"/admin/").Append(action).Append("\"><input name=\"__RequestVerificationToken\" type=\"hidden\" value=\""); Text(output, tokens.RequestToken ?? string.Empty); output.Append("\"><label>Idempotency key<input required maxlength=\"128\" name=\"idempotencyKey\" autocomplete=\"off\"></label><button type=\"submit\""); if (!enabled) output.Append(" disabled"); output.Append('>'); Text(output, label); output.Append("</button></form>");
        }
        output.Append("</div></section>");
    }

    private static string ChartPoints(IReadOnlyList<ValuePoint> history)
    {
        if (history.Count == 0) return string.Empty;
        var min = history.Min(x => x.ValueSek); var max = history.Max(x => x.ValueSek); var range = Math.Max(1m, max - min);
        return string.Join(' ', history.Select((point, i) => $"{(history.Count == 1 ? 300 : i * 600 / (history.Count - 1))},{(int)(165 - ((point.ValueSek - min) / range * 150))}"));
    }

    private static void Card(StringBuilder output, string title, string detail, DateTimeOffset at) { output.Append("<article class=\"timeline-card\"><h3>"); Text(output, title); output.Append("</h3><p>"); Text(output, detail); output.Append("</p><time>"); Text(output, at.ToString("u", CultureInfo.InvariantCulture)); output.Append("</time></article>"); }
    private static void SectionStart(StringBuilder output, string name) { output.Append("<section><h2>"); Text(output, name); output.Append("</h2><div class=\"cards\">"); }
    private static void SectionEnd(StringBuilder output) => output.Append("</div></section>");
    private static string Money(decimal value) => $"{value.ToString("N2", CultureInfo.InvariantCulture)} SEK";
    private static void Text(StringBuilder output, string value) => output.Append(HtmlEncoder.Default.Encode(value));
}
