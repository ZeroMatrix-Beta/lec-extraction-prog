using System;
using System.CommandLine;
using System.Threading.Tasks;
using Spectre.Console;
using LectureExtraction.Cli.Commands;
using LectureExtraction.Configuration;
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

        var context = CliOptions.ReadContext(parseResult);

        // Every prompt in the app now resolves through this, so installing it here is the whole of
        // "run headlessly" - no command has to know it is running without a keyboard.
        Ui.PromptSource = new PresetPromptSource(context.AssumeYes);

        // Off unless asked for: the pipeline calls ConfigLoader.Save at several points during a
        // normal run, and an unattended caller must not change what the interactive user comes
        // back to. `config set` re-enables it for its own write, where changing config is the ask.
        ConfigStore.SaveEnabled = context.SaveConfig;
        ConfigStore.DirectoryOverride = context.ConfigDir;

        if (context.Json) {
            RouteLoggingToStandardError();
        }

        try {
            return await parseResult.InvokeAsync();
        }
        catch (UnattendedPromptException ex) {
            // Distinct from a crash: the run was well-formed and simply needs one more argument.
            Ui.Error(ex.Message, "CLI");
            return ExitCodes.UnattendedPrompt;
        }
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

    /// <summary>
    /// Sends every <c>Ui</c> write to stderr, leaving stdout for the command's payload alone.
    ///
    /// <para>Without this, <c>--json</c> is unusable for anything that reports progress: FFmpeg's
    /// running commentary and the JSON document land on the same stream, and no parser can read
    /// the result. Because the whole app writes through <c>Ui</c>, redirecting Spectre's console
    /// once here covers every subsystem.</para>
    /// </summary>
    private static void RouteLoggingToStandardError() {
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
            Out = new AnsiConsoleOutput(Console.Error)
        });
    }

    /// <summary>Exposed for tests, which assert the shape of the tree without running anything.</summary>
    public static RootCommand BuildRootCommand() {
        var root = new RootCommand(Description);

        foreach (var option in CliOptions.All) {
            root.Add(option);
        }

        root.Add(ConfigCommands.Build());
        root.Add(MediaCommands.Build());
        root.Add(PlanCommand.Build());
        root.Add(RunCommand.Build(chainRefinement: true, "run", "Run the whole pipeline: segments -> LaTeX -> refinement -> PDF."));

        var extract = new Command("extract", "Transcription only - never chains into refinement.");
        extract.Add(RunCommand.Build(chainRefinement: false, "run", "Transcribe videos into per-part .tex files."));
        root.Add(extract);

        foreach (var planned in PlannedCommands.Build()) {
            root.Add(planned);
        }

        return root;
    }
}
