using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Shared utility methods to reduce code duplication across different extraction session types.
/// </summary>
public static partial class ExtractionHelpers {
    /// <summary>
    /// Strips the <c>-speed-N-compressed</c> / <c>-compressed</c> suffixes FFmpeg preprocessing
    /// adds. This is the name of a video's output folder, and the stem every output file is built
    /// from - so it has to be derived identically wherever it is needed. It previously existed as
    /// two private copies (the segment producer's and each session's) plus the same two regexes
    /// declared twice.
    /// </summary>
    public static string StripCompressionSuffix(string fileNameWithoutExtension) {
        string stripped = SpeedCompressedRegex().Replace(fileNameWithoutExtension, "");
        return CompressedRegex().Replace(stripped, "");
    }

    /// <summary>The folder a video's outputs are written to, relative to the target folder.</summary>
    public static string ComputeOutputFolderName(string videoPath) =>
        StripCompressionSuffix(Path.GetFileNameWithoutExtension(videoPath));

    /// <summary>
    /// The stem of the per-part <c>.tex</c> files, which carries a <c>step1-</c> prefix marking it
    /// as the first pipeline stage's output. Note this differs from the output *folder* name, which
    /// has no prefix - a distinction worth stating, because a caller predicting file paths from the
    /// folder name alone would look for the wrong files.
    /// </summary>
    public static string ComputeTexBaseName(string videoPath) {
        string baseName = ComputeOutputFolderName(videoPath);
        return baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase) ? baseName : "step1-" + baseName;
    }

    [GeneratedRegex(@"-speed-[\d\.]+-compressed$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedCompressedRegex();

    [GeneratedRegex(@"-compressed$", RegexOptions.IgnoreCase)]
    private static partial Regex CompressedRegex();

    public static string ResolveNonClashingTexPath(string originalPath) {
        if (!File.Exists(originalPath)) {
            return originalPath;
        }

        Ui.Info($"Zieldatei '{Path.GetFileName(originalPath)}' existiert bereits.", "Hinweis");
        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);
        int copyIndex = 1;
        string newPath;
        do {
            newPath = Path.Combine(dir, $"{baseName}-copy-{copyIndex}{ext}");
            copyIndex++;
        } while (File.Exists(newPath));

        Ui.Info($"Neue Datei wird erstellt: '{Path.GetFileName(newPath)}'");
        return newPath;
    }

    public static async Task LogSystemInstructionDumpAsync(string logFolder, string systemInstructionText, List<Google.GenAI.Types.Part> historyParts) {
        try {
            var sb = new System.Text.StringBuilder(systemInstructionText);
            if (historyParts != null && historyParts.Count > 0) {
                sb.AppendLine("\n\n<attached_history_and_benchmark_parts>");
                foreach (var part in historyParts) {
                    if (part.Text != null) sb.Append(part.Text);
                    else if (part.InlineData != null) sb.AppendLine($"\n[BINARY IMAGE PAYLOAD: {part.InlineData.MimeType}, {part.InlineData.Data?.Length ?? 0} bytes]\n");
                    else if (part.FileData != null) sb.AppendLine($"\n[REMOTE FILE URI: {part.FileData.FileUri}, {part.FileData.MimeType}]\n");
                }
                sb.AppendLine("\n</attached_history_and_benchmark_parts>");
            }
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            string dumpPath = Path.Combine(logFolder, "system_instruction_logged.md");
            await System.IO.File.WriteAllTextAsync(dumpPath, sb.ToString());
            Ui.Info($"System Instruction vollständig auf Festplatte geloggt unter: {dumpPath}", "LOG");
        }
        catch (Exception ex) {
            Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}");
        }
    }
}
