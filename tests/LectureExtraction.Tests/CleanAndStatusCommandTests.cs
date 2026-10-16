using System;
using System.CommandLine;
using System.IO;
using LectureExtraction.Cli;
using LectureExtraction.Cli.Commands;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using Xunit;

namespace LectureExtraction.Tests;

[Collection(ConsoleTestCollection.Name)]
public class CleanAndStatusCommandTests : IDisposable {
    private readonly IPromptSource _promptSource = Ui.PromptSource;
    private readonly bool _saveEnabled = ConfigStore.SaveEnabled;
    private readonly string? _directoryOverride = ConfigStore.DirectoryOverride;

    public void Dispose() {
        Ui.PromptSource = _promptSource;
        ConfigStore.SaveEnabled = _saveEnabled;
        ConfigStore.DirectoryOverride = _directoryOverride;
    }
    [Fact]
    public void ApiKeyProfileResolver_ResolvesNumericIndex() {
        string[] envNames = ["API_KEY-automated-content-extraction", "API_KEY-test-1", "API_KEY-latex-refinement"];
        Assert.Equal(0, ApiKeyProfileResolver.ResolveIndex("0", envNames));
        Assert.Equal(2, ApiKeyProfileResolver.ResolveIndex("2", envNames));
    }

    [Fact]
    public void ApiKeyProfileResolver_ResolvesByEnvName_ExactAndNormalized() {
        string[] envNames = ["API_KEY-automated-content-extraction", "API_KEY-ai-studio-test-project-1", "API_KEY-latex-refinement"];

        // Exact name
        Assert.Equal(2, ApiKeyProfileResolver.ResolveIndex("API_KEY-latex-refinement", envNames));
        // Stripped prefix
        Assert.Equal(2, ApiKeyProfileResolver.ResolveIndex("latex-refinement", envNames));
        Assert.Equal(0, ApiKeyProfileResolver.ResolveIndex("automated-content-extraction", envNames));
        // Partial substring
        Assert.Equal(1, ApiKeyProfileResolver.ResolveIndex("test-project-1", envNames));
    }

    [Fact]
    public void PlanCommand_AcceptsResumeOption() {
        var command = PlanCommand.Build();
        var result = command.Parse("--resume");
        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(PlanCommand.Resume));
    }

    [Fact]
    public void RunCommand_AcceptsCleanTmpOption() {
        var command = RunCommand.Build(chainRefinement: true, "run", "test");
        var result = command.Parse("--clean-tmp");
        Assert.Empty(result.Errors);
        Assert.True(result.GetValue(RunCommand.CleanTmp));
    }

    [Fact]
    public async Task CleanCommand_RejectsRunWithoutTmpOrCopies() {
        var root = CliBootstrapper.BuildRootCommand();
        int exitCode = await CliBootstrapper.RunAsync(root, ["clean", "--video", "test.mp4"]);
        Assert.Equal(ExitCodes.Usage, exitCode);
    }

    [Fact]
    public async Task CleanCommand_DryRun_ReportsExpectedCleanup() {
        string tempDir = Path.Combine(Path.GetTempPath(), "clean_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tmpSubdir = Path.Combine(tempDir, "tmp");
        Directory.CreateDirectory(tmpSubdir);
        File.WriteAllText(Path.Combine(tmpSubdir, "segment.mp4"), "dummy video content");

        try {
            var root = CliBootstrapper.BuildRootCommand();
            int exitCode = await CliBootstrapper.RunAsync(root, ["clean", "--folder", tempDir, "--tmp", "--dry-run", "--force"]);
            Assert.Equal(ExitCodes.Success, exitCode);
            // In dry run, directory must still exist
            Assert.True(Directory.Exists(tmpSubdir));
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task StatusCommand_InspectsDirectory() {
        string tempDir = Path.Combine(Path.GetTempPath(), "status_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "step1-test-part1.tex"), "% part 1");
        File.WriteAllText(Path.Combine(tempDir, "step1-test-all-offset.tex"), "% all offset");

        try {
            var root = CliBootstrapper.BuildRootCommand();
            int exitCode = await CliBootstrapper.RunAsync(root, ["status", "--folder", tempDir]);
            Assert.Equal(ExitCodes.Success, exitCode);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CleanCommand_Copies_MovesBothHyphenatedAndLegacyCopyFilesToArchive() {
        string tempDir = Path.Combine(Path.GetTempPath(), "clean_copies_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string copyHyphen = Path.Combine(tempDir, "step1-lec-all-offset-copy-1.tex");
        string copyLegacy = Path.Combine(tempDir, "step2-lec-offset-merged-copy1.tex");
        string primary = Path.Combine(tempDir, "step1-lec-all-offset.tex");
        File.WriteAllText(copyHyphen, "% copy-1");
        File.WriteAllText(copyLegacy, "% copy1");
        File.WriteAllText(primary, "% primary");

        try {
            var root = CliBootstrapper.BuildRootCommand();
            int exitCode = await CliBootstrapper.RunAsync(root, ["clean", "--folder", tempDir, "--copies", "--force"]);
            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.False(File.Exists(copyHyphen));
            Assert.False(File.Exists(copyLegacy));
            Assert.True(File.Exists(primary));
            Assert.True(Directory.GetFiles(Path.Combine(tempDir, "archive"), "step1-lec-all-offset-copy-1.tex", SearchOption.AllDirectories).Length > 0);
            Assert.True(Directory.GetFiles(Path.Combine(tempDir, "archive"), "step2-lec-offset-merged-copy1.tex", SearchOption.AllDirectories).Length > 0);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
