using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Cli;
using LectureExtraction.Cli.Commands;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;
using File = System.IO.File;

namespace LectureExtraction.Tests;

/// <summary>
/// The thinking config every stage sends. Moving seven copies into one factory must not change what
/// a request without thought summaries looks like - in particular that Gemini 3 gets the level and
/// not the budget.
/// </summary>
public class ThinkingConfigFactoryTests {
    [Fact]
    public void A_model_without_thinking_gets_no_config_even_when_thoughts_are_asked_for() {
        Assert.Null(ThinkingConfigFactory.Build("gemma-3-27b", 4096, "HIGH", includeThoughts: true));
    }

    [Fact]
    public void Gemini3_gets_the_level_and_the_budget_is_not_sent() {
        var config = ThinkingConfigFactory.Build("gemini-3.8-flash", 4096, "MEDIUM");

        Assert.NotNull(config);
        Assert.Null(config!.ThinkingBudget);
        Assert.Null(config.IncludeThoughts);
    }

    [Fact]
    public void Gemini25_gets_the_budget_clamped_to_the_vertex_maximum() {
        var config = ThinkingConfigFactory.Build("gemini-2.5-pro", 50000, "HIGH");

        Assert.Equal(ThinkingConfigFactory.MaxThinkingBudget, config!.ThinkingBudget);
    }

    [Fact]
    public void Nothing_configured_and_no_thoughts_means_no_config() {
        Assert.Null(ThinkingConfigFactory.Build("gemini-3.8-flash", null, null));
    }

    [Fact]
    public void IncludeThoughts_is_added_to_the_level_config() {
        var config = ThinkingConfigFactory.Build("gemini-3.8-flash", 4096, "MEDIUM", includeThoughts: true);

        Assert.True(config!.IncludeThoughts);
        Assert.Null(config.ThinkingBudget);
    }

    [Fact]
    public void IncludeThoughts_alone_creates_a_config() {
        var config = ThinkingConfigFactory.Build("gemini-3.8-flash", null, null, includeThoughts: true);

        Assert.True(config!.IncludeThoughts);
    }
}

/// <summary>Thought text must never reach the answer: it would land in the .tex and in the continue history.</summary>
public class ResponsePartsTests {
    internal static GenerateContentResponse Chunk(params (string Text, bool Thought)[] parts) => new() {
        Candidates = [new Candidate {
            Content = new Content { Role = "model", Parts = [.. parts.Select(p => new Part { Text = p.Text, Thought = p.Thought ? true : null })] }
        }]
    };

    [Fact]
    public void FinishReason_is_null_for_an_empty_candidate_list() {
        Assert.Null(ResponseParts.FinishReason(new GenerateContentResponse { Candidates = [] }));
        Assert.Null(ResponseParts.FinishReason(null));
    }

    [Fact]
    public void Thought_parts_are_split_from_the_answer() {
        var (answer, thought) = ResponseParts.Split(Chunk(("I should check the timestamps.", true), (@"\begin{speech}", false)));

        Assert.Equal(@"\begin{speech}", answer);
        Assert.Equal("I should check the timestamps.", thought);
    }

    [Fact]
    public void Without_thought_parts_the_answer_is_every_text_part_in_order() {
        var (answer, thought) = ResponseParts.Split(Chunk(("a", false), ("b", false)));

        Assert.Equal("ab", answer);
        Assert.Equal("", thought);
    }

    [Fact]
    public void A_chunk_without_candidates_yields_nothing() {
        Assert.Equal(("", ""), ResponseParts.Split(new GenerateContentResponse()));
        Assert.Equal(("", ""), ResponseParts.Split(null));
    }
}

/// <summary>The file format is what <c>lecx thoughts</c> and any other agent parse, so it must round-trip.</summary>
public class ThoughtFileTests {
    private static readonly ThoughtFileHeader Header =
        new("speech", "Step 3/4 - Speech refinement", "step3-x-offset-speech_refined.tex", "gemini-3.8-flash", new DateTimeOffset(2026, 10, 13, 11, 2, 3, TimeSpan.FromHours(2)));

