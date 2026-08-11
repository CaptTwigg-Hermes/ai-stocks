using System.Text.Json.Serialization;

namespace AiStocks.Api;

/// <summary>Stable API error payload for failed private API requests.</summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Title">Short human-readable error title.</param>
/// <param name="Status">HTTP status code.</param>
/// <param name="TraceId">Request trace identifier.</param>
/// <param name="Detail">Optional safe detail for the authenticated viewer.</param>
public sealed record ApiProblemDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("traceId")] string? TraceId,
    [property: JsonPropertyName("detail")] string? Detail = null);
