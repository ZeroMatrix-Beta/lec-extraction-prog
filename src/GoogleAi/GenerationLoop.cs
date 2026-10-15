using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] What one stage tells <see cref="GenerationLoop"/>: the completion marker, the
/// continue-prompt wording, the per-request logging, the delays and the console labels. Everything
/// else - retries, usage, thought summaries, empty answers, the continue budget, the history - is the
/// loop's, so a fix to it lands in every stage at once (it used to be copied into four loops).
/// [Human] Was eine Stufe der gemeinsamen Streaming-Schleife vorgibt: Abschluss-Marker,
/// Continue-Prompt, Logging, Wartezeiten und Konsolen-Texte.
/// </summary>
public sealed class GenerationLoopOptions {
    /// <summary>Hard cap on requests, the first one included (see <see cref="ContinueBudget"/>).</summary>
    public required int MaxContinueRequests { get; init; }

    /// <summary>Shown before each request; gets the request number and the cap.</summary>
    public required Action<int, int> AnnounceRequest { get; init; }

    /// <summary>True when one request's answer text carries the stage's completion marker.</summary>
    public required Func<string, bool> IsComplete { get; init; }

    /// <summary>
    /// Builds the continue prompt from the answer that was cut short, and prints the stage's own
    /// lines about it. Called only when another request is allowed.
    /// </summary>
    public required Func<string, string> BuildContinuePrompt { get; init; }

    /// <summary>The wait between a continue prompt and its request; false (cancelled) ends the loop.</summary>
    public required Func<Task<bool>> WaitBeforeContinueAsync { get; init; }

    /// <summary>Appended to the budget's stop reason, e.g. "Breche dieses Refinement ab."</summary>
    public required string BudgetStopSuffix { get; init; }

    /// <summary>Retry context for <see cref="ApiRetryPolicy"/>'s messages.</summary>
    public required string RetryContext { get; init; }

    /// <summary>Printed before the exception when the retries gave up.</summary>
    public string UnrecoverableErrorPrefix { get; init; } = "Der Fehler konnte nicht durch einen automatischen Retry behoben werden: ";

    /// <summary>Whether a cancelled or failed stream is reported as a warning (else as info).</summary>
    public bool CancelledAsWarning { get; init; }

    /// <summary>The retry policy's first backoff; null keeps its default.</summary>
    public int? InitialBackoff { get; init; }

    /// <summary>The retry policy's wait when the model is in high demand; null keeps its default.</summary>
    public int? HighDemandDelay { get; init; }

    /// <summary>Runs before every request (a rate-limit wait); false ends the loop.</summary>
    public Func<Task<bool>>? BeforeRequestAsync { get; init; }

    /// <summary>Sees every chunk with its answer text, before the text is printed.</summary>
    public Action<GenerateContentResponse, string>? OnChunk { get; init; }

    /// <summary>Print a request's Google Search sources once its stream has ended.</summary>
    public bool ShowGroundingSources { get; init; }

    /// <summary>A completed request's token lines; called before its thoughts are written.</summary>
    public Action<GenerationRequest>? ReportRequest { get; init; }

    /// <summary>Called after a completed request's thoughts are written (Stage 1's chat log).</summary>
    public Func<GenerationRequest, Task>? AfterRequestAsync { get; init; }

    /// <summary>How often a blank answer is sent again before the loop gives up; 0 treats it like any answer.</summary>
    public int EmptyAnswerRetries { get; init; }

    /// <summary>The pause before a blank answer is sent again.</summary>
    public TimeSpan EmptyAnswerRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// One completed request: its answer text, its usage, the loop's total so far (this request
/// included), its grounding, and the continue prompt it answered (null for the first request).
/// </summary>
public sealed record GenerationRequest(int RequestNumber, string Text, UsageReport Usage, TokenUsage Total, GroundingMetadata? Grounding, string? ContinuePrompt);

/// <summary>The joined answer text of every request, and their summed usage.</summary>
public readonly record struct GenerationLoopResult(string Text, TokenUsage Usage);

/// <summary>
/// [AI Context] The one streaming request loop behind Stage 1 (AI Studio and Vertex), the refinement
/// steps and the PDF repair: stream with <see cref="ApiRetryPolicy"/>, sum the usage, write the
/// thought summaries, send a blank answer again, and send continue prompts until the stage's
/// completion marker arrives or the <see cref="ContinueBudget"/> runs out. The stages differ only in
/// what <see cref="GenerationLoopOptions"/> carries.
///
/// <para>Every completed request is billed, a blank one included, so each one is counted in the
/// usage. A request the loop gave up on mid-stream (an error, a cancel) is not counted, but its
/// thoughts are written, marked ABORTED.</para>
/// [Human] Die gemeinsame Streaming-Schleife aller Stufen: Retry, Tokens, Gedanken, leere Antworten
/// und Continue-Anfragen. Die Stufen unterscheiden sich nur in den Optionen.
/// </summary>
public static class GenerationLoop {
    /// <summary>
    /// The opening every continue prompt shares: the cut-off answer's tail, quoted back as an anchor,
    /// followed by the stage's <paramref name="instruction"/>.
    /// </summary>
    public static string CutShortPrompt(string response, string instruction, int echoCharacters = ContinueBudget.EchoLength) =>
        "[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
        $"{ContinueBudget.EchoTail(response, echoCharacters)}\n\n" +
        instruction;

