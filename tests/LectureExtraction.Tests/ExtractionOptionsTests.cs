using System.CommandLine;
using LectureExtraction.Cli;
using LectureExtraction.Configuration;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the override rule the stage commands share: a flag that was not passed must leave the
/// configured value alone. Getting this backwards would silently reset a user's model or segment
/// count to a CLI default on every run.
/// </summary>
public class ExtractionOptionsTests {
    private static ParseResult Parse(params string[] args) =>
        CliBootstrapper.BuildRootCommand().Parse(args);

    private static AiStudioAutoExtractionConfig ConfigWithKnownValues() {
        var config = new AiStudioAutoExtractionConfig {
            NumberOfParts = 3,
            OverlapSeconds = 180,
            SpeedMultiplier = 1.0,
            FfmpegPreset = "fast",
            CurrentModel = "gemini-3.6-flash",
            ActiveApiProfile = 1
        };
        config.TargetFolder = @"D:\configured-target";
        return config;
    }

    [Fact]
    public void Apply_LeavesEverythingAloneWhenNothingWasPassed() {
        var config = ConfigWithKnownValues();

        ExtractionOptions.Apply(config, Parse("media", "probe", "--input", "x.mp4"));

        Assert.Equal(3, config.NumberOfParts);
        Assert.Equal(180, config.OverlapSeconds);
        Assert.Equal(1.0, config.SpeedMultiplier);
        Assert.Equal("fast", config.FfmpegPreset);
        Assert.Equal("gemini-3.6-flash", config.CurrentModel);
        Assert.Equal(1, config.ActiveApiProfile);
        Assert.Equal(@"D:\configured-target", config.TargetFolder);
    }

    [Fact]
    public void Apply_OverridesTheSegmentGeometry() {
        var config = ConfigWithKnownValues();

        ExtractionOptions.Apply(config, Parse("media", "segment", "--input", "x.mp4", "--parts", "5", "--overlap", "90"));

        Assert.Equal(5, config.NumberOfParts);
        Assert.Equal(90, config.OverlapSeconds);
    }

    [Fact]
    public void Apply_OverridesSpeedUsingInvariantCulture() {
        // The machine is de-DE; "1.5" must not be read as 15.
        var config = ConfigWithKnownValues();

        ExtractionOptions.Apply(config, Parse("media", "segment", "--input", "x.mp4", "--speed", "1.5"));

        Assert.Equal(1.5, config.SpeedMultiplier);
    }

    [Fact]
    public void Apply_MakesTheTargetFolderAbsolute() {
        var config = ConfigWithKnownValues();

        ExtractionOptions.Apply(config, Parse("media", "segment", "--input", "x.mp4", "--out", "relative-out"));

        Assert.True(System.IO.Path.IsPathRooted(config.TargetFolder));
        Assert.EndsWith("relative-out", config.TargetFolder);
    }

    [Fact]
    public void Apply_OverridesModelAndProfile() {
        // --model and --profile belong to the API-calling commands, which the media group does not
        // declare, so this exercises Apply against a command carrying the full shared set rather
        // than through a tree that would reject the flags.
        var command = new Command("probe-all") {
            ExtractionOptions.Out, ExtractionOptions.Parts, ExtractionOptions.Overlap,
            ExtractionOptions.Speed, ExtractionOptions.Preset, ExtractionOptions.Model, ExtractionOptions.Profile
        };
        var config = ConfigWithKnownValues();

        ExtractionOptions.Apply(config, command.Parse("--model gemini-2.5-flash --profile 2"));

        Assert.Equal("gemini-2.5-flash", config.CurrentModel);
        Assert.Equal(2, config.ActiveApiProfile);
    }
}

/// <summary>Shape of the media command group.</summary>
public class MediaCommandTreeTests {
    private static Command Media =>
        CliBootstrapper.BuildRootCommand().Subcommands.Single(c => c.Name == "media");

    [Theory]
    [InlineData("probe")]
    [InlineData("segment")]
    [InlineData("audio")]
    public void Media_ExposesSubcommand(string name) {
        Assert.Contains(Media.Subcommands, command => command.Name == name);
    }

    [Fact]
    public void Media_SubcommandsAreNoLongerPlaceholders() {
        // The planned-command stubs mark themselves in their description; a real command must not.
        foreach (var command in Media.Subcommands) {
            Assert.DoesNotContain("not implemented", command.Description ?? "");
        }
    }

    [Fact]
    public void Segment_RequiresAnInput() {
        var result = CliBootstrapper.BuildRootCommand().Parse("media segment");

        Assert.NotEmpty(result.Errors);
    }
}
