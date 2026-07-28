using System;
using System.IO;
using System.Linq;
using LectureExtraction.Media;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] File-type icons, and a one-glance summary of a source folder.
///
/// <para>This used to render a full recursive directory tree every time a folder was picked. That
/// was removed (2026-07-29): the lecture folders nest several levels deep and hold 50+ videos with
/// long, near-identical names, so the tree buried the prompt it was meant to introduce. Choosing a
/// folder now just confirms the choice; the summary below is shown once, later in setup, where a
/// reminder of what is about to be processed is actually useful.</para>
/// [Human] Datei-Icons und eine kompakte Ordner-Zusammenfassung. Der frühere rekursive
/// Verzeichnisbaum wurde entfernt - er war bei 50+ Videos schlicht eine Dateiflut.
/// </summary>
public static class DirectoryTreeRenderer {
    public static string GetFileIcon(string ext) => ext switch {
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "🎬",
        ".mp3" or ".wav" or ".m4a" or ".flac" or ".aac" => "🎵",
        ".pdf" => "📕",
        ".tex" or ".bib" or ".md" or ".txt" or ".doc" or ".docx" => "📄",
        ".cs" or ".json" or ".py" or ".js" or ".html" or ".css" => "💻",
        ".zip" or ".tar" or ".gz" or ".rar" => "📦",
        ".png" or ".jpg" or ".jpeg" or ".svg" => "🖼️",
        _ => "📎"
    };

    /// <summary>
    /// [AI Context] Three lines at most: the folder, how much is in it, and the date range of the
    /// videos it holds. The date range is the genuinely useful part - it answers "is this the
    /// semester I meant?" without listing anything.
    /// [Human] Höchstens drei Zeilen: Ordner, Umfang, und der Zeitraum der enthaltenen Videos.
    /// </summary>
    public static void DisplayFolderSummary(string folderPath) {
        try {
            if (!Directory.Exists(folderPath)) {
                Ui.Warn($"Ordner existiert nicht: {folderPath}");
                return;
            }

            var videos = Directory.GetFiles(folderPath, "*.mp4");
            int otherFiles = Directory.GetFiles(folderPath).Length - videos.Length;
            int subDirs = Directory.GetDirectories(folderPath).Length;

            Ui.Info($"Quellordner: {folderPath}");

            if (videos.Length == 0) {
                Ui.Detail($"Keine MP4-Videos · {otherFiles} sonstige Datei(en) · {subDirs} Unterordner");
                return;
            }

            Ui.Detail($"{videos.Length} Video(s) · {otherFiles} sonstige Datei(en) · {subDirs} Unterordner");

            var dates = videos.Select(v => VideoDateParser.Parse(v).Date)
                              .Where(d => d != DateTime.MinValue)
                              .OrderBy(d => d)
                              .ToArray();
            if (dates.Length > 0) {
                string range = dates[0] == dates[^1]
                    ? dates[0].ToString("yyyy-MM-dd")
                    : $"{dates[0]:yyyy-MM-dd} bis {dates[^1]:yyyy-MM-dd}";
                Ui.Detail($"Zeitraum: {range}");
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Ordner konnte nicht gelesen werden: {ex.GetType().Name} - {ex.Message}");
        }
    }
}
