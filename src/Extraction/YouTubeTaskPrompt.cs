using System;
using System.Collections.Generic;
using System.Globalization;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Prompts the user interactively for a YouTube URL, title, and video duration.
/// Automatically calculates overlapping time fragments if the duration exceeds 45 minutes,
/// enabling chunked transcription without physically slicing or uploading multiple video files.
/// [Human] Erlaubt es, ein YouTube-Video interaktiv per URL und Längenangabe in Fragmente zu zerlegen.
/// </summary>
public static class YouTubeTaskPrompt {
    public static YouTubeTranscriptionTask? CreateInteractiveYouTubeTask(int overlapSeconds = 180) {
        Console.Write("\nBitte gib die YouTube-URL ein: ");
        string url = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url)) {
            Ui.Warn("Keine URL eingegeben.", "Abbruch");
            return null;
        }

        Console.Write("Name / Titel für die Ausgabe (z.B. vorlesung-01) [Standard: youtube-lecture]: ");
        string name = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name)) {
            name = "youtube-lecture";
        }

        Console.Write("Wie lang ist das Video? (in Minuten, z.B. 90, oder im Format HH:MM:SS / MM:SS): ");
        string durInput = Console.ReadLine()?.Trim() ?? "";
        double totalSeconds = 0;

        if (durInput.Contains(':')) {
            string[] parts = durInput.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int s)) {
                totalSeconds = h * 3600 + m * 60 + s;
            }
            else if (parts.Length == 2 && int.TryParse(parts[0], out int m2) && int.TryParse(parts[1], out int s2)) {
                totalSeconds = m2 * 60 + s2;
            }
        }
        else if (double.TryParse(durInput, NumberStyles.Any, CultureInfo.InvariantCulture, out double mins)) {
            totalSeconds = mins * 60;
        }

        if (totalSeconds <= 0) {
            Ui.Error("Ungültige Zeitangabe. Abbruch.");
            return null;
        }

        if (overlapSeconds <= 0) {
            overlapSeconds = 180;
        }
        int maxSegmentSeconds = 45 * 60; // 45 Minuten max pro Fragment

        int numParts = 1;
        if (totalSeconds > maxSegmentSeconds) {
            numParts = (int)Math.Ceiling(totalSeconds / maxSegmentSeconds);
        }

        Console.Write($"Das Video ({totalSeconds / 60:F1} Min.) wird in {numParts} Teil(e) aufgeteilt ({overlapSeconds}s Overlap). Anzahl Teile bestätigen/ändern [{numParts}]: ");
        string partsInput = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(partsInput, out int customParts) && customParts > 0) {
            numParts = customParts;
        }

        List<YouTubeTimestampFragment> fragList = [];
        if (numParts <= 1 || totalSeconds <= overlapSeconds * 2) {
            fragList.Add(new() {
                StartTime = "00:00:00",
                EndTime = FormatSecondsToTime(totalSeconds),
                PartTitle = "Teil 1 (Komplett)"
            });
        }
        else {
            double segmentLength = (totalSeconds + (numParts - 1) * overlapSeconds) / numParts;
            for (int i = 0; i < numParts; i++) {
                double start = i * (segmentLength - overlapSeconds);
                double end = start + segmentLength;
                if (end > totalSeconds) {
                    end = totalSeconds;
                }

                fragList.Add(new() {
                    StartTime = FormatSecondsToTime(start),
                    EndTime = FormatSecondsToTime(end),
                    PartTitle = $"Teil {i + 1}"
                });
            }
        }

        Ui.Blank();
        Ui.Info($"Konstruierte {fragList.Count} Fragment(e) für die YouTube-Transkription:");
        for (int i = 0; i < fragList.Count; i++) {
            Ui.Detail($"- {fragList[i].PartTitle}: {fragList[i].StartTime} bis {fragList[i].EndTime}");
        }

        return new() {
            VideoUrl = url,
            OutputName = name,
            Fragments = fragList
        };
    }

    private static string FormatSecondsToTime(double totalSec) {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSec));
        return ts.ToString(@"hh\:mm\:ss");
    }
}
