using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using static System.Console;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] One streamed chat turn's usage, as reported by the server. Zero on a turn that was
/// cancelled before any usage metadata arrived.
/// [Human] Die Token-Zahlen einer einzelnen Antwort.
/// </summary>
public readonly record struct ChatTurnResult(string FullResponse, int InputTokens, int OutputTokens, int CachedTokens);

/// <summary>
/// [AI Context] Streams one chat turn to the console: the retry-wrapped streaming call, the
/// background key-press interceptor (so stray keystrokes during a long generation do not abort it or
/// queue up as the next prompt), grounding-source printing, cancellation handling, and per-request
/// plus running session token reporting.
///
/// <para>Both chat sessions carried this as ~170 near-identical lines. The differences turned out to
/// be two, and both are now explicit rather than buried: AI Studio waits 130 seconds before every
/// request (a quota guardrail Vertex does not need), which the caller supplies as
/// <c>beforeRequestAsync</c>; and the two copies disagreed about whether to print grounding sources
/// after an aborted turn. AI Studio's guard is kept - a cancelled or retry-exhausted turn prints
/// "abgebrochen", and listing sources underneath it is noise.</para>
///
/// <para>The running session totals live here, not in the sessions, because that is the only place
/// they were ever read. One instance per session; the client is passed per call, since AI Studio
/// replaces its own on <c>change-key</c>.</para>
/// [Human] Streamt eine Chat-Antwort inkl. Wiederholungen, Tastatur-Schutz, Quellenangaben und
/// Token-Bericht. Die Session-Summen liegen hier, weil sie nirgends sonst gelesen werden.
/// </summary>
public sealed class ResponseStreamPrinter {
    private int _sessionTotalInputTokens;
    private int _sessionTotalOutputTokens;
    private int _sessionTotalCachedTokens;

    /// <param name="beforeRequestAsync">
    /// Backend-specific step to run immediately before the request, returning false to abandon the
    /// turn. AI Studio uses it for its 130-second quota delay; Vertex passes null.
    /// </param>
    public async Task<ChatTurnResult> StreamAsync(
        Client client,
        string selectedModel,
        List<Content> apiContents,
        GenerateContentConfig config,
        Func<Task<bool>>? beforeRequestAsync = null) {
        string fullResponse = "";
        int inputTokens = 0;
        int outputTokens = 0;
        int cachedTokens = 0;

        bool exceptionCaught = false;
        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) {
            e.Cancel = true; // Verhindert das Beenden des Programms
            try { cts.Cancel(); } catch { }
        }
        Console.CancelKeyPress += cancelHandler;

        bool isGenerating = true;
        var inputInterceptorTask = Task.Run(async () => {
            while (isGenerating) {
                if (!InteractiveDelay.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
                    while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                    WriteLine("\n[AI-Model] Still waiting for the acknowledgment / response. Please wait...");
                }
                await Task.Delay(100);
            }
        });

        GroundingMetadata? accumulatedGrounding = null;

        try {
            if (beforeRequestAsync != null && !await beforeRequestAsync()) {
                exceptionCaught = true;
            }

            if (!exceptionCaught) {
                bool success = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                    streamFactory: () => client.Models.GenerateContentStreamAsync(model: selectedModel, contents: apiContents, config: config),
                    onChunkReceived: async (chunk) => {
                        string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                        Write(chunkText);
                        fullResponse += chunkText;

                        var metadata = chunk.Candidates?[0]?.GroundingMetadata;
                        if (metadata != null) {
                            accumulatedGrounding = metadata;
                        }

                        if (chunk.UsageMetadata != null) {
                            if (chunk.UsageMetadata.PromptTokenCount.HasValue) inputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                            if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) outputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                            if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) cachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                        }
                        await Task.CompletedTask;
                    },
                    cancellationToken: cts.Token,
                    maxRetries: 5,
                    retryContext: "Chat-Antwort"
                );
                if (!success) exceptionCaught = true;

                if (!exceptionCaught && accumulatedGrounding != null) {
                    PrintGroundingSources(accumulatedGrounding);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled") || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
            exceptionCaught = true;
        }
        finally {
            isGenerating = false;
            await inputInterceptorTask; // Warte kurz, bis der Input-Blocker sauber beendet ist
            Console.CancelKeyPress -= cancelHandler;

            if (inputTokens > 0 || outputTokens > 0) {
                _sessionTotalInputTokens += inputTokens;
                _sessionTotalOutputTokens += outputTokens;
                _sessionTotalCachedTokens += cachedTokens;
                WriteLine($"\n[Request Tokens]       Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {(Math.Max(0, inputTokens - cachedTokens)):N0} | Output: {outputTokens:N0} (inkl. Thinking Tokens)");
                WriteLine($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {(Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens)):N0} | Output: {_sessionTotalOutputTokens:N0}");
            }

            if (exceptionCaught || cts.IsCancellationRequested) {
                WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen.");
            }
            else {
                WriteLine();
            }
        }

        return new ChatTurnResult(fullResponse, inputTokens, outputTokens, cachedTokens);
    }

    /// <summary>
    /// [AI Context] Lists the web sources Google Search Grounding used for the answer.
    /// [Human] Zeigt die Quellen an, auf die sich die Antwort stützt.
    /// </summary>
    private static void PrintGroundingSources(GroundingMetadata grounding) {
        WriteLine("\n\n🔍 [Google Search Grounding] Quellen:");
        if (grounding.WebSearchQueries != null && grounding.WebSearchQueries.Count > 0) {
            WriteLine($"  Suchanfragen: {string.Join(", ", grounding.WebSearchQueries.Select(q => $"\"{q}\""))}");
        }
        if (grounding.GroundingChunks == null) return;

        int refIdx = 1;
        foreach (var chunkRef in grounding.GroundingChunks) {
            if (chunkRef.Web != null) {
                WriteLine($"   [{refIdx}] {chunkRef.Web.Title} - {chunkRef.Web.Uri} ({chunkRef.Web.Domain})");
                refIdx++;
            }
        }
    }
}