    [Fact]
    public void A_written_file_parses_back_to_the_same_requests() {
        var first = new ThoughtRequestRecord(1, Header.Started, 23456, 31000, "MAX_TOKENS", "**Drafting block 12**\n\n## A heading the model wrote\n\nCounting words: 118.");
        var second = new ThoughtRequestRecord(2, Header.Started.AddMinutes(3), 9000, 12000, "STOP", "");

        string text = ThoughtFile.FormatHeader(Header) + ThoughtFile.FormatRequest(first) + ThoughtFile.FormatRequest(second);
        var parsed = ThoughtFile.Parse("x.md", text);

        Assert.NotNull(parsed);
        Assert.Equal("speech", parsed!.Header.StageKey);
        Assert.Equal("gemini-3.8-flash", parsed.Header.Model);
        Assert.Equal(2, parsed.Requests.Count);
        Assert.Equal(first.Text, parsed.Requests[0].Text);
        Assert.Equal(23456, parsed.Requests[0].ThoughtTokens);
        Assert.Equal("MAX_TOKENS", parsed.Requests[0].FinishReason);
        Assert.Equal("", parsed.Requests[1].Text);
        Assert.Equal(9000, parsed.Requests[1].ThoughtTokens);
    }

    [Fact]
    public void A_file_without_the_marker_is_not_a_thoughts_file() {
        Assert.Null(ThoughtFile.Parse("notes.md", "# Some notes\n\ntext"));
    }

