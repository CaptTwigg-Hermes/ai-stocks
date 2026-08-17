(() => {
  "use strict";

  const dataMode = "official-nasdaq-xsto-15m-delayed";
  const executionMode = "assumed-delayed-paper-fills-v1";
  const modelIds = new Set([
    "gpt-5.6-sol",
    "claude-opus-4.8",
    "claude-sonnet-5",
    "gemini-3.1-pro-preview"
  ]);

  function isResponse(data) {
    if (!data || data.dataMode !== dataMode || data.executionMode !== executionMode ||
      data.strictContest !== false || data.isNonLive !== true || data.holdOnly !== false ||
      data.assumedFills !== true || data.assumedSekToDkk !== 0.65 ||
      data.assumedSlippagePercent !== 1 || !Array.isArray(data.participants) ||
      data.participants.length !== 4) return false;
    if (!data.participants.every((participant) => participant &&
      participant.portfolio?.dataMode === dataMode &&
      participant.portfolio?.executionMode === executionMode)) return false;
    const responseModelIds = new Set(data.participants.map((participant) => participant.modelId));
    return responseModelIds.size === 4 && [...modelIds].every((modelId) => responseModelIds.has(modelId));
  }

  window.aiStocksExhibitionContract = Object.freeze({ dataMode, executionMode, isResponse });
})();
