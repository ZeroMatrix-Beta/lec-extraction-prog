using System;
using System.CommandLine;
using System.Threading.Tasks;
using LectureExtraction.Cli.Commands;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Cli;

/// <summary>
/// Builds the command tree and runs it. This is the headless half of the program: it exists so the
/// pipeline can be driven by a script or an agent, while <c>MainMenu</c> keeps serving the
/// interactive user unchanged. Which of the two runs is decided in <c>Program.Main</c> purely by
/// whether any arguments were passed.
/// </summary>
public static class CliBootstrapper {
    private const string Description =
        "lecx - lecture video -> LaTeX -> PDF, headless. Run without arguments for the interactive menu.";

    public static async Task<int> RunAsync(string[] args) {
        EnableUnicodeOutput();

        var root = BuildRootCommand();
        var parseResult = root.Parse(args);

        // Parse errors are reported here rather than through the default handler so that they carry
        // the CLI's own usage exit code instead of the library's generic failure code.
        if (parseResult.Errors.Count > 0) {
            foreach (var error in parseResult.Errors) {
                Ui.Error(error.Message, "CLI");
            }
            Ui.Detail("Run 'lecx --help' for the available commands.");
            return ExitCodes.Usage;
        }

        return await parseResult.InvokeAsync();
    }

    /// <summary>
    /// Windows consoles default to an OEM code page that silently drops the arrows and dashes in
    /// the German UI strings and in help text. The interactive path is left alone - it is already
    /// readable in whatever console the user launched it from - but a redirected CLI run is usually
    /// being captured by something that expects UTF-8.
    /// </summary>
    private static void EnableUnicodeOutput() {
        try {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception ex) {
            // Some redirected handles reject the change; the only cost is degraded glyphs.
            Ui.Detail($"Console encoding unchanged ({ex.GetType().Name}: {ex.Message})", "CLI");
        }
    }

    /// <summary>Exposed for tests, which assert the shape of the tree without running anything.</summary>
    public static RootCommand BuildRootCommand() {
        var root = new RootCommand(Description);

        foreach (var option in CliOptions.All) {
            root.Add(option);
        }

        root.Add(ConfigCommands.Build());

        foreach (var planned in PlannedCommands.Build()) {
            root.Add(planned);
        }

        return root;
    }
}