    [Fact]
    public void A_second_run_gets_a_new_file_instead_of_overwriting() {
        string folder = Directory.CreateTempSubdirectory("thoughts-path-").FullName;
        try {
            string output = Path.Combine(folder, "step3-x.tex");
            string first = ThoughtFile.ResolveNewPath(output);
            Assert.Equal(Path.Combine(folder, "step3-x-thoughts.md"), first);

            File.WriteAllText(first, "taken");
            Assert.Equal(Path.Combine(folder, "step3-x-thoughts-2.md"), ThoughtFile.ResolveNewPath(output));
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>The three targets, and that a disabled log changes nothing.</summary>
[Collection(ConsoleTestCollection.Name)]
public class ThoughtLogTests {
    private static UsageReport Usage(int thoughts, int output, string finish) {
        var usage = new UsageReport();
        usage.Absorb(new GenerateContentResponseUsageMetadata { ThoughtsTokenCount = thoughts, CandidatesTokenCount = output });
        usage.RecordFinishReason(finish);
        return usage;
    }

    private static async Task WithConsole(Func<TestConsole, Task> body) {
        var previous = AnsiConsole.Console;
        var console = new TestConsole();
        AnsiConsole.Console = console;
        try {
            await body(console);
        }
        finally {
            AnsiConsole.Console = previous;
        }
    }

    [Fact]
    public Task Disabled_it_returns_the_answer_and_writes_nothing() => WithConsole(async console => {
        string folder = Directory.CreateTempSubdirectory("thoughts-off-").FullName;
        try {
            var log = new ThoughtLog(new ThoughtSummaryConfig { Enabled = false }, "speech", "Speech", "m", Path.Combine(folder, "out.tex"), Path.Combine(folder, "log.md"));

            string answer = log.Absorb(ResponsePartsTests.Chunk(("secret", true), ("tex", false)));
            await log.EndRequestAsync(1, Usage(10, 20, "STOP"));

            Assert.Equal("tex", answer);
            Assert.Empty(Directory.GetFiles(folder));
            Assert.DoesNotContain("secret", console.Output);
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public Task Enabled_it_writes_the_file_the_existing_log_and_the_console() => WithConsole(async console => {
        string folder = Directory.CreateTempSubdirectory("thoughts-on-").FullName;
        try {
            string existingLog = Path.Combine(folder, "out.tex-prompt-log.md");
            File.WriteAllText(existingLog, "# SYSTEM INSTRUCTION\n");
            var settings = new ThoughtSummaryConfig { Enabled = true, ToConsole = true, ToExistingLog = true, ToSeparateFile = true };
            var log = new ThoughtLog(settings, "speech", "Speech", "gemini-3.8-flash", Path.Combine(folder, "out.tex"), existingLog);

            log.Absorb(ResponsePartsTests.Chunk(("Checking block 4.", true)));
            Assert.Equal("tex", log.Absorb(ResponsePartsTests.Chunk(("tex", false))));
            await log.EndRequestAsync(1, Usage(1234, 500, "MAX_TOKENS"));

            Assert.NotNull(log.FilePath);
            var parsed = ThoughtFile.Read(log.FilePath!);
            Assert.Equal("Checking block 4.", parsed!.Requests.Single().Text);
            Assert.Equal(1234, parsed.Requests.Single().ThoughtTokens);
            Assert.Contains("Checking block 4.", File.ReadAllText(existingLog));
            Assert.StartsWith("# SYSTEM INSTRUCTION", File.ReadAllText(existingLog));
            Assert.Contains("Checking block 4.", console.Output);
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public Task A_retried_request_drops_what_it_had_collected() => WithConsole(async _ => {
        string folder = Directory.CreateTempSubdirectory("thoughts-retry-").FullName;
        try {
            var settings = new ThoughtSummaryConfig { Enabled = true, ToConsole = false, ToSeparateFile = true };
            var log = new ThoughtLog(settings, "final", "Final", "m", Path.Combine(folder, "out.tex"), null);

            log.Absorb(ResponsePartsTests.Chunk(("from the failed attempt", true)));
            log.DiscardRequest();
            log.Absorb(ResponsePartsTests.Chunk(("from the retry", true)));
            await log.EndRequestAsync(1, Usage(1, 1, "STOP"));

            Assert.Equal("from the retry", ThoughtFile.Read(log.FilePath!)!.Requests.Single().Text);
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public Task An_empty_answer_sent_again_is_a_second_attempt_of_the_same_request() => WithConsole(async _ => {
        string folder = Directory.CreateTempSubdirectory("thoughts-attempt-").FullName;
        try {
            var settings = new ThoughtSummaryConfig { Enabled = true, ToConsole = false, ToSeparateFile = true };
            var log = new ThoughtLog(settings, "final", "Final", "m", Path.Combine(folder, "out.tex"), null);

            log.Absorb(ResponsePartsTests.Chunk(("first try", true)));
            await log.EndRequestAsync(1, Usage(1, 0, "MALFORMED_FUNCTION_CALL"));
            log.Absorb(ResponsePartsTests.Chunk(("second try", true)));
            await log.EndRequestAsync(1, Usage(2, 9, "STOP"));
            log.Absorb(ResponsePartsTests.Chunk(("the continue", true)));
            await log.EndRequestAsync(2, Usage(3, 9, "STOP"));

            var requests = ThoughtFile.Read(log.FilePath!)!.Requests;
            Assert.Equal(new[] { (1, 1), (1, 2), (2, 1) }, requests.Select(r => (r.Request, r.Attempt)));
            Assert.Equal("second try", requests[1].Text);
            Assert.Contains("Request 1 · attempt 2 ·", File.ReadAllText(log.FilePath!));
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public Task An_aborted_request_keeps_its_thoughts() => WithConsole(async _ => {
        string folder = Directory.CreateTempSubdirectory("thoughts-abort-").FullName;
        try {
            var settings = new ThoughtSummaryConfig { Enabled = true, ToConsole = false, ToSeparateFile = true };
            var log = new ThoughtLog(settings, "speech", "Speech", "m", Path.Combine(folder, "out.tex"), null);

            log.Absorb(ResponsePartsTests.Chunk(("done", true)));
            await log.EndRequestAsync(1, Usage(1, 1, "MAX_TOKENS"));
            await log.EndAbortedRequestAsync(2); // nothing pending: no record
            log.Absorb(ResponsePartsTests.Chunk(("thinking when the stream died", true)));
            await log.EndAbortedRequestAsync(2);

            var requests = ThoughtFile.Read(log.FilePath!)!.Requests;
            Assert.Equal(2, requests.Count);
            Assert.Equal("thinking when the stream died", requests[1].Text);
            Assert.Equal(ThoughtLog.AbortedFinishReason, requests[1].FinishReason);
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    });

    [Fact]
    public Task A_log_written_later_is_not_reported_missing() => WithConsole(async console => {
        var settings = new ThoughtSummaryConfig { Enabled = true, ToConsole = false, ToExistingLog = true, ToSeparateFile = false };
        var later = new ThoughtLog(settings, "pdf-repair-1", "PDF repair", "m", null, existingLogPath: null, existingLogWrittenLater: true);
        var none = new ThoughtLog(settings, "final", "Final", "m", null, existingLogPath: null);

        later.Absorb(ResponsePartsTests.Chunk(("t", true)));
        await later.EndRequestAsync(1, Usage(1, 1, "STOP"));
        Assert.DoesNotContain("ToExistingLog", console.Output);

        none.Absorb(ResponsePartsTests.Chunk(("t", true)));
        await none.EndRequestAsync(1, Usage(1, 1, "STOP"));
        Assert.Contains("ToExistingLog", console.Output);
    });

    [Fact]
    public Task Thoughts_for_a_log_written_later_are_appended_on_request() => WithConsole(async _ => {
        string folder = Directory.CreateTempSubdirectory("thoughts-later-").FullName;
        try {
            var settings = new ThoughtSummaryConfig { Enabled = true, ToConsole = false, ToExistingLog = true, ToSeparateFile = false };
            var log = new ThoughtLog(settings, "pdf-repair-1", "PDF repair", "m", Path.Combine(folder, "fix.tex"), existingLogPath: null);

            log.Absorb(ResponsePartsTests.Chunk(("Missing brace in line 40.", true)));
            await log.EndRequestAsync(1, Usage(5, 5, "STOP"));
            string compileLog = Path.Combine(folder, "compile-log.txt");
            File.WriteAllText(compileLog, "compile output\n");
            await log.AppendAllToAsync(compileLog);

            Assert.Contains("Missing brace in line 40.", File.ReadAllText(compileLog));
            Assert.Null(log.FilePath);
        }
        finally {
            Directory.Delete(folder, recursive: true);
        }
    });
}

/// <summary><c>--thoughts</c>: parsing, and that not passing it leaves the config alone.</summary>
public class ThoughtOptionsTests {
    private static ParseResult Parse(params string[] args) => CliBootstrapper.BuildRootCommand().Parse(args);

    [Theory]
    [InlineData("on", true, null, null, null)]
    [InlineData("off", false, null, null, null)]
    [InlineData("all", true, true, true, true)]
    [InlineData("file", true, false, false, true)]
    [InlineData("console,log", true, true, true, false)]
    public void Values_parse_to_the_documented_override(string raw, bool enabled, bool? console, bool? log, bool? file) {
        Assert.True(ThoughtOptions.TryParse(raw, out var value, out _));
        Assert.Equal(new ThoughtOptions.Override(enabled, console, log, file), value);
    }

    [Fact]
    public void An_unknown_target_is_a_parse_error() {
        Assert.NotEmpty(Parse("refine", "run", "--tex", "x.tex", "--thoughts", "console,screen").Errors);
    }

    [Fact]
    public void The_option_is_accepted_on_every_paid_command() {
        Assert.Empty(Parse("run", "--video", "v.mp4", "--thoughts", "on").Errors);
        Assert.Empty(Parse("extract", "run", "--video", "v.mp4", "--thoughts", "file").Errors);
        Assert.Empty(Parse("refine", "run", "--tex", "x.tex", "--thoughts", "all").Errors);
        Assert.Empty(Parse("pdf", "compile", "--tex", "x.tex", "--thoughts", "off").Errors);
        Assert.Empty(Parse("batch", "--workers", "profile=1", "--thoughts", "on").Errors);
    }

    [Fact]
    public void Not_passing_it_leaves_every_stage_as_configured() {
        var config = new LatexRefinementSessionConfig();
        config.Step2SpeechRefinement.ThoughtSummaries.Enabled = true;

        ThoughtOptions.Apply(config, Parse("refine", "run", "--tex", "x.tex"));

        Assert.True(config.Step2SpeechRefinement.ThoughtSummaries.Enabled);
        Assert.False(config.Step1MergeAndTimestamp.ThoughtSummaries.Enabled);
    }

    [Fact]
    public void Passing_it_sets_every_refinement_step_and_the_pdf_repair() {
        var config = new LatexRefinementSessionConfig();

        ThoughtOptions.Apply(config, Parse("refine", "run", "--tex", "x.tex", "--thoughts", "file"));

        foreach (var settings in new[] {
            config.Step1MergeAndTimestamp.ThoughtSummaries, config.Step2SpeechRefinement.ThoughtSummaries,
            config.Step3LastRefinement.ThoughtSummaries, config.PdfCompilation.ThoughtSummaries }) {
            Assert.True(settings.Enabled);
            Assert.True(settings.ToSeparateFile);
            Assert.False(settings.ToConsole);
        }
    }

    [Fact]
    public void ExtractionOptions_apply_it_to_stage_1() {
        var config = new AiStudioAutoExtractionConfig();

        ExtractionOptions.Apply(config, Parse("run", "--video", "v.mp4", "--thoughts", "on"));

        Assert.True(config.ThoughtSummaries.Enabled);
    }
}

/// <summary>Discovery behind <c>lecx thoughts list/show</c>.</summary>
public class ThoughtsCommandTests {
    [Theory]
    [InlineData("speech", "speech", true)]
    [InlineData("extract-part2", "extract", true)]
    [InlineData("pdf-repair-1", "pdf", true)]
    [InlineData("final", "speech", false)]
    [InlineData("merge", null, true)]
    [InlineData("extract-part1", "extract-part1", true)]
    [InlineData("extract-part10", "extract-part1", false)]
    [InlineData("extract-part12", "extract-part", true)]
    [InlineData("pdf-repair-12", "pdf-repair-1", false)]
    [InlineData("Extract-Part3", "extract-part3", true)]
    public void Stage_filter_is_a_case_insensitive_prefix(string key, string? filter, bool expected) {
        Assert.Equal(expected, ThoughtsCommand.StageMatches(key, filter));
    }

    [Fact]
    public void Only_the_top_header_layer_names_the_files_thoughts() {
        var generation = new GenerationParameters();
        string part = TexDocumentWriter.BuildPartHeader(1, 2, "v-part1.mp4", 0, default, "m", generation, "v-part1-thoughts.md");
        string withoutOwn = TexDocumentWriter.StackLayers(
            TexDocumentWriter.BuildRefinementHeader(TexDocumentWriter.MergeStep, "step2.tex", "step1.tex", default, "m", generation), part, "body");
        string withOwn = TexDocumentWriter.StackLayers(
            TexDocumentWriter.BuildRefinementHeader(TexDocumentWriter.MergeStep, "step2-copy1.tex", "step1.tex", default, "m", generation, "step2-thoughts-2.md"), part, "body");

        Assert.Null(TexDocumentWriter.ThoughtsFileNameOf(withoutOwn));
        Assert.Equal("step2-thoughts-2.md", TexDocumentWriter.ThoughtsFileNameOf(withOwn));
        Assert.Null(TexDocumentWriter.ThoughtsFileNameOf("no header at all"));
    }

    [Fact]
    public void FindIn_skips_a_file_it_cannot_read_instead_of_failing() {
        string root = Directory.CreateTempSubdirectory("thoughts-locked-").FullName;
        try {
            var header = new ThoughtFileHeader("speech", "Speech", "step3.tex", "m", DateTimeOffset.Now);
            string path = Path.Combine(root, "step3-thoughts.md");
            File.WriteAllText(path, ThoughtFile.FormatHeader(header));

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                Assert.Empty(ThoughtsCommand.FindIn(root, null));
            }
            Assert.Single(ThoughtsCommand.FindIn(root, null));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindIn_reads_thoughts_files_in_lecture_folders_and_skips_other_markdown() {
        string root = Directory.CreateTempSubdirectory("thoughts-find-").FullName;
        try {
            string lecture = Directory.CreateDirectory(Path.Combine(root, "lecture-1")).FullName;
            var header = new ThoughtFileHeader("speech", "Speech", "step3.tex", "m", DateTimeOffset.Now);
            File.WriteAllText(Path.Combine(lecture, "step3-thoughts.md"),
                ThoughtFile.FormatHeader(header) + ThoughtFile.FormatRequest(new ThoughtRequestRecord(1, DateTimeOffset.Now, 7, 8, "STOP", "t")));
            File.WriteAllText(Path.Combine(lecture, "notes-thoughts.md"), "# not one of ours");

            var all = ThoughtsCommand.FindIn(root, null);
            Assert.Single(all);
            Assert.Equal(7, all[0].Requests.Single().ThoughtTokens);
            Assert.Empty(ThoughtsCommand.FindIn(root, "final"));
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
