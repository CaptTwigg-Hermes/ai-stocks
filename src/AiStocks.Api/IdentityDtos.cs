using System.Text.Json.Serialization;

namespace AiStocks.Api;

/// <summary>Cloudflare Access identity visible to the private API.</summary>
/// <param name="Email">Normalized authenticated email address.</param>
/// <param name="Role">Application role such as viewer or owner.</param>
public sealed record IdentityDto(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role);
