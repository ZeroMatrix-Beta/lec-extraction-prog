using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] Implements the 'lecx status' command to quickly inspect pipeline progress
/// (transcription parts, refinement steps 1-3, compiled PDF, tmp cache size) across lecture folders.
/// Reads each folder through <see cref="LectureProgress"/>, the same scan `finish` acts on, and names
/// the next stage, so "what is left" and "what finish would do" are one answer.
/// [Human] Zeigt den Status und Fortschritt von Vorlesungen (Transkription, Refinement, PDF) an.
/// </summary>
public static class StatusCommand {
    public static readonly Option<string?> Video = new("--video") {
        Description = "Inspect status for a single video file or lecture name."
    };

    public static readonly Option<string?> Folder = new("--folder") {
        Description = "Lecture directory or parent directory containing lecture output folders."
    };

    public static Command Build() {
        var command = new Command("status", "Inspect extraction, refinement and PDF progress for lecture directories, and the next stage of each.") {
            Video, Folder
        };

        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));

            List<string> candidateDirs = LectureFolders.Resolve(parseResult.GetValue(Video), parseResult.GetValue(Folder), config);
            if (candidateDirs.Count == 0) {
                Ui.Warn("Keine Vorlesungsordner gefunden.", "status");
                return ExitCodes.Success;
            }

            var progress = candidateDirs.Select(LectureProgress.Scan).ToList();
            var statuses = progress.Select(ToInfo).ToList();

            CliOutput.Payload(context, statuses, () => RenderTable(progress));
            return ExitCodes.Success;
        });

        return command;
    }

    private static LectureStatusInfo ToInfo(LectureProgress p) => new(
        Lecture: p.Lecture,
        PartsCount: p.PartsCount,
        Stitched: p.CombinedTex != null,
        Step1Merged: p.MergedTex != null,
        Step2SpeechRefined: p.SpeechTex != null,
        Step3Polish: p.FinalTex != null,
        PdfReady: p.Pdf != null,
        PdfPath: p.Pdf,
        PdfSize: p.Pdf != null ? new System.IO.FileInfo(p.Pdf).Length : 0,
        PdfSizeHuman: p.Pdf != null ? CleanCommand.FormatBytes(new System.IO.FileInfo(p.Pdf).Length) : "-",
        TmpBytes: p.TmpBytes,
        TmpSizeHuman: p.TmpBytes > 0 ? CleanCommand.FormatBytes(p.TmpBytes) : "0 B",
        NextStep: p.NextStage.ToString(),
        NextInput: p.NextInput);

    private static void RenderTable(IReadOnlyList<LectureProgress> progress) {
        var rows = progress.Select(p => (IReadOnlyList<string>)[
            LectureLabel.Short(p.Lecture),
            p.ReachedLabel,
            p.NextStage == LectureStage.Done ? "-" : LectureProgress.StageLabel(p.NextStage),
            p.Pdf != null ? CleanCommand.FormatBytes(new System.IO.FileInfo(p.Pdf).Length) : "-",
            p.TmpBytes > 0 ? CleanCommand.FormatBytes(p.TmpBytes) : "-"
        ]);
        Ui.Grid($"Vorlesungs-Status ({progress.Count} Ordner)", ["Vorlesung", "Stand", "Als Nächstes", "PDF", "tmp"], rows);

        int open = progress.Count(p => p.NextStage is not (LectureStage.Done or LectureStage.NotStarted));
        if (open > 0) {
            Ui.Detail($"{open} Vorlesung(en) begonnen, aber nicht fertig: 'lecx finish --dry-run' zeigt, was als Nächstes laufen würde.");
        }
    }
}

public sealed record LectureStatusInfo(
    string Lecture,
    int PartsCount,
    bool Stitched,
    bool Step1Merged,
    bool Step2SpeechRefined,
    bool Step3Polish,
    bool PdfReady,
    string? PdfPath,
    long PdfSize,
    string PdfSizeHuman,
    long TmpBytes,
    string TmpSizeHuman,
    string NextStep,
    string? NextInput);
