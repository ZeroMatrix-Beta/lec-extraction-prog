using System.CommandLine;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The commands the CLI will grow, registered before they work. They are here so that
/// <c>--help</c> shows the whole intended shape - a caller can see where the pipeline is going and
/// review the vocabulary - while each one refuses honestly instead of pretending to run. Every
/// description names the phase that fills it in, and each command is deleted from this file as its
/// real implementation lands.
/// </summary>
public static class PlannedCommands {
    public static Command[] Build() => [
        Planned("run", "Run the whole pipeline for one video or a folder: segments -> LaTeX -> refinement -> PDF.", "C5"),
        Planned("batch", "Run several videos in parallel worker processes, each with its own model and API-key profile.", "C8"),
        Planned("plan", "Report what a run would do - videos, segments, requests - without calling the API.", "C4"),
        WithSubcommands("extract", "Transcription only - never chains into refinement.", [
            Planned("run", "Transcribe a prepared video into per-part .tex files.", "C5")
        ]),
        WithSubcommands("refine", "LaTeX refinement steps 1-3 over an existing .tex.", [
            Planned("run", "Merge, polish and validate a .tex file.", "C6")
        ]),
        WithSubcommands("pdf", "PDF compilation (step 4).", [
            Planned("compile", "Compile a .tex to PDF, optionally with the AI repair loop.", "C6")
        ]),
        Planned("ask", "Send one prompt with optional attachments and print the answer.", "C9")
    ];

    private static Command WithSubcommands(string name, string description, Command[] children) {
        var command = new Command(name, description);
        foreach (var child in children) {
            command.Add(child);
        }
        return command;
    }

    private static Command Planned(string name, string description, string phase) {
        var command = new Command(name, $"{description} [not implemented yet - {phase}]");
        command.SetAction(_ => {
            Ui.Error($"'{name}' is not implemented yet (planned for phase {phase}). Run 'lecx config --help' for what works today.", "CLI");
            return ExitCodes.Usage;
        });
        return command;
    }
}
