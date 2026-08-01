using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Globalization;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;
using LectureExtraction.Refinement;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] AI-Studio-only implicit prefix-cache priming: loads the dummy-part0.tex anchor and sends
/// warm-up handshakes (single-shot or staged/batched) to pre-fill Google's implicit prefix cache before
/// real video processing begins. Split out of AiStudioAutoExtractionSession.cs (Phase 4.5).
/// Member Index:
/// - WarmUpWithBatchedHistoryAsync: Staged cache warming by grouping history files into batches.
/// - TryLoadSystemInstructionWithHistoryAsync: Loads system instruction text & preloads history.
/// [Human] Der Cache-Warmup-Teil der Session: dummy-part0.tex laden und Warmup-Handshakes senden.
/// </summary>
public partial class AiStudioAutoExtractionSession {
    /// <summary>
    /// [AI Context] Performs staged cache warming: splits history files into batches and sends
    /// incremental warm-up handshakes between each batch to pre-fill Google's implicit prefix cache.
    /// Each batch appends its text files to _systemInstructionText and uploads non-text files.
    /// [Human] Gestaffeltes Cache-Warming: History wird in Batches aufgeteilt, nach jedem Batch
    /// wird ein Handshake gesendet, um den Google-Cache schrittweise aufzubauen.
    /// </summary>
    private async Task<bool> WarmUpWithBatchedHistoryAsync(List<string> historyFiles, string? commonBase) {
        var batches = HistoryFileResolver.GroupHistoryFilesByTopLevelSubfolder(
            historyFiles, _config.HistoryPreloadPaths, _config.HistoryBatchCount);

        int systemInstructionDelay = _config.SystemInstructionDelaySeconds > 0 ? _config.SystemInstructionDelaySeconds : 65;
        int historyBatchDelay = _config.HistoryRateLimitDelaySeconds > 0 ? _config.HistoryRateLimitDelaySeconds : 65;

        Ui.Info($"Starte gestaffeltes Cache-Warming für System Instruction + History in {batches.Count} Batch(es) (BaseDelay: {systemInstructionDelay}s, HistoryDelay: {historyBatchDelay}s)...", "SystemInstruction-Warmup");

        // Step 0: Optionally warm up base system instruction before adding history
        if (!_config.MergeSystemInstructionAndFirstHistoryBatch) {
            Ui.Step("Cache-Warming Step 0: Warmup für Basis System Instruction");
            if (!await PrimePrefixCacheAsync(systemInstructionDelay, includeDummyPart0: false)) return false;
        } else {
            Ui.Info($"Überspringe separaten Warmup & Wartezeit ({systemInstructionDelay}s) für Basis System Instruction (wird mit erstem Batch vereint)...", "Cache-Warming");
        }

        // Process each history batch: append files, then warm up
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++) {
            var (batchLabel, batchFiles) = batches[batchIndex];
            bool isLastBatch = batchIndex == batches.Count - 1;

            Ui.Step($"Cache-Warming Step {batchIndex + 1}/{batches.Count}: Lade History-Batch '{batchLabel}' ({batchFiles.Count} Datei(en)) in System Instruction");

            // Append this batch's files to the growing system instruction text
            var batchBuilder = new System.Text.StringBuilder();
            _historyParts.AddRange(await SystemInstructionTextBuilder.AppendHistoryFilesAsync(
                batchFiles, batchBuilder, commonBase, _attachmentHandler));
            _systemInstructionText += batchBuilder.ToString();

            bool shouldSendHandshake = true;
            if (_config.MergeAllConsecutiveHistoryBatches && !isLastBatch) {
                int pairingStart = _config.MergeSystemInstructionAndFirstHistoryBatch ? 1 : 0;
                if (batchIndex >= pairingStart && (batchIndex - pairingStart) % 2 == 0) {
                    shouldSendHandshake = false;
                }
            }

            if (shouldSendHandshake) {
                // [AI Context] If MergeSystemInstructionAndFirstHistoryBatch is active, the first batch
                // logically represents the combined "system instruction + first history" round, so the
                // SystemInstructionDelaySeconds (not HistoryRateLimitDelaySeconds) should apply.
                bool isFirstMergedBatch = _config.MergeSystemInstructionAndFirstHistoryBatch && batchIndex == 0;
                int batchDelay = isFirstMergedBatch ? systemInstructionDelay : historyBatchDelay;
                if (!await PrimePrefixCacheAsync(batchDelay, includeDummyPart0: isLastBatch)) return false;
            } else {
                Ui.Info($"Überspringe Handshake & Wartezeit ({historyBatchDelay}s) für Batch '{batchLabel}' (wird mit dem nächsten Batch vereint)...", "Cache-Warming");
            }
        }

