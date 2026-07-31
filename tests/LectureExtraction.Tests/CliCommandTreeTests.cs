using System.CommandLine;
using LectureExtraction.Cli;
using LectureExtraction.Configuration;

namespace LectureExtraction.Tests;

/// <summary>
/// Guards the CLI's public surface.
///
/// <para>The command names, the global option spellings and the exit codes are a contract an
/// automation caller writes scripts against - renaming <c>--dry-run</c> or shuffling an exit code
/// breaks callers silently, exactly the way a renamed config key does. These tests parse the real
/// tree rather than a copy, so the assertions cannot drift away from what ships.</para>
/// </summary>
public class CliCommandTreeTests {
    private static ParseResult Parse(params string[] args) => CliBootstrapper.BuildRootCommand().Parse(args);

    [Theory]
    [InlineData("config")]
    [InlineData("run")]
    [InlineData("batch")]
    [InlineData("plan")]
    [InlineData("media")]
    [InlineData("extract")]
    [InlineData("refine")]
    [InlineData("pdf")]
    [InlineData("ask")]
    public void RootCommand_ExposesTopLevelCommand(string name) {
        var root = CliBootstrapper.BuildRootCommand();

        Assert.Contains(root.Subcommands, command => command.Name == name);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--dry-run")]
    [InlineData("--yes")]
    [InlineData("--save-config")]
    [InlineData("--config-dir")]
    [InlineData("--quiet")]
    public void GlobalOption_IsAcceptedOnASubcommand(string option) {
        // Recursive options are the reason a caller can put --json after any command; if one stops
        // being recursive it parses as an unrecognised token here rather than at a user's shell.
        string[] args = option == "--config-dir"
            ? ["config", "models", option, "some-dir"]
            : ["config", "models", option];

        var result = Parse(args);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void GlobalOptions_DefaultToTheSafeSetting() {
        var context = CliOptions.ReadContext(Parse("config", "models"));

        // SaveConfig false is the load-bearing default: the app rewrites its own *Config.json at
        // runtime, and an unattended run must not change what the interactive user comes back to.
        Assert.False(context.SaveConfig);
        Assert.False(context.DryRun);
        Assert.False(context.Json);
        Assert.False(context.Quiet);
        Assert.False(context.AssumeYes);
        Assert.Null(context.ConfigDir);
    }

    [Fact]
    public void GlobalOptions_AreReadFromTheParseResult() {
        var context = CliOptions.ReadContext(
            Parse("config", "models", "--json", "--dry-run", "--yes", "--save-config", "--quiet", "--config-dir", "C:/tmp"));

        Assert.True(context.Json);
        Assert.True(context.DryRun);
        Assert.True(context.AssumeYes);
        Assert.True(context.SaveConfig);
        Assert.True(context.Quiet);
        Assert.Equal("C:/tmp", context.ConfigDir);
    }

    [Fact]
    public void UnknownCommand_IsAParseError() {
        Assert.NotEmpty(Parse("frobnicate").Errors);
    }

    [Fact]
    public void ConfigGet_RequiresItsKeyArgument() {
        Assert.NotEmpty(Parse("config", "get").Errors);
    }

    [Fact]
    public void ExitCodes_AreTheDocumentedValues() {
        Assert.Equal(0, ExitCodes.Success);
        Assert.Equal(1, ExitCodes.Unexpected);
        Assert.Equal(2, ExitCodes.Usage);
        Assert.Equal(3, ExitCodes.UnattendedPrompt);
        Assert.Equal(4, ExitCodes.Configuration);
        Assert.Equal(5, ExitCodes.ApiExhausted);
        Assert.Equal(6, ExitCodes.Partial);
    }
}

/// <summary>
/// Covers the reflection that lets a command reach <c>ConfigLoader&lt;T&gt;</c> from a string, and
/// the dotted-path reader behind <c>config get</c>.
/// </summary>
public class ConfigSectionRegistryTests {
    [Fact]
    public void Names_CoverEveryConfigFileOnDisk() {
        // The section name is also the JSON file name, so a type missing here is a config file the
        // CLI cannot see.
        Assert.Contains(nameof(AiStudioAutoExtractionConfig), ConfigSectionRegistry.Names);
        Assert.Contains(nameof(VertexAutoExtractionConfig), ConfigSectionRegistry.Names);
        Assert.Contains(nameof(LatexRefinementSessionConfig), ConfigSectionRegistry.Names);
        Assert.Contains(nameof(FfmpegSessionConfig), ConfigSectionRegistry.Names);
        Assert.Contains(nameof(DirectAiChatSessionAiStudioConfig), ConfigSectionRegistry.Names);
        Assert.Contains(nameof(DirectAiChatSessionVertexConfig), ConfigSectionRegistry.Names);
    }

    [Fact]
    public void TryResolve_IsCaseInsensitive() {
        Assert.True(ConfigSectionRegistry.TryResolve("aistudioautoextractionconfig", out var type));
        Assert.Equal(typeof(AiStudioAutoExtractionConfig), type);
    }

    [Fact]
    public void TryResolve_RejectsAnUnknownSection() {
        Assert.False(ConfigSectionRegistry.TryResolve("NotAConfig", out _));
    }

    [Fact]
    public void TryReadPath_ReadsANestedProperty() {
        var config = new AiStudioAutoExtractionConfig();
        config.Paths.SourceFolder = @"D:\somewhere";

        Assert.True(ConfigSectionRegistry.TryReadPath(config, "Paths.SourceFolder", out object? value, out _));
        Assert.Equal(@"D:\somewhere", value);
    }

    [Fact]
    public void TryReadPath_ReadsTheFlatAliasOfTheSameValue() {
        // The flat delegating properties are [JsonIgnore] but still ordinary properties, so a
        // caller who knows either spelling gets the same answer.
        var config = new AiStudioAutoExtractionConfig();
        config.Paths.SourceFolder = @"D:\somewhere";

        Assert.True(ConfigSectionRegistry.TryReadPath(config, "SourceFolder", out object? value, out _));
        Assert.Equal(@"D:\somewhere", value);
    }

    [Fact]
    public void TryReadPath_ReportsTheSegmentThatFailed() {
        Assert.False(ConfigSectionRegistry.TryReadPath(new AiStudioAutoExtractionConfig(), "Paths.Nope", out _, out string? error));
        Assert.Contains("Nope", error);
    }
}
