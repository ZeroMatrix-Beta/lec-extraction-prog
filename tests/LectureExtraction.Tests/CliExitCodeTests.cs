using System;
using System.CommandLine;
using System.Threading.Tasks;
using LectureExtraction.Cli;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Tests;

/// <summary>
/// Runs commands that fail on purpose through <see cref="CliBootstrapper.RunAsync(RootCommand, string[])"/>
/// and checks the exit code a caller sees.
///
/// <para>System.CommandLine's default exception handler used to catch every exception before the
/// bootstrapper's own handler could, print "Unhandled exception" and return 1 - so an unattended run
/// that only needed one more argument reported itself as a crash, and the documented exit code 3 was
/// unreachable. Parsing the tree, which is all the other CLI tests do, cannot see that.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class CliExitCodeTests : IDisposable {
    // RunAsync installs these process-wide; put back what the other tests expect.
    private readonly IPromptSource _promptSource = Ui.PromptSource;
    private readonly bool _saveEnabled = ConfigStore.SaveEnabled;
    private readonly string? _directoryOverride = ConfigStore.DirectoryOverride;

    public void Dispose() {
        Ui.PromptSource = _promptSource;
        ConfigStore.SaveEnabled = _saveEnabled;
        ConfigStore.DirectoryOverride = _directoryOverride;
    }

    private static RootCommand TreeWith(string name, Func<int> action) {
        var root = CliBootstrapper.BuildRootCommand();
        var command = new Command(name);
        command.SetAction(_ => action());
        root.Add(command);
        return root;
    }

    [Fact]
    public async Task UnansweredPrompt_ExitsWithTheUnattendedPromptCode() {
        var root = TreeWith("asks", () => {
            Ui.Confirm("Weiter?", true);
            return ExitCodes.Success;
        });

        int exitCode = await CliBootstrapper.RunAsync(root, ["asks"]);

        Assert.Equal(ExitCodes.UnattendedPrompt, exitCode);
    }

    [Fact]
    public async Task OtherExceptions_ReachTheCallerInsteadOfBecomingAGenericFailureCode() {
        // Program.Main is the handler for these; it prints the error and returns Unexpected.
        var root = TreeWith("crashes", () => throw new InvalidOperationException("boom"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => CliBootstrapper.RunAsync(root, ["crashes"]));

        Assert.Equal("boom", thrown.Message);
    }

    [Fact]
    public async Task AYesFlag_AnswersAPromptThatHasADefault() {
        var root = TreeWith("asks", () => Ui.Confirm("Weiter?", true) ? ExitCodes.Success : ExitCodes.Unexpected);

        int exitCode = await CliBootstrapper.RunAsync(root, ["asks", "--yes"]);

        Assert.Equal(ExitCodes.Success, exitCode);
    }
}
