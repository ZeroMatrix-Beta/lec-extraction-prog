using System.CommandLine;

namespace LectureExtraction.Cli;

/// <summary>
/// The options every command accepts. They are declared <c>Recursive</c> and attached to the root
/// command once, so a subcommand cannot accidentally ship a divergent spelling of <c>--json</c>.
/// </summary>
public static class CliOptions {
    public static readonly Option<bool> Json = new("--json") {
        Description = "Write a machine-readable result to stdout; human logging goes to stderr.",
        Recursive = true
    };

    public static readonly Option<bool> DryRun = new("--dry-run") {
        Description = "Resolve everything and report what would happen, without issuing a paid request.",
        Recursive = true
    };

    public static readonly Option<bool> Yes = new("--yes", "-y") {
        Description = "Accept the safe default for prompts that have one, instead of failing.",
        Recursive = true
    };

    /// <summary>
    /// Opts back in to <c>ConfigLoader.Save</c>. It is off by default in CLI mode because the app
    /// rewrites its own <c>*Config.json</c> mid-run, and an unattended caller must not silently
    /// change the settings the interactive user comes back to.
    /// </summary>
    public static readonly Option<bool> SaveConfig = new("--save-config") {
        Description = "Allow the run to persist configuration changes (off by default in CLI mode).",
        Recursive = true
    };

    public static readonly Option<string?> ConfigDir = new("--config-dir") {
        Description = "Directory holding the *Config.json files. Defaults to the executable's folder.",
        Recursive = true
    };

    public static readonly Option<bool> Quiet = new("--quiet", "-q") {
        Description = "Suppress everything except errors.",
        Recursive = true
    };

    public static Option[] All => [Json, DryRun, Yes, SaveConfig, ConfigDir, Quiet];

    /// <summary>Reads the global options out of a parse result into the record commands consume.</summary>
    public static CliContext ReadContext(ParseResult parseResult) => new(
        Json: parseResult.GetValue(Json),
        DryRun: parseResult.GetValue(DryRun),
        AssumeYes: parseResult.GetValue(Yes),
        SaveConfig: parseResult.GetValue(SaveConfig),
        ConfigDir: parseResult.GetValue(ConfigDir),
        Quiet: parseResult.GetValue(Quiet));
}

/// <summary>The resolved global options, passed to a command instead of a <c>ParseResult</c>.</summary>
public sealed record CliContext(
    bool Json,
    bool DryRun,
    bool AssumeYes,
    bool SaveConfig,
    string? ConfigDir,
    bool Quiet);
