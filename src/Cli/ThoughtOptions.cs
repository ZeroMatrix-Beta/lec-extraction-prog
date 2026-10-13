using System;
using System.CommandLine;
using System.Linq;
using LectureExtraction.Configuration;

namespace LectureExtraction.Cli;

/// <summary>
/// [AI Context] <c>--thoughts</c>: turns Gemini's thought summaries on or off for every stage a command
/// runs, for this run only (config writeback is off in CLI mode). Not passing it leaves each stage's
/// configured <c>ThoughtSummaries</c> alone, like every other override.
///
/// <para>Values: <c>on</c> (enable, keep each stage's configured targets), <c>off</c>, <c>all</c>
/// (enable with every target), or a comma list of targets to enable exactly:
/// <c>console</c>, <c>log</c> (the stage's existing log), <c>file</c> (the <c>-thoughts.md</c> file
/// <c>lecx thoughts</c> reads).</para>
/// [Human] <c>--thoughts</c> schaltet die Gedanken-Zusammenfassungen für alle Stufen dieses Laufs.
/// </summary>
public static class ThoughtOptions {
    public static readonly Option<string?> Thoughts = Create();

    /// <summary>What a parsed <c>--thoughts</c> value sets; a null target keeps the configured one.</summary>
    public sealed record Override(bool Enabled, bool? ToConsole = null, bool? ToExistingLog = null, bool? ToSeparateFile = null);

    private static Option<string?> Create() {
        var option = new Option<string?>("--thoughts") {
            Description = "Gemini thought summaries for every stage of this run: on, off, all, or targets to enable (console,log,file). Default: the config."
        };
        option.Validators.Add(result => {
            string? raw = result.GetValueOrDefault<string?>();
            if (raw != null && !TryParse(raw, out _, out string error)) {
                result.AddError(error);
            }
        });
        return option;
    }

    public static bool TryParse(string raw, out Override? value, out string error) {
        value = null;
        error = "";
        string text = raw.Trim().ToLowerInvariant();

        switch (text) {
            case "on" or "true" or "1":
                value = new Override(true);
                return true;
            case "off" or "false" or "0" or "none":
                value = new Override(false);
                return true;
            case "all":
                value = new Override(true, true, true, true);
                return true;
        }

        bool console = false, log = false, file = false;
        foreach (string token in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            switch (token) {
                case "console":
                    console = true;
                    break;
                case "log" or "existing-log":
                    log = true;
                    break;
                case "file" or "separate-file":
                    file = true;
                    break;
                default:
                    error = $"--thoughts: unknown value '{token}'. Use on, off, all, or a comma list of console, log, file.";
                    return false;
            }
        }

        if (!console && !log && !file) {
            error = "--thoughts: no target given. Use on, off, all, or a comma list of console, log, file.";
            return false;
        }

        value = new Override(true, console, log, file);
        return true;
    }

    public static void ApplyTo(ThoughtSummaryConfig target, Override value) {
        target.Enabled = value.Enabled;
        if (value.ToConsole is bool console) target.ToConsole = console;
        if (value.ToExistingLog is bool log) target.ToExistingLog = log;
        if (value.ToSeparateFile is bool file) target.ToSeparateFile = file;
    }

    /// <summary>The parsed override, or null when <c>--thoughts</c> was not passed.</summary>
    public static Override? Read(ParseResult parseResult) =>
        parseResult.GetValue(Thoughts) is string raw && TryParse(raw, out var value, out _) ? value : null;

    /// <summary>Stage 1 (the AI Studio extraction config).</summary>
    public static void Apply(AiStudioAutoExtractionConfig config, ParseResult parseResult) {
        if (Read(parseResult) is Override value) {
            config.ThoughtSummaries ??= new();
            ApplyTo(config.ThoughtSummaries, value);
        }
    }

    /// <summary>Refinement steps 1-3 and the PDF repair.</summary>
    public static void Apply(LatexRefinementSessionConfig config, ParseResult parseResult) {
        if (Read(parseResult) is not Override value) {
            return;
        }

        foreach (var step in new[] { config.Step1MergeAndTimestamp, config.Step2SpeechRefinement, config.Step3LastRefinement }.Where(step => step != null)) {
            step.ThoughtSummaries ??= new();
            ApplyTo(step.ThoughtSummaries, value);
        }

        config.PdfCompilation ??= new();
        config.PdfCompilation.ThoughtSummaries ??= new();
        ApplyTo(config.PdfCompilation.ThoughtSummaries, value);
    }
}
