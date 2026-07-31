using System;
using System.CommandLine;
using System.IO;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The refinement stages over an existing <c>.tex</c>: steps 1-3, and step 4 (PDF) on its own.
///
/// <para>Both commands go through the same <c>LatexRefinementSession</c> the interactive path uses,
/// and select stages the same way it does - by toggling the <c>Enabled</c> flags before starting.
/// Nothing about the pipeline's sequencing is reimplemented here.</para>
///
/// <para>The extraction config is deliberately <b>not</b> passed. It is what gates refinement on
/// <c>GoIntoLatexRefinement</c>, <c>GenerateOffsetFiles</c> and <c>GenerateAudioFile</c>, which are
/// prerequisites of "refinement as the tail of an extraction". A caller naming a .tex file
/// explicitly has already made that decision.</para>
/// </summary>
public static class RefineCommands {
    private static readonly Option<string> Tex = new("--tex") {
        Description = "The .tex file to work on.",
        Required = true
    };

    private static readonly Option<int?> Step = new("--step") {
        Description = "Run a single step: 1 (merge/timestamps), 2 (speech), 3 (final polish). Omit for all three."
    };

    private static readonly Option<bool> ThroughEnd = new("--through-end") {
        Description = "With --step, continue through the remaining steps and compile the PDF."
    };

    private static readonly Option<string?> Audio = new("--audio") {
        Description = "Audio track used to correct timestamps in step 1."
    };

    private static readonly Option<int?> FixLoop = new("--fix-loop") {
        Description = "Rounds of AI repair to attempt on a failed compile. 0 makes the command free."
    };

    public static Command BuildRefine() {
        var group = new Command("refine", "LaTeX refinement steps 1-3 over an existing .tex.");
        var run = new Command("run", "Merge, polish and validate a .tex file.") { Tex, Step, ThroughEnd, Audio };

        run.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string tex = parseResult.GetValue(Tex)!;

            if (!File.Exists(tex)) {
                Ui.Error($".tex file not found: '{tex}'", "refine");
                return ExitCodes.Usage;
            }

            int? step = parseResult.GetValue(Step);
            if (step is < 1 or > 3) {
                Ui.Error("--step must be 1, 2 or 3.", "refine");
                return ExitCodes.Usage;
            }

            var config = ConfigLoader<LatexRefinementSessionConfig>.Load();
            string? audio = ResolveAudio(parseResult, tex);
            bool throughEnd = parseResult.GetValue(ThroughEnd);

            // "4" is the interactive menu's spelling of "the whole pipeline"; reusing it keeps the
            // stage selection in one place rather than duplicating the flag arithmetic.
            string stepChoice = step?.ToString() ?? "4";

            if (context.DryRun) {
                CliOutput.Payload(context, new {
                    dryRun = true, tex = Path.GetFullPath(tex), step = stepChoice, throughEnd,
                    audio, backend = config.UseVertex ? "vertex" : "aistudio"
                }, () => Ui.Info($"Würde {(step == null ? "die komplette Pipeline" : $"Schritt {step}")} auf {Path.GetFileName(tex)} anwenden."));
                return ExitCodes.Success;
            }

            RefinementUiHelper.ApplyStepSelection(config, stepChoice, throughEnd);
            await RefinementUiHelper.RunRefinementAsync(config, null, Path.GetFullPath(tex), audio);

            CliOutput.Payload(context, new { tex = Path.GetFullPath(tex), step = stepChoice, throughEnd, completed = true },
                () => { /* The session reports each stage as it runs. */ });
            return ExitCodes.Success;
        });

        group.Add(run);
        return group;
    }

    public static Command BuildPdf() {
        var group = new Command("pdf", "PDF compilation (step 4).");
        var compile = new Command("compile", "Compile a .tex to PDF, optionally with the AI repair loop.") { Tex, FixLoop };

        compile.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string tex = parseResult.GetValue(Tex)!;

            if (!File.Exists(tex)) {
                Ui.Error($".tex file not found: '{tex}'", "pdf");
                return ExitCodes.Usage;
            }

            var config = ConfigLoader<LatexRefinementSessionConfig>.Load();

            // Every generation step off, PDF on: the session then runs step 4 alone.
            config.Step1MergeAndTimestamp.Enabled = false;
            config.Step2SpeechRefinement.Enabled = false;
            config.Step3LastRefinement.Enabled = false;
            config.PdfCompilation ??= new PdfCompilationConfig();
            config.PdfCompilation.Enabled = true;

            // The repair loop is what makes this command billable - it sends the failed document
            // and its log to the model. --fix-loop 0 keeps the command purely local.
            int? fixLoop = parseResult.GetValue(FixLoop);
            if (fixLoop is int rounds) {
                config.PdfCompilation.MaxFixRounds = rounds;
                if (rounds == 0) {
                    config.PdfCompilation.UseAntiGravityAgent = false;
                }
            }

            if (context.DryRun) {
                CliOutput.Payload(context, new {
                    dryRun = true, tex = Path.GetFullPath(tex),
                    fixLoopRounds = config.PdfCompilation.MaxFixRounds
                }, () => Ui.Info($"Würde {Path.GetFileName(tex)} kompilieren (AI-Reparatur: {config.PdfCompilation.MaxFixRounds} Runden)."));
                return ExitCodes.Success;
            }

            await RefinementUiHelper.RunRefinementAsync(config, null, Path.GetFullPath(tex), null);

            string pdfPath = Path.ChangeExtension(Path.GetFullPath(tex), ".pdf");
            bool produced = File.Exists(pdfPath);

            CliOutput.Payload(context, new { tex = Path.GetFullPath(tex), pdf = produced ? pdfPath : null, produced },
                () => { /* CompilePdfAsync reports the path it wrote. */ });

            return produced ? ExitCodes.Success : ExitCodes.Unexpected;
        });

        group.Add(compile);
        return group;
    }

    /// <summary>
    /// Uses an explicit --audio, otherwise looks for the audio track the extraction writes beside
    /// the .tex. Step 1 works without it; the timestamps are simply less accurate.
    /// </summary>
    private static string? ResolveAudio(ParseResult parseResult, string tex) {
        string? explicitAudio = parseResult.GetValue(Audio);
        if (!string.IsNullOrWhiteSpace(explicitAudio)) {
            return File.Exists(explicitAudio) ? Path.GetFullPath(explicitAudio) : null;
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(tex)) ?? ".";
        foreach (string candidate in Directory.GetFiles(folder, "*_audio.aac")) {
            return candidate;
        }

        return null;
    }
}
