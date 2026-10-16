using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The shared streaming loop behind Stage 1, the refinement steps and the PDF repair, driven by fake
/// streams: what it sends, what it sums, what it writes to the thoughts file, and when it stops.
///
/// <para>The retry policy's waits are replaced by an instant fake and the console by a
/// <see cref="TestConsole"/>, so the class joins the console collection.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class GenerationLoopTests : IDisposable {
    private const string Marker = "% [SYSTEM] Refinement complete";

    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;
    private readonly Func<int, string, Task<bool>> _previousDelay = ApiRetryPolicy.DelayAsync;
    private readonly TestConsole _console = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("generation-loop-").FullName;

    public GenerationLoopTests() {
        AnsiConsole.Console = _console;
        ApiRetryPolicy.DelayAsync = (_, _) => Task.FromResult(true);
    }

    public void Dispose() {
        AnsiConsole.Console = _previousConsole;
        ApiRetryPolicy.DelayAsync = _previousDelay;
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task A_cut_off_answer_is_continued_until_the_marker_arrives() {
        var history = new List<Content> { UserTurn("transcribe") };
        var seen = new List<int>();
        var streams = new Queue<IAsyncEnumerable<GenerateContentResponse>>([
            Stream([Answer("first half"), Usage(100, 10, cached: 40, thoughts: 5)]),
            Stream([Answer($"second half\n{Marker}"), Usage(130, 20, cached: 0, thoughts: 7)]),
        ]);
        int waits = 0;

        var result = await GenerationLoop.RunAsync(history, h => { seen.Add(h.Count); return streams.Dequeue(); }, ThoughtLog.Inert(),
            Options(o => o with { WaitBeforeContinueAsync = () => { waits++; return Task.FromResult(true); } }));

        Assert.Equal($"first halfsecond half\n{Marker}", result.Text);
        Assert.Equal(GenerationEnd.Completed, result.End);
        Assert.Equal(new TokenUsageSnapshot(230, 30, 40, 12, 2), Snapshot(result));
        Assert.Equal(new[] { 1, 3 }, seen);
        Assert.Equal(1, waits);
        Assert.Equal(new[] { "user", "model", "user" }, history.Select(c => c.Role));
        Assert.Equal("first half", history[1].Parts![0].Text);
        Assert.Equal("continue after: first half", history[2].Parts![0].Text);
    }

    [Fact]
    public async Task A_blank_answer_is_sent_again_and_billed_but_not_added_to_the_history() {
        var history = new List<Content> { UserTurn("fix") };
        var streams = new Queue<IAsyncEnumerable<GenerateContentResponse>>([
            Stream([Thought("nothing to say"), Answer("  "), Usage(50, 0, 0, 3)]),
            Stream([Thought("now the answer"), Answer($"fixed\n{Marker}"), Usage(50, 9, 0, 4)]),
        ]);
        var thoughts = EnabledThoughts();

        var result = await GenerationLoop.RunAsync(history, _ => streams.Dequeue(), thoughts, Options(o => o with { EmptyAnswerRetries = 3 }));

        Assert.Equal($"fixed\n{Marker}", result.Text);
        Assert.Equal(2, result.Usage.Requests);
        Assert.Single(history);
        Assert.Contains("Warte 0 Sekunden vor Versuch 1/3", _console.Output);
        var records = ThoughtFile.Read(thoughts.FilePath!)!.Requests;
        Assert.Equal(new[] { (1, 1), (1, 2) }, records.Select(r => (r.Request, r.Attempt)));
    }

    [Fact]
    public async Task Without_empty_answer_retries_a_blank_answer_is_continued_like_any_other() {
        var history = new List<Content> { UserTurn("transcribe") };
        var streams = new Queue<IAsyncEnumerable<GenerateContentResponse>>([
            Stream([Answer("")]),
            Stream([Answer(Marker)]),
        ]);

        var result = await GenerationLoop.RunAsync(history, _ => streams.Dequeue(), ThoughtLog.Inert(), Options());

        Assert.Equal(Marker, result.Text);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public async Task A_retried_stream_drops_the_text_and_thoughts_of_the_failed_attempt() {
        var history = new List<Content> { UserTurn("refine") };
        var streams = new Queue<IAsyncEnumerable<GenerateContentResponse>>([
            Stream([Thought("from the failed attempt"), Answer("ab")], new ServerError("unavailable", 503, "UNAVAILABLE")),
            Stream([Thought("from the retry"), Answer($"whole\n{Marker}"), Usage(10, 2, 0, 1)]),
        ]);
        var thoughts = EnabledThoughts();

        var result = await GenerationLoop.RunAsync(history, _ => streams.Dequeue(), thoughts, Options());

        Assert.Equal($"whole\n{Marker}", result.Text);
        Assert.Equal(1, result.Usage.Requests);
        Assert.Equal("from the retry", ThoughtFile.Read(thoughts.FilePath!)!.Requests.Single().Text);
    }

    [Fact]
    public async Task A_stream_that_fails_for_good_writes_its_thoughts_as_aborted_and_bills_nothing() {
        var history = new List<Content> { UserTurn("refine") };
        var streams = new Queue<IAsyncEnumerable<GenerateContentResponse>>([
            Stream([Answer("first half"), Usage(100, 10, 0, 5)]),
            Stream([Thought("thinking when it died")], new ClientError("bad request", 400, "INVALID_ARGUMENT")),
        ]);
        var thoughts = EnabledThoughts();

        var result = await GenerationLoop.RunAsync(history, _ => streams.Dequeue(), thoughts, Options());

        Assert.Equal("first half", result.Text);
        Assert.Equal(GenerationEnd.Failed, result.End);
        Assert.False(result.IsUsable);
        Assert.Equal(1, result.Usage.Requests);
        var records = ThoughtFile.Read(thoughts.FilePath!)!.Requests;
        Assert.Equal(2, records.Count);
        var aborted = records[1];
        Assert.Equal((2, ThoughtLog.AbortedFinishReason, "thinking when it died"), (aborted.Request, aborted.FinishReason, aborted.Text));
        Assert.Contains("Der Fehler konnte nicht", _console.Output);
    }

    [Fact]
    public async Task The_marker_ends_the_loop_after_the_first_request() {
        var history = new List<Content> { UserTurn("refine") };
        int calls = 0;

        var result = await GenerationLoop.RunAsync(history, _ => { calls++; return Stream([Answer("done "), Answer(Marker)]); }, ThoughtLog.Inert(),
            Options(o => o with { WaitBeforeContinueAsync = () => throw new InvalidOperationException("no continue expected") }));

        Assert.Equal($"done {Marker}", result.Text);
        Assert.Equal(1, calls);
        Assert.Single(history);
    }

    [Fact]
    public async Task The_budget_stops_a_model_that_never_finishes() {
        var history = new List<Content> { UserTurn("refine") };
        int calls = 0;

        var result = await GenerationLoop.RunAsync(history, _ => { calls++; return Stream([Answer($"text {calls}")]); }, ThoughtLog.Inert(),
            Options(o => o with { MaxContinueRequests = 2 }));

        Assert.Equal(2, calls);
        Assert.Equal("text 1text 2", result.Text);
        Assert.Equal(GenerationEnd.BudgetExhausted, result.End);
        Assert.True(result.IsUsable);
        Assert.Contains("Maximale Anzahl an Requests (2) erreicht. Stop.", _console.Output);
    }

    [Fact]
    public async Task Blank_answers_past_the_retries_end_the_loop_as_unusable() {
        var history = new List<Content> { UserTurn("fix") };

        var result = await GenerationLoop.RunAsync(history, _ => Stream([Answer(" ")]), ThoughtLog.Inert(),
            Options(o => o with { EmptyAnswerRetries = 2 }));

        Assert.Equal(GenerationEnd.EmptyAnswers, result.End);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public async Task A_continue_wait_that_is_cancelled_leaves_an_unusable_fragment() {
        var history = new List<Content> { UserTurn("refine") };

        var result = await GenerationLoop.RunAsync(history, _ => Stream([Answer("first half")]), ThoughtLog.Inert(),
            Options(o => o with { WaitBeforeContinueAsync = () => Task.FromResult(false) }));

        Assert.Equal("first half", result.Text);
        Assert.Equal(GenerationEnd.Cancelled, result.End);
        Assert.False(result.IsUsable);
    }

    [Fact]
    public async Task Each_request_reports_its_usage_the_running_total_and_the_continue_prompt_it_answered() {
        var history = new List<Content> { UserTurn("transcribe") };
        var reported = new List<GenerationRequest>();
        var streams = new Queue<IAsyncEnumerable<GenerateContentResponse>>([
            Stream([Answer("one"), Usage(100, 10, 0, 0)]),
            Stream([Answer(Marker), Usage(120, 5, 0, 0)]),
        ]);

        await GenerationLoop.RunAsync(history, _ => streams.Dequeue(), ThoughtLog.Inert(),
            Options(o => o with { AfterRequestAsync = r => { reported.Add(r); return Task.CompletedTask; } }));

        Assert.Equal(new[] { (1, 100, 100, (string?)null), (2, 120, 220, "continue after: one") },
            reported.Select(r => (r.RequestNumber, r.Usage.PromptTokens, r.Total.Input, r.ContinuePrompt)));
    }

    [Fact]
    public async Task A_request_that_streams_nothing_for_a_while_says_it_is_still_waiting() {
        var history = new List<Content> { UserTurn("refine") };

        var result = await GenerationLoop.RunAsync(history, _ => Slow(TimeSpan.FromMilliseconds(400), Answer(Marker)), ThoughtLog.Inert(),
            new GenerationLoopOptions {
                MaxContinueRequests = 1,
                AnnounceRequest = (_, _) => { },
                IsComplete = text => text.Contains(Marker),
                BuildContinuePrompt = text => text,
                WaitBeforeContinueAsync = () => Task.FromResult(true),
                BudgetStopSuffix = "",
                RetryContext = "test",
                SilenceNoticeInterval = TimeSpan.FromMilliseconds(100),
            });

        Assert.Equal(Marker, result.Text);
        Assert.Contains("Noch keine Daten vom Server", _console.Output);
    }

    private static GenerationLoopOptions SingleRequest(TimeSpan firstChunkTimeout) => new() {
        MaxContinueRequests = 1,
        AnnounceRequest = (_, _) => { },
        IsComplete = text => text.Contains(Marker),
        BuildContinuePrompt = text => text,
        WaitBeforeContinueAsync = () => Task.FromResult(true),
        BudgetStopSuffix = "",
        RetryContext = "test",
        FirstChunkTimeout = firstChunkTimeout,
    };

    [Fact]
    public async Task An_attempt_that_delivers_nothing_is_abandoned_and_sent_again() {
        var history = new List<Content> { UserTurn("refine") };
        int calls = 0;

        var result = await GenerationLoop.RunAsync(history,
            _ => ++calls == 1 ? Slow(TimeSpan.FromSeconds(5), Answer("never seen")) : Stream([Answer(Marker)]),
            ThoughtLog.Inert(), SingleRequest(TimeSpan.FromMilliseconds(100)));

        Assert.Equal(2, calls);
        Assert.Equal(Marker, result.Text);
        Assert.Contains("Keine Daten vom Server", _console.Output);
    }

    [Fact]
    public async Task A_stream_that_has_started_is_never_cut_by_the_first_chunk_timeout() {
        var history = new List<Content> { UserTurn("refine") };
        int calls = 0;

        var result = await GenerationLoop.RunAsync(history,
            _ => { calls++; return StartsThenPauses(Answer("begin "), TimeSpan.FromMilliseconds(400), Answer(Marker)); },
            ThoughtLog.Inert(), SingleRequest(TimeSpan.FromMilliseconds(100)));

        Assert.Equal(1, calls);
        Assert.Equal($"begin {Marker}", result.Text);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> StartsThenPauses(GenerateContentResponse first, TimeSpan pause, GenerateContentResponse second,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        yield return first;
        await Task.Delay(pause, cancellationToken);
        yield return second;
    }

    private static async IAsyncEnumerable<GenerateContentResponse> Slow(TimeSpan delay, GenerateContentResponse chunk,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await Task.Delay(delay, cancellationToken);
        yield return chunk;
    }

    [Fact]
    public void The_cut_short_prompt_quotes_the_tail_and_appends_the_instruction() {
        string prompt = GenerationLoop.CutShortPrompt(new string('x', 600) + "END", "Go on.", 10);

        Assert.Equal("[IMPORTANT] Your response was cut short. Your last output ended with:\n\n...\nxxxxxxxEND\n\nGo on.", prompt);
    }

    private ThoughtLog EnabledThoughts() =>
        new(new ThoughtSummaryConfig { Enabled = true, ToConsole = false, ToSeparateFile = true }, "final", "Final", "m", Path.Combine(_folder, "out.tex"), null);

    /// <summary>A record copy of the options, so a test can change one member with <c>with</c>.</summary>
    private sealed record TestOptions(int MaxContinueRequests, Func<Task<bool>> WaitBeforeContinueAsync, int EmptyAnswerRetries,
        Func<GenerationRequest, Task>? AfterRequestAsync);

    private static GenerationLoopOptions Options(Func<TestOptions, TestOptions>? change = null) {
        var o = new TestOptions(10, () => Task.FromResult(true), 0, null);
        if (change != null) o = change(o);
        return new GenerationLoopOptions {
            MaxContinueRequests = o.MaxContinueRequests,
            AnnounceRequest = (n, max) => { },
            IsComplete = text => text.Contains(Marker, StringComparison.OrdinalIgnoreCase),
            BuildContinuePrompt = text => $"continue after: {text}",
            WaitBeforeContinueAsync = o.WaitBeforeContinueAsync,
            BudgetStopSuffix = "Stop.",
            RetryContext = "test",
            EmptyAnswerRetries = o.EmptyAnswerRetries,
            EmptyAnswerRetryDelay = TimeSpan.Zero,
            AfterRequestAsync = o.AfterRequestAsync,
        };
    }

    private sealed record TokenUsageSnapshot(int Input, int Output, int Cached, int Thinking, int Requests);

    private static TokenUsageSnapshot Snapshot(GenerationLoopResult r) =>
        new(r.Usage.Input, r.Usage.Output, r.Usage.Cached, r.Usage.Thinking, r.Usage.Requests);

    private static Content UserTurn(string text) => new() { Role = "user", Parts = [new Part { Text = text }] };

    private static GenerateContentResponse Answer(string text) => ResponsePartsTests.Chunk((text, false));

    private static GenerateContentResponse Thought(string text) => ResponsePartsTests.Chunk((text, true));

    private static GenerateContentResponse Usage(int prompt, int output, int cached, int thoughts) => new() {
        UsageMetadata = new GenerateContentResponseUsageMetadata {
            PromptTokenCount = prompt, CandidatesTokenCount = output, CachedContentTokenCount = cached, ThoughtsTokenCount = thoughts
        }
    };

    private static async IAsyncEnumerable<GenerateContentResponse> Stream(GenerateContentResponse[] chunks, Exception? thenThrow = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        foreach (var chunk in chunks) {
            await Task.Yield();
            yield return chunk;
        }
        if (thenThrow != null) {
            throw thenThrow;
        }
    }
}
