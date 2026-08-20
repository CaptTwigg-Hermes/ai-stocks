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
    [property: JsonPropertyName("priceDkk"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? PriceDkk,
    [property: JsonPropertyName("isPreviewPrice")] bool IsPreviewPrice,
    [property: JsonPropertyName("executedAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ExecutedAt = null,
    [property: JsonPropertyName("availableAt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? AvailableAt = null,
    [property: JsonPropertyName("source"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonPropertyName("delayMinutes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DelayMinutes = null,
    [property: JsonPropertyName("tradable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Tradable = null,
    [property: JsonPropertyName("paperTradable"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? PaperTradable = null);

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
    [property: JsonPropertyName("valueDkk")] decimal ValueDkk,
    [property: JsonPropertyName("averageBuyPriceDkk")] decimal AverageBuyPriceDkk = 0m,
    [property: JsonPropertyName("costBasisDkk")] decimal CostBasisDkk = 0m,
    [property: JsonPropertyName("gainDkk")] decimal GainDkk = 0m,
    [property: JsonPropertyName("gainPercent")] decimal GainPercent = 0m);

public sealed record PreviewPortfolioDto(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("startingCashDkk")] decimal StartingCashDkk,
    [property: JsonPropertyName("cashDkk")] decimal CashDkk,
    [property: JsonPropertyName("holdingsValueDkk")] decimal HoldingsValueDkk,
    [property: JsonPropertyName("totalValueDkk")] decimal TotalValueDkk,
    [property: JsonPropertyName("returnPercent")] decimal ReturnPercent,
    [property: JsonPropertyName("holdings")] IReadOnlyList<PreviewHoldingDto> Holdings,
    [property: JsonPropertyName("dataMode")] string DataMode,
    [property: JsonPropertyName("executionMode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutionMode = null);

public sealed record PreviewLeaderboardEntryDto(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("participantType")] string ParticipantType,
    [property: JsonPropertyName("valueDkk")] decimal ValueDkk,
    [property: JsonPropertyName("returnPercent")] decimal ReturnPercent);

public sealed record PreviewLeaderboardDto(
    [property: JsonPropertyName("items")] IReadOnlyList<PreviewLeaderboardEntryDto> Items,
    [property: JsonPropertyName("dataMode")] string DataMode);

public sealed record AiProgressDto(
    [property: JsonPropertyName("participants")] IReadOnlyList<AiProgressAgentDto> Participants,
    [property: JsonPropertyName("activity")] IReadOnlyList<AiActivityDto> Activity,
    [property: JsonPropertyName("dataMode")] string DataMode,
    [property: JsonPropertyName("isNonLive")] bool IsNonLive,
    [property: JsonPropertyName("strictContest")] bool StrictContest,
    [property: JsonPropertyName("holdOnly")] bool HoldOnly,
    [property: JsonPropertyName("executionMode")] string? ExecutionMode = null,
    [property: JsonPropertyName("assumedFills")] bool AssumedFills = false,
    [property: JsonPropertyName("assumedSekToDkk")] decimal? AssumedSekToDkk = null,
    [property: JsonPropertyName("assumedSlippagePercent")] decimal? AssumedSlippagePercent = null,
    [property: JsonPropertyName("performance")] IReadOnlyList<PerformanceSeriesDto>? Performance = null);

public sealed record PerformancePointDto(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("valueDkk")] decimal ValueDkk);

public sealed record PerformanceSeriesDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("points")] IReadOnlyList<PerformancePointDto> Points);

public sealed record AiProgressAgentDto(
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("queuedAt")] DateTimeOffset? QueuedAt,
    [property: JsonPropertyName("startedAt")] DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completedAt")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("portfolio")] PreviewPortfolioDto Portfolio,
    [property: JsonPropertyName("latestDecision")] AiDecisionDto? LatestDecision);

public sealed record AiStatusRequestDto(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt);

public sealed record AiActivityDto(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt);

public sealed record AiDecisionDto(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("instrumentId")] string? InstrumentId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("evidence")] IReadOnlyList<AiEvidenceDto> Evidence,
    [property: JsonPropertyName("attestation")] AiAttestationDto Attestation,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("assumedPaperFill")] AiAssumedPaperFillDto? AssumedPaperFill = null);

public sealed record AiAssumedPaperFillDto(
    [property: JsonPropertyName("observedPriceSek")] decimal ObservedPriceSek,
    [property: JsonPropertyName("assumedSekToDkk")] decimal AssumedSekToDkk,
    [property: JsonPropertyName("assumedSlippagePercent")] decimal AssumedSlippagePercent,
    [property: JsonPropertyName("fillPriceDkk")] decimal FillPriceDkk,
    [property: JsonPropertyName("totalDkk")] decimal TotalDkk,
    [property: JsonPropertyName("observationExecutedAt")] DateTimeOffset ObservationExecutedAt,
    [property: JsonPropertyName("observationAvailableAt")] DateTimeOffset ObservationAvailableAt,
    [property: JsonPropertyName("filledAt")] DateTimeOffset FilledAt,
    [property: JsonPropertyName("executionMode")] string ExecutionMode);

public sealed record AiEvidenceDto(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("exactExcerpt")] string ExactExcerpt,
    [property: JsonPropertyName("contentSha256")] string ContentSha256);

public sealed record AiAttestationDto(
    [property: JsonPropertyName("runtimeProvider")] string RuntimeProvider,
    [property: JsonPropertyName("runtimeModel")] string RuntimeModel,
    [property: JsonPropertyName("reportSha256")] string ReportSha256);

public sealed record AiDecisionRequestDto(
    [property: JsonPropertyName("runId")] string RunId,
    [property: JsonPropertyName("agentId")] Guid AgentId,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("instrumentId")] string? InstrumentId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("confidence")] decimal Confidence,
    [property: JsonPropertyName("evidence")] IReadOnlyList<AiEvidenceDto> Evidence,
    [property: JsonPropertyName("runtimeProvider")] string RuntimeProvider,
    [property: JsonPropertyName("runtimeModel")] string RuntimeModel,
    [property: JsonPropertyName("reportSha256")] string ReportSha256,
    [property: JsonPropertyName("completedAt")] DateTimeOffset CompletedAt,
    [property: JsonPropertyName("observedPriceSek")] decimal? ObservedPriceSek = null,
    [property: JsonPropertyName("observationAvailableAt")] DateTimeOffset? ObservationAvailableAt = null);
