(() => {
  "use strict";

  const dataMode = "official-nasdaq-xsto-15m-delayed";
  const modelIds = new Set([
    "gpt-5.6-sol",
    "claude-opus-4.8",
    "claude-sonnet-5",
    "gemini-3.1-pro-preview"
  ]);

  function isResponse(data) {
    if (!data || data.dataMode !== dataMode || data.strictContest !== false ||
      data.isNonLive !== true || data.holdOnly !== true || !Array.isArray(data.participants) ||
      data.participants.length !== 4) return false;
    if (!data.participants.every((participant) => participant &&
      participant.portfolio?.dataMode === dataMode)) return false;
    const responseModelIds = new Set(data.participants.map((participant) => participant.modelId));
    return responseModelIds.size === 4 && [...modelIds].every((modelId) => responseModelIds.has(modelId));
  }

  window.aiStocksExhibitionContract = Object.freeze({ dataMode, isResponse });
})();
