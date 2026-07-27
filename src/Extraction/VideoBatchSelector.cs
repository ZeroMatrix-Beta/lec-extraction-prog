using System;
using System.IO;
using System.Linq;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Finds all MP4 videos in the source folder, sorts them chronologically,
/// and prompts the user whether to start at Video 1 or pick a specific starting video.
/// [Human] Sucht alle MP4-Dateien und fragt, ob bei Video 1 oder einem späteren Video begonnen werden soll.
/// </summary>
public static class VideoBatchSelector {
    public static string[] SelectAndFilterVideosForBatch(string sourceFolder) {
        if (!Directory.Exists(sourceFolder)) {
            Console.WriteLine($"[FEHLER] Der Ordner '{sourceFolder}' existiert nicht.");
            return [];
        }

        var files = Directory.GetFiles(sourceFolder, "*.mp4")
                             .OrderBy(f => VideoDateParser.Parse(f).Date)
                             .ThenBy(f => VideoDateParser.Parse(f).WeekNumber ?? int.MaxValue)
                             .ThenBy(f => f)
                             .ToArray();

        if (files.Length == 0) {
            Console.WriteLine($"[INFO] Keine MP4-Videos im Ordner '{sourceFolder}' gefunden.");
            return [];
        }

        Console.WriteLine($"\nEs wurden {files.Length} MP4-Video(s) im Quellordner gefunden.");
        Console.WriteLine($"Erstes Video: {files[0]}");
        Console.Write("Möchten Sie bei Video 1 beginnen? (j/n, Standard: j): ");

        string input = Console.ReadLine()?.Trim().ToLower() ?? "";
        if (input == "n" || input == "nein") {
            Console.WriteLine("\nBitte wählen Sie das Video aus, bei dem gestartet werden soll:");
            for (int i = 0; i < files.Length; i++) {
                Console.WriteLine($"  {i + 1}) {Path.GetFileName(files[i])}");
            }
            Console.Write($"Start-Nummer (1-{files.Length}): ");
            if (int.TryParse(Console.ReadLine()?.Trim(), out int startNum) && startNum >= 1 && startNum <= files.Length) {
                int startIndex = startNum - 1;
                Console.WriteLine($"\nStarte Batch-Verarbeitung ab Video {startNum}: {Path.GetFileName(files[startIndex])}");
                return [.. files.Skip(startIndex)];
            }
            else {
                Console.WriteLine("[WARNUNG] Ungültige Eingabe. Starte bei Video 1.");
            }
        }

        return files;
    }
}
