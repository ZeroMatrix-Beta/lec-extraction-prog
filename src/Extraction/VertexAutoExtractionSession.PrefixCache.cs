using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Implicit prefix-cache warm-up for Vertex, ported from AiStudioAutoExtractionSession.
/// Member Index:
/// - GetStaticPromptBeginning: Returns the static per-part prompt preamble.
/// - WarmUpSystemInstructionCacheAsync: Sends warm-up handshake using dummy-part0.tex.
/// - TryLoadSystemInstructionWithHistoryAsync: Loads system instruction text & triggers warmup if enabled.
/// [Human] Der von AI Studio portierte Cache-Warmup-Teil der Vertex-Session.
/// </summary>
public partial class VertexAutoExtractionSession {
    /// <summary>
    /// [AI Context] Returns the static, per-partNumber prefix of the user-turn prompt. This text is
    /// deterministic and placed BEFORE the video payload in every request, forming a stable, growing cache
    /// prefix that the warm-up can pre-activate in the same token order. Ported (2026-07-28) from the
    /// content Vertex's own UploadSegmentAndBuildPromptAsync already sent — merging_and_scope and segment_start
    /// are the exact same wording Vertex used before this port, just relocated from after the video to
    /// before it; nothing new was invented. partNumber == 1  → no segment_start parameter (matches the
    /// warm-up dummy turn exactly). partNumber  > 1 → adds the segment_start parameter for mid-lecture
    /// continuity.
    /// [Human] Gibt den immer gleichen statischen Anfang des Prompts zurück. Steht VOR dem Video, damit
    /// Google einen Cache-Hit erkennt. Portiert von AI Studio, Wortlaut von Vertex selbst übernommen.
    /// </summary>
    private static string GetStaticPromptBeginning(int partNumber) {
        string s = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n" +
                   "<context_and_parameters>\n" +
                   "IMPORTANT: The System Instructions (System Prompt) contain the absolute rules, syntax specifications, and constraints for this transcription and MUST be followed strictly. The parameters below only specify details for this video fragment:\n\n" +
                   "<parameter name=\"merging_and_scope\">Do NOT attempt to merge the current part with the previous parts (i.e. do not try to fix the cut). Focus solely on transcribing this fragment as it is. As specified in the System Instructions, keep mathematical derivations and explanations self-contained and grouped within 'math-stroke' environments to preserve logical flow.</parameter>\n";
        if (partNumber != 1) {
            s += "<parameter name=\"segment_start\">\n" +
                 "1. Start the transcription EXACTLY where the audio begins in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction.\n" +
                 "2. If the previous part ended in the middle of an environment (like a `proof`, `short-proof`, or `math-stroke`), you MUST logically continue that environment in this part (e.g., start with `\\begin{proof}` or `\\begin{math-stroke}` if the professor is still doing the proof/derivation). However, you must still transcribe the spoken words exactly from where this new video segment begins.\n" +
                 "</parameter>\n";
        }
        return s;
    }

    /// <summary>
    /// [AI Context] Sends a lightweight handshake containing the system instruction (and, unlike
    /// AI Studio, NOT the explicit CachedContent reference — that cache is created later, in
    /// InitializeContextCachingAsync, after this setup-time warmup has already run) to activate Google's
    /// implicit prefix cache for the stable per-part preamble before the first real video request. Ported
    /// (2026-07-28) from AiStudioAutoExtractionSession.PrimePrefixCacheAsync, simplified to a
    /// single-shot handshake (no batched-history variant, since Vertex has no equivalent batching config).
    /// [Human] Wärme-Handshake für Vertex (portiert von AI Studio, vereinfacht auf einen einzelnen
    /// Handshake ohne History-Batching).
    /// </summary>
    private async Task<bool> PrimePrefixCacheAsync() {
        Ui.Info("Starte initialen Handshake-Roundtrip, um den stabilen Prompt-Anfang bei Google im impliziten Cache zu aktivieren...", "Cache-Warming");

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = 100
        };

        var sysParts = new List<Part>();
        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
        if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) {
            sysParts.AddRange(_historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)));
        }
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        string handshakeText = $"[Cache-Warming Handshake] System instruction and instructions loaded. Please acknowledge with exactly: '[AI-Model: {_config.CurrentModel}] Handshake confirmed. Ready.'";

        string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n";
        var warmupParts = new List<Part> {
            new() { Text = dummyReferenceBlock + GetStaticPromptBeginning(1) },
            new() { Text = handshakeText }
        };

        var pingContent = new List<Content> {
            new() { Role = "user", Parts = warmupParts }
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

                Ui.Success("Handshake erfolgreich.", "Cache-Warming");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Ui.Detail($"[Gemini Antwort] {responseText.Trim()}");
                }
                if (inputTokens > 0) {
                    Ui.Detail($"[Tokens] Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {freshTokens:N0} | Output: {outputTokens:N0}");
                }

                int delay = _config.RateLimitDelaySeconds > 0 ? _config.RateLimitDelaySeconds : 130;
                Ui.Detail($"Warte {delay} Sekunden (Token Refill)...", "Rate-Limit");
                await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
                return true;
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Cache-Warming Handshake fehlgeschlagen: {ex.Message}. Fahre trotzdem fort.", "Cache-Warming");
            int delay = _config.RateLimitDelaySeconds > 0 ? _config.RateLimitDelaySeconds : 130;
            Ui.Detail($"Warte {delay} Sekunden (Token Refill nach Handshake)...", "Rate-Limit");
            await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
        }
        return true;
    }
}
