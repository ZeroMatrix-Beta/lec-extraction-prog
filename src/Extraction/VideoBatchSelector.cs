using System;
using System.IO;
using System.Linq;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Media;
using Spectre.Console;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Finds all MP4 videos in the source folder, sorts them chronologically,
/// and prompts the user whether to start at Video 1 or pick a specific starting video.
/// [Human] Sucht alle MP4-Dateien und fragt, ob bei Video 1 oder einem späteren Video begonnen werden soll.
/// </summary>
public static class VideoBatchSelector {
    public static string[] SelectAndFilterVideosForBatch(string sourceFolder) {
        if (!Directory.Exists(sourceFolder)) {
            Ui.Error($"Der Ordner '{sourceFolder}' existiert nicht.");
            return [];
        }

        var files = Directory.GetFiles(sourceFolder, "*.mp4")
                             .OrderBy(f => VideoDateParser.Parse(f).Date)
                             .ThenBy(f => VideoDateParser.Parse(f).WeekNumber ?? int.MaxValue)
                             .ThenBy(f => f)
                             .ToArray();

        if (files.Length == 0) {
            Ui.Info($"Keine MP4-Videos im Ordner '{sourceFolder}' gefunden.");
            return [];
        }

        Ui.Info($"Es wurden {files.Length} MP4-Video(s) im Quellordner gefunden.");
        Ui.Detail($"Erstes Video: {files[0]}");

        bool startAtFirst = AnsiConsole.Confirm("Möchten Sie bei Video 1 beginnen?", true);
        if (!startAtFirst) {
            var videoChoices = files.Select((f, idx) => $"{idx + 1}) {Path.GetFileName(f)}").ToArray();
            string selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold text-primary]Bitte wählen Sie das Video aus, bei dem gestartet werden soll:[/]")
                    .PageSize(15)
                    .AddChoices(videoChoices)
            );

            int startNum = Array.IndexOf(videoChoices, selected) + 1;
            if (startNum >= 1 && startNum <= files.Length) {
                int startIndex = startNum - 1;
                Ui.Info($"Starte Batch-Verarbeitung ab Video {startNum}: {Path.GetFileName(files[startIndex])}");
                return [.. files.Skip(startIndex)];
            }
        }

        return files;
    }
}
