using System.CommandLine;
using LectureExtraction.Configuration;

namespace LectureExtraction.Cli;

/// <summary>
/// The knobs several commands share, declared once so <c>media segment</c>, <c>plan</c> and
/// <c>run</c> cannot drift into different spellings or different defaults for the same setting.
/// A null option means "leave the configured value alone" - the CLI overrides configuration, it
/// does not replace it.
/// </summary>
public static class ExtractionOptions {
    public static readonly Option<string?> Out = new("--out") {
        Description = "Target folder for the produced files. Defaults to the configured target folder."
    };

    public static readonly Option<int?> Parts = new("--parts") {
        Description = "Number of overlapping segments to split into."
    };

    public static readonly Option<int?> Overlap = new("--overlap") {
        Description = "Overlap between segments, in seconds."
    };

    public static readonly Option<double?> Speed = new("--speed") {
        Description = "Playback speed multiplier applied during preprocessing."
    };

    public static readonly Option<string?> Preset = new("--preset") {
        Description = "FFmpeg encoding preset (e.g. fast, medium, slow)."
    };

    public static readonly Option<string?> Model = new("--model") {
        Description = "Model id to use for this run."
    };

    public static readonly Option<int?> Profile = new("--profile") {
        Description = "API-key profile index to use for this run."
    };

    /// <summary>
    /// Applies whichever overrides were supplied. Config writeback is off by default in CLI mode,
    /// so these change the in-memory config for this run only unless --save-config was passed.
    /// </summary>
    public static void Apply(AiStudioAutoExtractionConfig config, ParseResult parseResult) {
        string? target = parseResult.GetValue(Out);
        if (!string.IsNullOrWhiteSpace(target)) {
            config.TargetFolder = System.IO.Path.GetFullPath(target);
        }

        if (parseResult.GetValue(Parts) is int parts) {
            config.NumberOfParts = parts;
        }

        if (parseResult.GetValue(Overlap) is int overlap) {
            config.OverlapSeconds = overlap;
        }

        if (parseResult.GetValue(Speed) is double speed) {
            config.SpeedMultiplier = speed;
        }

        string? preset = parseResult.GetValue(Preset);
        if (!string.IsNullOrWhiteSpace(preset)) {
            config.FfmpegPreset = preset;
        }

        string? model = parseResult.GetValue(Model);
        if (!string.IsNullOrWhiteSpace(model)) {
            config.CurrentModel = model;
        }

        if (parseResult.GetValue(Profile) is int profile) {
            config.ActiveApiProfile = profile;
        }
    }
}
