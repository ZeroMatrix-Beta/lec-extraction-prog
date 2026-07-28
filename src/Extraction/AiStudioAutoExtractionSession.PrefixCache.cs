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

        Console.WriteLine($"\n  [SystemInstruction-Warmup] Starte gestaffeltes Cache-Warming für System Instruction + History in {batches.Count} Batch(es) (BaseDelay: {systemInstructionDelay}s, HistoryDelay: {historyBatchDelay}s)...");

        // Step 0: Optionally warm up base system instruction before adding history
        if (!_config.MergeSystemInstructionAndFirstHistoryBatch) {
            Console.WriteLine("\n  [Cache-Warming Step 0] Warmup für Basis System Instruction...");
            if (!await PrimePrefixCacheAsync(systemInstructionDelay, includeDummyPart0: false)) return false;
        } else {
            Console.WriteLine($"\n  [Cache-Warming] Überspringe separaten Warmup & Wartezeit ({systemInstructionDelay}s) für Basis System Instruction (wird mit erstem Batch vereint)...");
        }

        // Process each history batch: append files, then warm up
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++) {
            var (batchLabel, batchFiles) = batches[batchIndex];
            bool isLastBatch = batchIndex == batches.Count - 1;

            Console.WriteLine($"\n  [Cache-Warming Step {batchIndex + 1}/{batches.Count}] Lade History-Batch '{batchLabel}' ({batchFiles.Count} Datei(en)) in System Instruction...");

            // Append this batch's files to the growing system instruction text
            var batchBuilder = new System.Text.StringBuilder();
            await AppendHistoryFilesToInstructionAsync(batchFiles, batchBuilder, commonBase);
            _systemInstructionText += batchBuilder.ToString();

            // Decide whether to send a handshake for this batch.
            // If MergeSystemInstructionAndFirstHistoryBatch is true, batch 0 is sent immediately together
            // with the system instruction (its own handshake IS that merge, kept small and early on purpose
            // to start Google's prefix-cache warming ASAP without a large fresh-token spike) — it is not a
            // pairing candidate. Pairing among the remaining batches then starts fresh from there:
            // pairs are (0,1),(2,3),... normally, or (1,2),(3,4),... when batch 0 is excluded.
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
                Console.WriteLine($"  [Cache-Warming] Überspringe Handshake & Wartezeit ({historyBatchDelay}s) für Batch '{batchLabel}' (wird mit dem nächsten Batch vereint)...");
            }
        }

        Console.WriteLine($"\n  [Tokens] History-Warming abgeschlossen. Max-Frisch-Tokens in einem Schritt: {_sessionMaxFreshTokens:N0}");
        return true;
    }

    /// <summary>
    /// [AI Context] Sends a lightweight handshake request containing the System Instruction to Google AI Studio.
    /// This warms up Google's implicit prefix cache and enforces a token refill delay
    /// before heavy video processing begins, preventing Quota Errors and ensuring high cache hits.
    /// [Human] Wärme-Handshake: Sendet ein kleines Signal an Google, damit die KI die System Instruction vorab in den impliziten Cache laedt.
    /// </summary>
    private async Task<bool> PrimePrefixCacheAsync(int? customDelay = null, bool includeDummyPart0 = false) {
        Console.WriteLine("\n  [Cache-Warming] Starte initialen Handshake-Roundtrip, um die System Instruction bei Google im impliziten Cache zu aktivieren...");

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

        // [AI Context] When DebugSendReferenceFile is enabled, the warm-up user-turn is split into TWO Parts:
        //   Part 0: contextText + dummyPart0 + GetStaticPromptBeginning(1)  ← IDENTICAL to Part 1's pre-video text Part
        //   Part 1: handshake instruction (throwaway, the response doesn't matter)
        // This Part boundary after Part 0 mirrors the boundary that exists in the real Part-1 request (between
        // the pre-video text and the video attachment), so Google's implicit prefix cache can match the full
        // SysInstruction + Part-0-text sequence and cache it before the first real video request arrives.
        // When SendDummyFileWithEachWarmUpRound is true, the dummy block is included in EVERY warm-up round
        // (regardless of includeDummyPart0 and DebugSendReferenceFile) to give Google a consistent user-turn
        // structure across all warm-up rounds, improving cache association.
        bool shouldIncludeDummy = (_config.DebugSendReferenceFile && includeDummyPart0) || _config.SendDummyFileWithEachWarmUpRound;
        List<Part> warmupParts;
        if (shouldIncludeDummy) {
            // [AI Context] dummy-part0.tex is ~4500 tokens of Lorem Ipsum – large enough to anchor Google's
            // implicit prefix cache on the user-turn portion even without relying solely on the system instruction.
            // This Part 0 is bit-identical to Part 1's pre-video text Part, ensuring maximum cache hits.
            string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n";

            // Part 0: pre-video prefix – token-identical to Part 1's first text Part.
            // Part 1: throwaway handshake – the response is irrelevant; only the cache priming matters.
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

                Console.WriteLine($"  [Cache-Warming] Handshake erfolgreich.");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Console.WriteLine($"  [Gemini Antwort] {responseText.Trim()}");
                }
                if (inputTokens > 0) {
                    Console.WriteLine($"  [Tokens] Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {freshTokens:N0} | Output: {outputTokens:N0}");
                }

                int delay = customDelay ?? (_config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130);
                Console.WriteLine($"  [Rate-Limit] Warte {delay} Sekunden (Token Refill)...");
                await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
                return true;
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"  [WARNUNG] Cache-Warming Handshake fehlgeschlagen: {ex.Message}. Fahre trotzdem fort.");
            int delay = customDelay ?? (_config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130);
            Console.WriteLine($"  [Rate-Limit] Warte {delay} Sekunden (Token Refill nach Handshake)...");
            await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
        }
        return true;
    }
}
