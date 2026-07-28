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
            await AppendHistoryFilesToInstructionAsync(batchFiles, batchBuilder, commonBase);
            _systemInstructionText += batchBuilder.ToString();

            bool shouldSendHandshake = true;
            if (_config.MergeAllConsecutiveHistoryBatches && !isLastBatch) {
                int pairingStart = _config.MergeSystemInstructionAndFirstHistoryBatch ? 1 : 0;
                if (batchIndex >= pairingStart && (batchIndex - pairingStart) % 2 == 0) {
                    shouldSendHandshake = false;
                }
            }

            if (shouldSendHandshake) {
                if (!await PrimePrefixCacheAsync(historyBatchDelay, includeDummyPart0: isLastBatch)) return false;
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
            MaxOutputTokens = 100
        };

        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        string handshakeText = $"[Cache-Warming Handshake] System instruction and instructions loaded. Please acknowledge with exactly: '[AI-Model: {_config.CurrentModel}] Handshake confirmed. Ready.'";

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


        try {
            string responseText = "";
            int inputTokens = 0, outputTokens = 0, cachedTokens = 0;

            bool success = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, pingContent, requestConfig),
                onChunkReceived: async (chunk) => {
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    responseText += txt;
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

                Ui.Success("Handshake erfolgreich.", "Cache-Warming");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Ui.Detail($"[Gemini Antwort] {responseText.Trim()}");
                }
                if (inputTokens > 0) {
                    Ui.Detail($"[Tokens] Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {freshTokens:N0} | Output: {outputTokens:N0}");
                }

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
