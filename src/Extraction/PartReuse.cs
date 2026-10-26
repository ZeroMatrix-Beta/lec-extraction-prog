using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] The one rule that decides whether a transcribed part is reused from disk instead of
/// being sent to the API again. <c>TranscribeSegmentsAsync</c> applies it to the part it is about to
/// transcribe and to the part it pre-uploads, and <c>ExtractionPlanner</c> applies it to predict the
/// run, so the plan and the run cannot disagree about what will be billed.
///
/// <para>A part that is transcribed again while its file still exists is written next to it as
/// <c>&lt;name&gt;-partN-copy-K.tex</c> (<see cref="ExtractionHelpers.ResolveNonClashingTexPath"/>).
/// The check used to look at <c>&lt;name&gt;-partN.tex</c> only, so once the original had aged out of
/// the window every later run found the same stale file, paid for the part again and added one more
/// copy. The newest of the original and its copies holds the part's latest text, so that file is the
/// one whose age is checked and the one that is reused.</para>
/// [Human] Entscheidet, ob ein Teil von der Festplatte wiederverwendet wird: die neueste Datei aus
/// partN.tex und ihren -copy-N-Kopien, sofern sie jünger als das Resume-Fenster ist.
/// </summary>
public static partial class PartReuse {
    /// <summary>
    /// Returns the newest of <c>&lt;texBaseName&gt;-partN.tex</c> and its <c>-copy-K</c> siblings, or
    /// null when there is none. On equal write times the original wins over its copies.
    /// </summary>
    public static string? FindNewestPartFile(string outputFolder, string texBaseName, int partNumber) {
        if (!Directory.Exists(outputFolder)) {
            return null;
        }

        string stem = $"{texBaseName}-part{partNumber}";
        return Directory.EnumerateFiles(outputFolder, $"{stem}*.tex")
            .Where(path => IsPartFileOrCopy(Path.GetFileNameWithoutExtension(path), stem))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenBy(path => Path.GetFileName(path).Length)
            .FirstOrDefault();
    }

    /// <summary>
    /// Returns the part file a run reuses instead of transcribing part <paramref name="partNumber"/>
    /// again: the newest file from <see cref="FindNewestPartFile"/>, if it was written within
    /// <paramref name="window"/>. Otherwise null, and the part is transcribed.
    /// </summary>
    public static string? FindReusablePart(string outputFolder, string texBaseName, int partNumber, TimeSpan window) {
        string? newest = FindNewestPartFile(outputFolder, texBaseName, partNumber);
        return newest != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(newest) <= window ? newest : null;
    }

    // "-part1" must not pick up "-part11" or "-part1-offset": only the bare stem and "-copy-K" count.
    private static bool IsPartFileOrCopy(string fileNameWithoutExtension, string stem) =>
        fileNameWithoutExtension.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
        && (fileNameWithoutExtension.Length == stem.Length
            || CopySuffixRegex().IsMatch(fileNameWithoutExtension[stem.Length..]));

    [GeneratedRegex(@"^-copy-?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffixRegex();
}
