using System.Text.Json.Serialization;

namespace AiStocks.Api;

public sealed record InstrumentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("exchange")] string Exchange,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("price")] decimal Price,
    [property: JsonPropertyName("priceDkk")] decimal PriceDkk,
    [property: JsonPropertyName("isPreviewPrice")] bool IsPreviewPrice);

public sealed record InstrumentListDto(
    [property: JsonPropertyName("items")] IReadOnlyList<InstrumentDto> Items,
    [property: JsonPropertyName("dataMode")] string DataMode);

public sealed record HumanOrderRequestDto(
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("instrumentId")] string InstrumentId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("note")] string? Note = null);

public sealed record PreviewOrderDto(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("instrumentId")] string InstrumentId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("fillPriceDkk")] decimal FillPriceDkk,
    [property: JsonPropertyName("totalDkk")] decimal TotalDkk,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("filledAt")] DateTimeOffset FilledAt);

public sealed record OrderListDto(
    [property: JsonPropertyName("items")] IReadOnlyList<PreviewOrderDto> Items);

public sealed record PreviewHoldingDto(
    [property: JsonPropertyName("instrumentId")] string InstrumentId,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("priceDkk")] decimal PriceDkk,
    [property: JsonPropertyName("valueDkk")] decimal ValueDkk);

public sealed record PreviewPortfolioDto(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("startingCashDkk")] decimal StartingCashDkk,
    [property: JsonPropertyName("cashDkk")] decimal CashDkk,
    [property: JsonPropertyName("holdingsValueDkk")] decimal HoldingsValueDkk,
    [property: JsonPropertyName("totalValueDkk")] decimal TotalValueDkk,
    [property: JsonPropertyName("returnPercent")] decimal ReturnPercent,
    [property: JsonPropertyName("holdings")] IReadOnlyList<PreviewHoldingDto> Holdings,
    [property: JsonPropertyName("dataMode")] string DataMode);

public sealed record PreviewLeaderboardEntryDto(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("participantType")] string ParticipantType,
    [property: JsonPropertyName("valueDkk")] decimal ValueDkk,
    [property: JsonPropertyName("returnPercent")] decimal ReturnPercent);

public sealed record PreviewLeaderboardDto(
    [property: JsonPropertyName("items")] IReadOnlyList<PreviewLeaderboardEntryDto> Items,
    [property: JsonPropertyName("dataMode")] string DataMode);
