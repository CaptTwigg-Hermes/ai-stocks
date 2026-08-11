using Microsoft.AspNetCore.Http;

namespace AiStocks.Api;

/// <summary>Small helpers for returning stable API payloads from minimal endpoints.</summary>
public static class ApiEndpointResults
{
    /// <summary>Returns a JSON problem payload with the supplied stable code and HTTP status.</summary>
    public static IResult Problem(string code, string title, int statusCode, HttpContext context, string? detail = null) =>
        Results.Json(new ApiProblemDto(code, title, statusCode, context.TraceIdentifier, detail), statusCode: statusCode);

    /// <summary>Returns a fail-closed 503 response for unavailable read-side dependencies.</summary>
    public static IResult Unavailable(HttpContext context, string title = "API unavailable") =>
        Problem("api-unavailable", title, StatusCodes.Status503ServiceUnavailable, context);
}