        Ui.Detail($"History-Warming abgeschlossen. Max-Frisch-Tokens in einem Schritt: {_sessionMaxFreshTokens:N0}", "Tokens");
        return true;
    }

    /// <summary>
    /// [AI Context] Sends a lightweight handshake request containing the System Instruction to Google AI Studio.
    /// This warms up Google's implicit prefix cache and enforces a token refill delay
    /// before heavy video processing begins, preventing Quota Errors and ensuring high cache hits.
    /// [Human] Wärme-Handshake: Sendet ein kleines Signal an Google, damit die KI die System Instruction vorab in den impliziten Cache laedt.
    /// </summary>
    private async Task<bool> PrimePrefixCacheAsync(int? customDelay = null, bool includeDummyPart0 = false) {
        Ui.Info("Starte initialen Handshake-Roundtrip, um die System Instruction bei Google im impliziten Cache zu aktivieren...", "Cache-Warming");

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        // [AI Context] ThinkingConfig is built with the same model-specific logic as BuildGenerationRequestAsync
        // so that the warmup handshake uses a bit-identical GenerateContentConfig — which is a prerequisite
        // for Google's implicit prefix cache to produce a hit on the real Part-1 request.
        // DisableThinkingDuringWarmUp=true overrides to ThinkingBudget=0 (opt-in, see finding F9).
        // [Human] Gleiche Thinking-Konfiguration wie Part 1+, damit der Warmup-Prefix gecacht wird.
        if (ModelCapabilities.SupportsThinking(_config.CurrentModel)) {
            if (_config.DisableThinkingDuringWarmUp) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 0 };
                Ui.Detail("Handshake läuft ohne Thinking (DisableThinkingDuringWarmUp = true).", "Cache-Warming");
            } else {
                bool isGemini25 = _config.CurrentModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
                if (!isGemini25 && !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
                } else if (_config.ThinkingBudget.HasValue) {
                    int budget = _config.ThinkingBudget.Value;
                    if (budget > 32768) budget = 32768;
                    requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = budget };
                }
            }
        }

        // [AI Context] The handshake text is explicitly phrased to suppress any content generation:
        // - No video or media is attached — the system instruction's processing rules do not apply.
        // - The model must ONLY echo the acknowledgement string, nothing else.
        string handshakeText = $"""[Cache-Warming Handshake] This is a technical warmup request to pre-fill the implicit prefix cache (on Google AI Studio servers). No video, audio, or any other media file is attached to this request — the lecture extraction instructions in the system instruction are therefore NOT applicable and must NOT be executed. Do not generate any LaTeX, summaries, or content of any kind. Please acknowledge receipt with exactly this string and nothing else: '[AI-Model: {_config.CurrentModel}] Handshake confirmed. Ready.'""";

        // [AI Context] Prefix-consistency rule: include dummy-part0.tex only when the system instruction
        // is COMPLETE (includeDummyPart0=true, i.e. last history batch or single-shot warmup). Intermediate
        // batches have a partial system instruction that cannot produce a cache hit for Part-1 anyway, so
        // sending the dummy there wastes ~4500 tokens with zero cache benefit.
        // SendDummyFileWithEachWarmUpRound=true overrides to always send (debugging/testing aid).
        // [Human] Dummy nur beim letzten Handshake (vollständige Sys-Instruction), sonst Tokenverbrauch ohne Nutzen.
        bool shouldIncludeDummy = (_config.DebugSendReferenceFile && includeDummyPart0) || _config.SendDummyFileWithEachWarmUpRound;
        List<Part> warmupParts;
        if (shouldIncludeDummy) {
            string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n";

            warmupParts = [
                new Part { Text = ReferenceContextPreamble + dummyReferenceBlock + GetStaticPromptBeginning(1) },
                new Part { Text = handshakeText }
            ];
        } else {
            warmupParts = [new Part { Text = handshakeText }];
        }

        var pingContent = new List<Content> {
            new() {
                Role = "user",
                Parts = warmupParts
            }
        };

        // [AI Context] Count tokens before request so that token count is visible even if a Quota Error occurs
        try {
            var warmupContents = new List<Content>();
            if (requestConfig.SystemInstruction != null) warmupContents.Add(requestConfig.SystemInstruction);
            warmupContents.AddRange(pingContent);
            var counted = await _client.Models.CountTokensAsync(_config.CurrentModel, warmupContents);
            int totalToks = counted.TotalTokens ?? 0;
            int estNew = _lastWarmupInputTokens > 0 ? Math.Max(0, totalToks - _lastWarmupInputTokens) : totalToks;
            Ui.Info($"[Warmup Request] Neu dazugekommene Tokens: {estNew:N0} | Total Prompt: {totalToks:N0} Tokens", "Tokens");
        }
        catch (Exception countEx) {
            Ui.Detail($"[Exception gefangen] {countEx.GetType().Name}: {countEx.Message}");
            // [AI Context] Even when CountTokens fails (e.g. network outage), display the last known
            // total so the user always sees a token count line before the actual generate request.
            string lastKnown = _lastWarmupInputTokens > 0 ? $"{_lastWarmupInputTokens:N0}" : "unbekannt";
            Ui.Info($"[Warmup Request] Token-Zählung nicht verfügbar (Netzwerkfehler). Letzter bekannter Total-Prompt: {lastKnown} Tokens (zzgl. neuer Batch)", "Tokens");
        }

        try {
            string responseText = "";
            int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
            var usage = new UsageReport();

            bool success = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, pingContent, requestConfig),
                onChunkReceived: async (chunk) => {
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    responseText += txt;
                    usage.Absorb(chunk.UsageMetadata);
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) inputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) outputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) cachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                    await Task.CompletedTask;
                },
                cancellationToken: CancellationToken.None,
                retryContext: "Cache-Warming Handshake"
            );

            if (success) {
                int freshTokens = Math.Max(0, inputTokens - cachedTokens);
                _sessionTotalInputTokens += inputTokens;
                _sessionTotalOutputTokens += outputTokens;
                _sessionTotalCachedTokens += cachedTokens;
                _sessionMaxFreshTokens = Math.Max(_sessionMaxFreshTokens, freshTokens);

                int newlyAdded = cachedTokens > 0 ? Math.Max(0, inputTokens - cachedTokens) : (_lastWarmupInputTokens > 0 ? Math.Max(0, inputTokens - _lastWarmupInputTokens) : freshTokens);
                _lastWarmupInputTokens = inputTokens;

                Ui.Success("Handshake erfolgreich.", "Cache-Warming");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Ui.Detail($"[Gemini Antwort] {responseText.Trim()}");
                }

                Ui.Info(usage.Describe($"[Warmup Tokens] Neu dazugekommen: {newlyAdded:N0} | Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Output: {outputTokens:N0}"), "Tokens");

                int delay = customDelay ?? (_config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130);
                Ui.Detail($"Warte {delay} Sekunden (Token Refill)...", "Rate-Limit");
                await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
                return true;
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Cache-Warming Handshake fehlgeschlagen: {ex.Message}. Fahre trotzdem fort.", "Cache-Warming");
            int delay = customDelay ?? (_config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130);
            Ui.Detail($"Warte {delay} Sekunden (Token Refill nach Handshake)...", "Rate-Limit");
            await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
        }
        return true;
    }
}
