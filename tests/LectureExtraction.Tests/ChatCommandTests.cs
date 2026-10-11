using System.CommandLine;
using System.Threading.Tasks;
using LectureExtraction.Cli;
using LectureExtraction.Cli.Commands;
using Xunit;

namespace LectureExtraction.Tests;

public class ChatCommandTests {
    private static ParseResult Parse(params string[] args) => CliBootstrapper.BuildRootCommand().Parse(args);

    [Fact]
    public void ChatCommand_AcceptsAllOptions() {
        var result = Parse(
            "chat",
            "--backend", "aistudio",
            "--model", "gemini-3.6-flash",
            "--profile", "1",
            "--skip-setup",
            "--system-prompt", "custom.md",
            "--no-system-prompt",
            "--no-history");

        Assert.Empty(result.Errors);
        Assert.Equal("aistudio", result.GetValue(ChatCommand.Backend));
        Assert.Equal("gemini-3.6-flash", result.GetValue(ChatCommand.Model));
        Assert.Equal(1, result.GetValue(ChatCommand.Profile));
        Assert.True(result.GetValue(ChatCommand.SkipSetup));
        Assert.Equal("custom.md", result.GetValue(ChatCommand.SystemPrompt));
        Assert.True(result.GetValue(ChatCommand.NoSystemPrompt));
        Assert.True(result.GetValue(ChatCommand.NoHistory));
    }

    [Fact]
    public async Task ChatCommand_DryRun_ReturnsSuccess() {
        int code = await CliBootstrapper.RunAsync(["chat", "--dry-run"]);
        Assert.Equal(ExitCodes.Success, code);
    }

    [Fact]
    public async Task ChatCommand_UnknownBackend_ReturnsUsage() {
        int code = await CliBootstrapper.RunAsync(["chat", "--backend", "unknown"]);
        Assert.Equal(ExitCodes.Usage, code);
    }
}
