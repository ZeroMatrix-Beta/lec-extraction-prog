using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] Implements the 'lecx status' command to quickly inspect pipeline progress
/// (transcription parts, refinement steps 1-3, compiled PDF, tmp cache size) across lecture folders.
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
        var command = new Command("status", "Inspect extraction, refinement and PDF progress for lecture directories.") {
            Video, Folder
        };

        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));

            List<string> candidateDirs = ResolveTargetDirectories(parseResult, config);

            if (candidateDirs.Count == 0) {
                Ui.Warn("Keine Vorlesungsordner gefunden.", "status");
                return ExitCodes.Success;
            }

            var statuses = new List<LectureStatusInfo>();

            foreach (string dir in candidateDirs.OrderBy(d => Path.GetFileName(d))) {
                string lecture = Path.GetFileName(dir);

                var partFiles = Directory.GetFiles(dir, "step1-*-part*.tex");
                int partsCount = partFiles.Length > 0
                    ? partFiles.Select(f => Path.GetFileName(f)).Where(f => !f.Contains("-offset")).Distinct().Count()
                    : 0;

                bool stitched = Directory.GetFiles(dir, "step1-*-all*.tex").Length > 0;
                bool step1Merged = Directory.GetFiles(dir, "step2-*-merged.tex").Length > 0;
                bool step2Speech = Directory.GetFiles(dir, "step3-*-speech_refined.tex").Length > 0;
                bool step3Polish = Directory.GetFiles(dir, "step4-*-final.tex").Length > 0;

                string? pdfPath = Directory.GetFiles(dir, "*.pdf")
                    .FirstOrDefault(f => Path.GetFileName(f).Equals($"{lecture}.pdf", StringComparison.OrdinalIgnoreCase))
                    ?? Directory.GetFiles(dir, "step4-*-final.pdf").FirstOrDefault()
                    ?? Directory.GetFiles(dir, "step5-*-last_try*.pdf").FirstOrDefault();

                long pdfSize = pdfPath != null && File.Exists(pdfPath) ? new FileInfo(pdfPath).Length : 0;
                bool pdfReady = pdfSize > 0;

                string tmpDir = Path.Combine(dir, "tmp");
                long tmpBytes = Directory.Exists(tmpDir) ? GetDirectorySize(tmpDir) : 0;

                statuses.Add(new LectureStatusInfo(
                    Lecture: lecture,
                    PartsCount: partsCount,
                    Stitched: stitched,
                    Step1Merged: step1Merged,
                    Step2SpeechRefined: step2Speech,
                    Step3Polish: step3Polish,
                    PdfReady: pdfReady,
                    PdfPath: pdfPath,
                    PdfSize: pdfSize,
                    PdfSizeHuman: pdfReady ? CleanCommand.FormatBytes(pdfSize) : "-",
                    TmpBytes: tmpBytes,
                    TmpSizeHuman: tmpBytes > 0 ? CleanCommand.FormatBytes(tmpBytes) : "0 B"
                ));
            }

            CliOutput.Payload(context, statuses, () => RenderTable(statuses));
            return ExitCodes.Success;
        });

        return command;
    }

    private static void RenderTable(IReadOnlyList<LectureStatusInfo> statuses) {
        Ui.Step($"Vorlesungs-Status ({statuses.Count} Ordner)");

        var rows = new List<(string Label, string Value)>();
        foreach (var s in statuses) {
            string progress = s.PdfReady ? "100% (PDF)" :
                              s.Step3Polish ? "Step 3 (Polish)" :
                              s.Step2SpeechRefined ? "Step 2 (Speech)" :
                              s.Step1Merged ? "Step 1 (Merge)" :
                              s.Stitched ? "Transkribiert" :
                              s.PartsCount > 0 ? $"{s.PartsCount} Teile" : "Leer";

            string details = $"Status: {progress} | PDF: {s.PdfSizeHuman} | Tmp: {s.TmpSizeHuman}";
            rows.Add((s.Lecture, details));
        }

        Ui.Table("Status-Übersicht", rows);
    }

    private static List<string> ResolveTargetDirectories(ParseResult parseResult, AiStudioAutoExtractionConfig config) {
        string? video = parseResult.GetValue(Video);
        if (!string.IsNullOrWhiteSpace(video)) {
            string baseName = ExtractionHelpers.ComputeOutputFolderName(video);
            string candidate = Path.Combine(config.TargetFolder, baseName);
            if (Directory.Exists(candidate)) return [candidate];
            if (Directory.Exists(video)) return [video];
            return [];
        }

        string? folder = parseResult.GetValue(Folder);
        string root = !string.IsNullOrWhiteSpace(folder)
            ? Path.GetFullPath(folder)
            : (string.IsNullOrWhiteSpace(config.TargetFolder) ? Path.Combine(config.SourceFolder, "extracted_output") : config.TargetFolder);

        if (!Directory.Exists(root)) return [];

        if (Directory.Exists(Path.Combine(root, "tmp")) || Directory.GetFiles(root, "step1-*.tex").Length > 0) {
            return [root];
        }

        var subdirs = Directory.GetDirectories(root);
        List<string> result = [];
        foreach (string subdir in subdirs) {
            if (Directory.Exists(Path.Combine(subdir, "tmp")) ||
                Directory.GetFiles(subdir, "step1-*.tex").Length > 0 ||
                Directory.GetFiles(subdir, "*.pdf").Length > 0) {
                result.Add(subdir);
            }
        }

        return result;
    }

    private static long GetDirectorySize(string path) {
        try {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch {
            return 0;
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
    string TmpSizeHuman);
