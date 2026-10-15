using System;
using System.IO;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] A short, sortable name for a lecture in tables and one-line reports, e.g.
/// "2026-02-16 Mo W1", derived from the date, weekday and week in the video or folder name. The full
/// names (~55-80 characters, "...-Analysis_II-_mehrere_Variablen-speed-1-compressed") made every
/// table row wrap; the full name stays in --json payloads and in file paths.
/// [Human] Kurzer Vorlesungsname für Tabellen, z.B. "2026-02-16 Mo W1".
/// </summary>
public static class LectureLabel {
    private static readonly string[] GermanDayAbbreviations = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    /// <summary>The short label, or the name itself when it carries no date.</summary>
    public static string Short(string videoOrFolderName) {
        string name = Path.GetFileNameWithoutExtension(videoOrFolderName);
        if (!VideoDateParser.HasDate(name)) {
            return name;
        }

        var info = VideoDateParser.Parse(name);
        string label = info.Date.ToString("yyyy-MM-dd");
        // The weekday the name states wins over the calendar's: it is how the lecture is known, and a
        // mismatch is the name's to fix, not the label's to hide.
        DayOfWeek day = Enum.TryParse(info.WeekdayEnglish, ignoreCase: true, out DayOfWeek named) ? named : info.Date.DayOfWeek;
        label += " " + GermanDayAbbreviations[(int)day];
        if (info.WeekNumber is int week) {
            label += $" W{week}";
        }
        return label;
    }
}
