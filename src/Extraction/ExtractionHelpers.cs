using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Media;

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

    /// <summary>
    /// The folder the lecture output folders live in: the configured <c>TargetFolder</c>, or
    /// <c>&lt;SourceFolder&gt;/extracted_output</c> when none is set. Every CLI command that looks for
    /// outputs (status, clean, thoughts, run --clean-tmp) resolves it here, so they agree.
    /// </summary>
    public static string OutputRoot(IAutoExtractionConfig config) =>
        string.IsNullOrWhiteSpace(config.TargetFolder) ? Path.Combine(config.SourceFolder, "extracted_output") : config.TargetFolder;

    /// <summary>The folder a video's outputs are written to, relative to the target folder.</summary>
    public static string ComputeOutputFolderName(string videoPath) =>
        StripCompressionSuffix(StripVideoExtensions(Path.GetFileNameWithoutExtension(videoPath)));

    private static readonly string[] s_videoExtensions = [".mp4", ".mkv", ".avi", ".mov", ".webm"];

    /// <summary>
    /// [AI Context] Removes trailing video extensions left inside a name, e.g. the second ".mp4" of
    /// "video.mp4.mp4". Windows users end up with these when "Hide extensions for known file types"
    /// is on in Explorer and they type the extension themselves. Shared by the output-folder name and
    /// the refinement base name so both strip the same list.
    /// </summary>
    public static string StripVideoExtensions(string name) {
        while (Array.Exists(s_videoExtensions, ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) {
            name = Path.GetFileNameWithoutExtension(name);
        }
        return name;
    }

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

    /// <summary>
    /// A duration as the per-part prompt states it: in words plus the HH:MM:SS form the
    /// transcript's timestamps use, e.g. "32 minutes and 59 seconds (00:32:59)". The prompt used
    /// to format <see cref="TimeSpan.Minutes"/>, the minutes *component*, so a 1:30:50 lecture
    /// was announced as "30 minutes and 50 seconds" - and Gemini took that as the end of the video.
    /// </summary>
    public static string FormatDurationForPrompt(double seconds) {
        TimeSpan t = TimeSpan.FromSeconds(Math.Floor(seconds));
        int hours = (int)t.TotalHours;
        static string Unit(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";
        string words = hours > 0
            ? $"{Unit(hours, "hour")}, {Unit(t.Minutes, "minute")} and {Unit(t.Seconds, "second")}"
            : $"{Unit(t.Minutes, "minute")} and {Unit(t.Seconds, "second")}";
        return $"{words} ({hours:D2}:{t.Minutes:D2}:{t.Seconds:D2})";
    }

    [GeneratedRegex(@"-speed-[\d\.]+-compressed$", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedCompressedRegex();

    [GeneratedRegex(@"-compressed$", RegexOptions.IgnoreCase)]
    private static partial Regex CompressedRegex();

    [GeneratedRegex(@"(?:-speed-[\d\.]+-compressed|-compressed)(?:\.[a-z0-9]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PreCompressedRegex();

    /// <summary>
    /// Checks whether a file path or file name contains a pre-compressed marker
    /// (e.g. <c>-speed-1-compressed.mp4</c> or <c>-compressed.mp4</c>).
    /// </summary>
    public static bool IsPreCompressed(string pathOrFileName) =>
        PreCompressedRegex().IsMatch(pathOrFileName);

    /// <summary>
    /// Compression keeps 1 FPS; raw recordings run at 25. Anything at or below this counts as
    /// compressed, whatever preset, CRF or tool produced it.
    /// </summary>
    public const double MaxCompressedFrameRate = 2.0;

    /// <summary>
    /// Whether a video can be split as-is instead of being compressed first. A "compressed" marker in
    /// the name settles it without opening the file; otherwise the frame rate decides, so 1 FPS files
    /// made elsewhere (other presets, other names) aren't compressed a second time.
    /// </summary>
    public static async Task<bool> IsAlreadyCompressedAsync(string videoPath) {
        if (IsPreCompressed(videoPath)) return true;
        double fps = await FfmpegToolkit.GetVideoFrameRateAsync(videoPath);
        return fps > 0 && fps <= MaxCompressedFrameRate;
    }

    /// <summary>
    /// If the given video is not pre-compressed, checks if a sibling pre-compressed file
    /// with the same output folder name exists in the same directory.
    /// If found, returns the path to the pre-compressed variant; otherwise returns the original path.
    /// </summary>
    public static string ResolveCompressedVariantIfExists(string originalPath) {
        if (IsPreCompressed(originalPath)) {
            return originalPath;
        }

        string? dir = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
            return originalPath;
        }

        string targetBaseName = ComputeOutputFolderName(originalPath);
        string[] candidates = Directory.GetFiles(dir, "*.mp4");
        for (int i = 0; i < candidates.Length; i++) {
            string candidate = candidates[i];
            if (IsPreCompressed(candidate) && string.Equals(ComputeOutputFolderName(candidate), targetBaseName, StringComparison.OrdinalIgnoreCase)) {
                return candidate;
            }
        }

        return originalPath;
    }

    /// <summary>
    /// Deduplicates a collection of video paths sharing the same <see cref="ComputeOutputFolderName"/>,
    /// preferring the pre-compressed variant (<c>-speed-N-compressed.mp4</c> or <c>-compressed.mp4</c>)
    /// over the uncompressed raw file.
    /// </summary>
    public static List<string> DeduplicatePreferCompressed(IEnumerable<string> videoPaths) {
        return videoPaths
            .GroupBy(ComputeOutputFolderName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(IsPreCompressed).ThenBy(p => p).First())
            .ToList();
    }

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
            Ui.Error($"[Exception gefangen] {ex.Describe()}");
        }
    }
}
