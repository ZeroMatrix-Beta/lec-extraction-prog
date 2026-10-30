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
/// The duration parsing and fragment math are public so <c>lecx youtube</c> builds the same task.
/// [Human] Erlaubt es, ein YouTube-Video interaktiv per URL und Längenangabe in Fragmente zu zerlegen.
/// </summary>
public static class YouTubeTaskPrompt {
    private const int MaxSegmentSeconds = 45 * 60; // 45 Minuten max pro Fragment

    public static YouTubeTranscriptionTask? CreateInteractiveYouTubeTask(int overlapSeconds = 180) {
        string url = Ui.Ask("Bitte gib die YouTube-URL ein:", null).Trim();
        if (string.IsNullOrWhiteSpace(url)) {
            Ui.Warn("Keine URL eingegeben.", "Abbruch");
            return null;
        }

        string name = Ui.Ask("Name / Titel für die Ausgabe (z.B. vorlesung-01):", "youtube-lecture").Trim();
        if (string.IsNullOrWhiteSpace(name)) {
            name = "youtube-lecture";
        }

        string durInput = Ui.Ask("Wie lang ist das Video? (in Minuten, z.B. 90, oder im Format HH:MM:SS / MM:SS):", null).Trim();
        double totalSeconds = ParseDuration(durInput);
        if (totalSeconds <= 0) {
            Ui.Error("Ungültige Zeitangabe. Abbruch.");
            return null;
        }

        if (overlapSeconds <= 0) {
            overlapSeconds = 180;
        }

        int numParts = DefaultPartCount(totalSeconds);
        numParts = Ui.Ask($"Das Video ({totalSeconds / 60:F1} Min.) wird in {numParts} Teil(e) aufgeteilt ({overlapSeconds}s Overlap). Anzahl Teile bestätigen/ändern:", numParts);

        var fragList = BuildFragments(totalSeconds, numParts, overlapSeconds);
        ShowFragments(fragList);

        return new() {
            VideoUrl = url,
            OutputName = name,
            Fragments = fragList
        };
    }

    /// <summary>
    /// [AI Context] Reads a duration given as minutes ("90", "47.5"), "MM:SS" or "HH:MM:SS".
    /// Returns 0 for anything else, which callers treat as invalid.
    /// [Human] Liest eine Dauer (Minuten, MM:SS oder HH:MM:SS) in Sekunden; 0 = ungültig.
    /// </summary>
    public static double ParseDuration(string input) {
        input = input.Trim();
        if (input.Contains(':')) {
            string[] parts = input.Split(':');
            if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m) && int.TryParse(parts[2], out int s)) {
                return h * 3600 + m * 60 + s;
            }
            if (parts.Length == 2 && int.TryParse(parts[0], out int m2) && int.TryParse(parts[1], out int s2)) {
                return m2 * 60 + s2;
            }
            return 0;
        }
        return double.TryParse(input, NumberStyles.Any, CultureInfo.InvariantCulture, out double mins) ? mins * 60 : 0;
    }

    /// <summary>One fragment per started 45 minutes.</summary>
    public static int DefaultPartCount(double totalSeconds) =>
        totalSeconds > MaxSegmentSeconds ? (int)Math.Ceiling(totalSeconds / MaxSegmentSeconds) : 1;

    /// <summary>
    /// [AI Context] Splits the video into <paramref name="numParts"/> equally long fragments that
    /// overlap by <paramref name="overlapSeconds"/>. A video too short to overlap stays one fragment.
    /// [Human] Teilt die Videolänge in gleich lange, sich überlappende Fragmente.
    /// </summary>
    public static List<YouTubeTimestampFragment> BuildFragments(double totalSeconds, int numParts, int overlapSeconds) {
        List<YouTubeTimestampFragment> fragList = [];
        if (numParts <= 1 || totalSeconds <= overlapSeconds * 2) {
            fragList.Add(new() {
                StartTime = "00:00:00",
                EndTime = FormatSecondsToTime(totalSeconds),
                PartTitle = "Teil 1 (Komplett)"
            });
            return fragList;
        }

        double segmentLength = (totalSeconds + (numParts - 1) * overlapSeconds) / numParts;
        for (int i = 0; i < numParts; i++) {
            double start = i * (segmentLength - overlapSeconds);
            double end = Math.Min(start + segmentLength, totalSeconds);
            fragList.Add(new() {
                StartTime = FormatSecondsToTime(start),
                EndTime = FormatSecondsToTime(end),
                PartTitle = $"Teil {i + 1}"
            });
        }
        return fragList;
    }

    public static void ShowFragments(IReadOnlyList<YouTubeTimestampFragment> fragList) {
        Ui.Blank();
        Ui.Info($"Konstruierte {fragList.Count} Fragment(e) für die YouTube-Transkription:");
        foreach (var frag in fragList) {
            Ui.Detail($"- {frag.PartTitle}: {frag.StartTime} bis {frag.EndTime}");
        }
    }

    private static string FormatSecondsToTime(double totalSec) {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSec));
        return ts.ToString(@"hh\:mm\:ss");
    }
}