    /// <param name="history">The conversation; each continue appends the model turn and the continue prompt to it.</param>
    /// <param name="generate">Starts one streamed request over the history as it is at that moment.</param>
    public static async Task<GenerationLoopResult> RunAsync(
        List<Content> history, Func<List<Content>, IAsyncEnumerable<GenerateContentResponse>> generate, ThoughtLog thoughts, GenerationLoopOptions options) {
        string fullText = "";
        TokenUsage total = default;
        var budget = new ContinueBudget(options.MaxContinueRequests);
        int emptyAnswerRetries = 0;
        string? continuePrompt = null;

        using var cancelScope = new ConsoleCancelScope();

        while (true) {
            if (options.BeforeRequestAsync != null && !await options.BeforeRequestAsync()) {
                break;
            }

            options.AnnounceRequest(budget.RequestNumber, budget.MaxTotal);

            string text = "";
            GroundingMetadata? grounding = null;
            var usage = new UsageReport();
            bool callSuccess;

            try {
                callSuccess = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                    streamFactory: () => generate(history),
                    onChunkReceived: chunk => {
                        string answer = thoughts.Absorb(chunk);
                        options.OnChunk?.Invoke(chunk, answer);
                        Ui.Raw(answer);
                        text += answer;

                        grounding = ResponseParts.Grounding(chunk) ?? grounding;

                        // Per request, the last chunk carrying usage wins; requests are summed below.
                        usage.Absorb(chunk.UsageMetadata);
                        usage.RecordFinishReason(ResponseParts.FinishReason(chunk));
                        return Task.CompletedTask;
                    },
                    cancellationToken: cancelScope.Token,
                    initialBackoff: options.InitialBackoff ?? 130,
                    retryContext: options.RetryContext,
                    onRetry: () => {
                        text = "";
                        grounding = null;
                        usage = new UsageReport();
                        thoughts.DiscardRequest();
                    },
                    highDemandDelay: options.HighDemandDelay,
                    resumeOnPartialProgress: true
                );
            }
            catch (Exception ex) {
                Ui.Error($"{options.UnrecoverableErrorPrefix}{ex.Describe()}", "Abbruch");
                break;
            }

            if (options.ShowGroundingSources && grounding != null) {
                PrintGroundingSources(grounding);
            }

            if (!callSuccess) {
                const string cancelled = "Generierung durch Benutzer abgebrochen oder fehlgeschlagen.";
                if (options.CancelledAsWarning) Ui.Warn(cancelled);
                else Ui.Info(cancelled);
                break;
            }

            // Every completed request is billed, a blank answer included.
            total += new TokenUsage(usage.PromptTokens, usage.CandidateTokens, usage.CachedTokens, usage.ThoughtTokens, Requests: 1);
            var request = new GenerationRequest(budget.RequestNumber, text, usage, total, grounding, continuePrompt);
            options.ReportRequest?.Invoke(request);
            await thoughts.EndRequestAsync(budget.RequestNumber, usage);
            if (options.AfterRequestAsync != null) {
                await options.AfterRequestAsync(request);
            }

            if (options.EmptyAnswerRetries > 0 && string.IsNullOrWhiteSpace(text)) {
                if (emptyAnswerRetries < options.EmptyAnswerRetries) {
                    emptyAnswerRetries++;
                    Ui.Error("Das Modell hat eine komplett leere Antwort zurückgegeben (z.B. wegen MALFORMED_RESPONSE oder Safety-Filtern).");
                    Ui.Detail($"Warte {options.EmptyAnswerRetryDelay.TotalSeconds:0} Sekunden vor Versuch {emptyAnswerRetries}/{options.EmptyAnswerRetries}...");
                    try {
                        await Task.Delay(options.EmptyAnswerRetryDelay, cancelScope.Token);
                    }
                    catch (OperationCanceledException) {
                        break;
                    }
                    continue;
                }
                Ui.Error($"Das Modell hat nach {options.EmptyAnswerRetries} Versuchen weiterhin eine komplett leere Antwort zurückgegeben. Der Vorgang wird abgebrochen.");
                break;
            }

            emptyAnswerRetries = 0;
            fullText += text;

            if (options.IsComplete(text)) {
                break;
            }

            if (!budget.TryContinue(text, out string stopReason)) {
                Ui.Warn($"{stopReason} {options.BudgetStopSuffix}");
                break;
            }

            continuePrompt = options.BuildContinuePrompt(text);
            history.Add(new Content { Role = "model", Parts = [new Part { Text = text }] });
            history.Add(new Content { Role = "user", Parts = [new Part { Text = continuePrompt }] });

            if (!await options.WaitBeforeContinueAsync()) {
                break;
            }
        }

        await thoughts.EndAbortedRequestAsync(budget.RequestNumber);
        return new GenerationLoopResult(fullText, total);
    }

    private static void PrintGroundingSources(GroundingMetadata grounding) {
        Ui.Info("Quellen (Google Search Grounding):");
        if (grounding.WebSearchQueries != null && grounding.WebSearchQueries.Count > 0) {
            Ui.Detail($"Suchanfragen: {string.Join(", ", grounding.WebSearchQueries.Select(q => $"\"{q}\""))}");
        }
        if (grounding.GroundingChunks != null) {
            int refIdx = 1;
            foreach (var chunkRef in grounding.GroundingChunks) {
                if (chunkRef.Web != null) {
                    Ui.Detail($"[{refIdx}] {chunkRef.Web.Title} - {chunkRef.Web.Uri}");
                    refIdx++;
                }
            }
        }
    }
}
