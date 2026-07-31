using System;
using System.IO;
using System.Linq;
using LectureExtraction.Cli;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the pre-flight report. Its whole value is being trustworthy about cost, so the tests
/// concentrate on the two numbers a caller decides on - how many requests are still pending, and
/// how many parts can be reused - plus the warnings that catch an expensive mistake before it is
/// made.
/// </summary>
public class ExtractionPlannerTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lecx-plan-" + Guid.NewGuid().ToString("N"));

    public ExtractionPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) {
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private AiStudioAutoExtractionConfig Config(int parts = 2) {
        var config = new AiStudioAutoExtractionConfig { NumberOfParts = parts };
        config.SourceFolder = _root;
        config.TargetFolder = Path.Combine(_root, "out");
        return config;
    }

    private string Video(string name) {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "not a real video");
        return path;
    }

    /// <summary>Writes a finished part .tex where the pipeline would, so the planner can find it.</summary>
    private void WriteFinishedPart(AiStudioAutoExtractionConfig config, string videoPath, int part, TimeSpan age) {
        string folder = Path.Combine(config.TargetFolder, ExtractionHelpers.ComputeOutputFolderName(videoPath));
        Directory.CreateDirectory(folder);

        string texPath = Path.Combine(folder, $"{ExtractionHelpers.ComputeTexBaseName(videoPath)}-part{part}.tex");
        File.WriteAllText(texPath, "\\section{done}");
        File.SetLastWriteTime(texPath, DateTime.Now - age);
    }

    [Fact]
    public void PendingRequests_IsSegmentsTimesVideos_WhenNothingExistsYet() {
        var config = Config(parts: 3);
        string[] videos = [Video("02-16-2026-monday-week1-Analysis.mp4"), Video("02-18-2026-wednesday-week1-Analysis.mp4")];

        var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(2, plan.VideoCount);
        Assert.Equal(6, plan.PendingRequests);
        Assert.Equal(0, plan.ResumableSegments);
    }

    [Fact]
    public void AFreshPart_CountsAsResumable_AndIsNotBilledAgain() {
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromMinutes(30));

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(1, plan.ResumableSegments);
        Assert.Equal(1, plan.PendingRequests);
    }

    [Fact]
    public void AStalePart_FallsOutsideTheWindow_AndIsBilledAgain() {
        // The behaviour that costs money silently: the same file, three hours later, is re-requested.
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromHours(3));

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(0, plan.ResumableSegments);
        Assert.Equal(2, plan.PendingRequests);
    }

    [Fact]
    public void AWiderResumeWindow_RescuesTheSameStalePart() {
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromHours(3));

        var plan = ExtractionPlanner.Build(config, [video], resumeWindowHours: 24, force: false);

        Assert.Equal(1, plan.ResumableSegments);
    }

    [Fact]
    public void Force_IgnoresEveryExistingPart() {
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromMinutes(5));

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: true);

        Assert.Equal(0, plan.ResumableSegments);
        Assert.Equal(2, plan.PendingRequests);
    }

    [Fact]
    public void BothVariantsOfOneLecture_AreReportedAsAnOutputFolderCollision() {
        // The real hazard: a source folder holding lecture.mp4 and lecture-speed-1-compressed.mp4
        // resolves both to one output folder, so each reads the other's parts as its own cache.
        var config = Config();
        string[] videos = [
            Video("02-16-2026-monday-week1-Analysis.mp4"),
            Video("02-16-2026-monday-week1-Analysis-speed-1-compressed.mp4")
        ];

        var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Single(plan.Videos.Select(v => v.OutputFolder).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, warning => warning.Contains("share the output folder"));
    }

    [Fact]
    public void AVideoOutsideTheNamingScheme_IsWarnedAbout() {
        var config = Config();

        var plan = ExtractionPlanner.Build(config, [Video("random-recording.mp4")], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Contains(plan.Warnings, warning => warning.Contains("naming scheme"));
    }

    [Fact]
    public void Videos_AreOrderedChronologically() {
        var config = Config();
        string[] videos = [
            Video("03-02-2026-monday-week3-Analysis.mp4"),
            Video("02-16-2026-monday-week1-Analysis.mp4")
        ];

        var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal("02-16-2026-monday-week1-Analysis.mp4", plan.Videos[0].FileName);
    }

    [Fact]
    public void TargetFolder_DefaultsUnderTheSourceFolder_AsTheSessionDoes() {
        var config = Config();
        config.TargetFolder = "";

        var plan = ExtractionPlanner.Build(config, [Video("02-16-2026-monday-week1-Analysis.mp4")], 2, force: false);

        Assert.Equal(Path.Combine(_root, "extracted_output"), plan.TargetFolder);
    }
}

/// <summary>The base-name derivation the planner and the pipeline must agree on.</summary>
public class ExtractionHelpersNamingTests {
    [Theory]
    [InlineData("lecture-speed-1-compressed.mp4", "lecture")]
    [InlineData("lecture-speed-1.25-compressed.mp4", "lecture")]
    [InlineData("lecture-compressed.mp4", "lecture")]
    [InlineData("lecture.mp4", "lecture")]
    public void ComputeOutputFolderName_StripsCompressionSuffixes(string fileName, string expected) {
        Assert.Equal(expected, ExtractionHelpers.ComputeOutputFolderName(fileName));
    }

    [Fact]
    public void ComputeTexBaseName_AddsTheStageOnePrefix() {
        Assert.Equal("step1-lecture", ExtractionHelpers.ComputeTexBaseName("lecture-speed-1-compressed.mp4"));
    }

    [Fact]
    public void ComputeTexBaseName_DoesNotDoubleThePrefix() {
        Assert.Equal("step1-lecture", ExtractionHelpers.ComputeTexBaseName("step1-lecture.mp4"));
    }

    [Fact]
    public void OutputFolderName_AndTexBaseName_DifferByThePrefix() {
        // Stated as a test because predicting .tex paths from the folder name alone is a natural
        // mistake that would look for files the pipeline never writes.
        Assert.Equal("step1-" + ExtractionHelpers.ComputeOutputFolderName("lecture.mp4"),
                     ExtractionHelpers.ComputeTexBaseName("lecture.mp4"));
    }
}
