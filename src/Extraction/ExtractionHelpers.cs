using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Config;
using Infrastructure;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Shared utility methods to reduce code duplication across different extraction session types.
/// </summary>
public static partial class ExtractionHelpers {
    // [AI Context] Globale Flag, um Input-Intercepting-Tasks (z.B. im REPL) während eines Delays zu pausieren
    // Fixed IDE warning: Non-constant fields should not be visible. Converted to a property with a volatile backing field for thread safety.
    private static volatile bool _isInSmartDelay = false;
    public static bool IsInSmartDelay {
        get => _isInSmartDelay;
        set => _isInSmartDelay = value;
    }

    // [AI Context] Tracks the UTC timestamp when the last model completion / file generation finished across any extraction or refinement step.
    public static DateTime LastGenerationCompletionTimeUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Resolves an array of mixed file/directory paths into a distinct list of absolute file paths.
    /// </summary>
    public static List<string> ResolveHistoryFiles(string[] paths) {
        List<string> allHistoryFiles = [];
        if (paths == null) return allHistoryFiles;

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))) {
            if (System.IO.File.Exists(path))
                allHistoryFiles.Add(Path.GetFullPath(path));
            else if (Directory.Exists(path))
                allHistoryFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f)));
            else
                Console.WriteLine($"  [WARNUNG] HistoryPreloadPath nicht gefunden (weder Datei noch Ordner): {path}");
        }
        return [.. allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// [AI Context] Groups a flat list of resolved history files into at most <paramref name="batchCount"/> batches.
    /// Step 1: Files are grouped by their top-level subfolder relative to any HistoryPreloadPath root.
    /// Step 2: Those subfolder-groups are distributed evenly into batchCount buckets (chunked).
    ///         If batchCount &lt;= 1, all files are returned as a single batch.
    ///         If batchCount &gt;= subfolder count, each subfolder is its own batch (one-per-subfolder mode).
    /// Each result entry has a human-readable label listing the subfolders it contains.
    /// [Human] Verteilt die History-Subfolders gleichmäßig auf batchCount Batches.
    /// </summary>
    public static List<(string GroupLabel, List<string> Files)> GroupHistoryFilesByTopLevelSubfolder(
        List<string> files, string[] historyPreloadPaths, int batchCount) {

        if (files == null || files.Count == 0) return [];

        // --- Step 1: group files by their relative directory path ---
        var folderGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var folderOrder = new List<string>();

        foreach (var file in files) {
            string groupKey = "";
            foreach (var rootPath in historyPreloadPaths.Where(p => !string.IsNullOrWhiteSpace(p))) {
                if (!Directory.Exists(rootPath)) continue;
                string root = Path.GetFullPath(rootPath);
                string fullFile = Path.GetFullPath(file);
                if (fullFile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullFile.Equals(root, StringComparison.OrdinalIgnoreCase)) {
                    string relative = Path.GetRelativePath(root, fullFile);
                    string? dirName = Path.GetDirectoryName(relative);
                    groupKey = !string.IsNullOrEmpty(dirName) ? dirName : "";
                    break;
                }
            }
            if (!folderGroups.TryGetValue(groupKey, out var groupList)) {
                groupList = [];
                folderGroups[groupKey] = groupList;
                folderOrder.Add(groupKey);
            }
            groupList.Add(file);
        }

        // Build ordered list of (folderKey, files) - root files first
        var ordered = new List<(string Key, List<string> Files)>();
        if (folderGroups.TryGetValue("", out var rootFiles)) ordered.Add(("(root)", rootFiles));
        foreach (var key in folderOrder.Where(k => k != "")) ordered.Add((key, folderGroups[key]));

        // --- Step 2: if batchCount <= 1, return single batch ---
        if (batchCount <= 1 || ordered.Count == 0) {
            var allFiles = ordered.SelectMany(g => g.Files).ToList();
            string label = ordered.Count > 0 ? string.Join(", ", ordered.Select(g => g.Key)) : "(alle)";
            return [(label, allFiles)];
        }

        // --- Step 3: distribute folder groups deterministically across batchCount buckets ---
        int effectiveBatches = Math.Min(batchCount, ordered.Count);
        var result = new List<(string, List<string>)>();

        for (int b = 0; b < effectiveBatches; b++) {
            int start = b * ordered.Count / effectiveBatches;
            int end = (b + 1) * ordered.Count / effectiveBatches;
            var bucket = ordered.GetRange(start, end - start);
            if (bucket.Count > 0) {
                var displayNames = bucket.Select(g => Path.GetFileName(g.Key)).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToList();
                string bucketLabel = displayNames.Count > 0 ? string.Join(" + ", displayNames) : "(root)";
                var bucketFiles = bucket.SelectMany(g => g.Files).ToList();
                result.Add((bucketLabel, bucketFiles));
            }
        }
        return result;
    }


    /// <summary>
    /// [AI Context] Prints a list of file paths in a structured, hierarchical tree format showing files first, then folders.
    /// </summary>
    public static void PrintFileTree(List<string> filePaths) {
        if (filePaths == null || filePaths.Count == 0) return;

        string? baseDir = FindCommonBaseDirectory(filePaths);
        var root = BuildVirtualTree(filePaths, baseDir);
        if (!string.IsNullOrEmpty(baseDir)) {
            Console.WriteLine($"  📁 {baseDir}");
        }
        RenderVirtualTreeNode(root, "      ", showRelativePath: false, Console.WriteLine);
    }

    public static string? FindCommonBaseDirectory(List<string> allPaths) {
        if (allPaths == null || allPaths.Count == 0) return null;
        try {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(allPaths[0])) ?? "";
            foreach (var path in allPaths) {
                string fullPath = Path.GetFullPath(path);
                while (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(baseDir)) {
                    baseDir = Path.GetDirectoryName(baseDir) ?? "";
                }
            }
            return string.IsNullOrEmpty(baseDir) ? null : baseDir;
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            return null;
        }
    }

    public static string GenerateMarkdownFileTree(List<string> filePaths, string? baseDir) {
        if (filePaths == null || filePaths.Count == 0) return "";

        if (string.IsNullOrEmpty(baseDir)) {
            baseDir = FindCommonBaseDirectory(filePaths);
        }

        var root = BuildVirtualTree(filePaths, baseDir);
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(baseDir)) {
            sb.AppendLine($"  📁 {baseDir}");
        }
        RenderVirtualTreeNode(root, "      ", showRelativePath: true, line => sb.AppendLine(line));
        return sb.ToString();
    }

    /// <summary>
    /// [AI Context] Represents a node in an in-memory virtual directory tree constructed from a list of file paths.
    /// Used to generate sorted, hierarchical visual layouts for console output and system prompts.
    /// </summary>
    private sealed class VirtualTreeNode {
        public string Name { get; set; } = "";
        public string? RelativePath { get; set; }
        public bool IsDirectory { get; set; }
        public Dictionary<string, VirtualTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [AI Context] Builds a hierarchical virtual tree structure from a flat list of file paths,
    /// splitting by directory separators relative to a given base directory.
    /// </summary>
    private static VirtualTreeNode BuildVirtualTree(List<string> filePaths, string? baseDir) {
        var root = new VirtualTreeNode { IsDirectory = true };

        foreach (var path in filePaths) {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string relPath = !string.IsNullOrEmpty(baseDir)
                ? Path.GetRelativePath(baseDir, path).Replace('\\', '/')
                : path.Replace('\\', '/');

            if (relPath == "." || relPath == string.Empty) continue;

            string cleanedRelPath = CleanCopySuffix(relPath);
            string normalizedRelPath = NormalizeRelativePath(cleanedRelPath);

            string[] parts = cleanedRelPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var current = root;
            for (int i = 0; i < parts.Length; i++) {
                string part = parts[i];
                bool isFile = (i == parts.Length - 1);

                if (!current.Children.TryGetValue(part, out VirtualTreeNode? child)) {
                    child = new() {
                        Name = part,
                        IsDirectory = !isFile,
                        RelativePath = isFile ? normalizedRelPath : null
                    };
                    current.Children[part] = child;
                }
                current = child;
            }
        }

        return root;
    }

    /// <summary>
    /// [AI Context] Normalizes a relative path to follow Unix conventions:
    /// uses forward slashes, prefixes with "./", and removes common copy suffixes like " - Kopie" or " - Copy".
    /// </summary>
    public static string NormalizeRelativePath(string relPath) {
        if (string.IsNullOrEmpty(relPath)) return relPath;

        string normalized = relPath.Replace('\\', '/');
        normalized = CleanCopySuffix(normalized);

        if (normalized.StartsWith("./", StringComparison.Ordinal)) {
            normalized = normalized[2..];
        }

        if (normalized.StartsWith('/')) {
            return "." + normalized;
        }
        return "./" + normalized;
    }

    /// <summary>
    /// [AI Context] Removes common copy suffixes (such as " - Kopie" or "-Copy") case-insensitively using a robust Regex.
    /// </summary>
    public static string CleanCopySuffix(string input) {
        if (string.IsNullOrEmpty(input)) return input;
        return CopySuffixRegex().Replace(input, "");
    }

    /// <summary>
    /// [AI Context] Recursively renders virtual tree nodes with visual branching characters (├──, └──).
    /// Files are displayed first, followed by directories, with optional inclusion of full relative paths.
    /// </summary>
    private static void RenderVirtualTreeNode(VirtualTreeNode node, string indent, bool showRelativePath, Action<string> writeLine) {
        var children = node.Children.Values
            .OrderBy(c => c.IsDirectory ? 1 : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < children.Count; i++) {
            var child = children[i];
            bool isLast = (i == children.Count - 1);
            string branch = isLast ? "└── " : "├── ";

            if (child.IsDirectory) {
                writeLine($"{indent}{branch}📁 {child.Name}/");
                string childIndent = indent + (isLast ? "    " : "│   ");
                RenderVirtualTreeNode(child, childIndent, showRelativePath, writeLine);
            }
            else {
                string ext = Path.GetExtension(child.Name).ToLowerInvariant();
                string icon = FfmpegUtilities.ConsoleUiHelper.GetFileIcon(ext);
                string label = (showRelativePath && !string.IsNullOrEmpty(child.RelativePath) && !string.Equals(child.RelativePath, child.Name, StringComparison.OrdinalIgnoreCase))
                    ? $"{child.Name} ({child.RelativePath})"
                    : child.Name;
                writeLine($"{indent}{branch}{icon} {label}");
            }
        }
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
            Console.WriteLine($"\n  📄 [LOG] System Instruction vollständig auf Festplatte geloggt unter:\n           {dumpPath}");
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        }
    }

    /// <summary>
    /// [AI Context] Regex-based cleanup ensures that even if the output is split across multiple continuation chunks,
    /// all markdown blocks and system messages are fully stripped, preventing compilation errors.
    /// </summary>
    public static string CleanLatexResponse(string rawResponse) {
        string cleanTex = rawResponse;

        // We use a non-greedy regex to capture everything inside the blocks, allowing multiple blocks.
        var matches = MyRegex().Matches(cleanTex);
        if (matches.Count > 0) {
            var sb = new System.Text.StringBuilder();
            foreach (System.Text.RegularExpressions.Match match in matches) {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(match.Groups[1].Value);
            }
            cleanTex = sb.ToString();
        }

        // Always strip any remaining markdown code block markers (even if we extracted a block),
        // because the model might have split the LaTeX code into multiple consecutive markdown blocks.
        cleanTex = LatexBlockRegex().Replace(cleanTex, "");
        cleanTex = CodeBlockRegex().Replace(cleanTex, "");

        // Fuzzy regex to catch variations like "**[SYSTEM] Segment complete.**" with leading spaces or bold markers
        // Updated to use Source-Generated Regex to improve performance and resolve IDE warnings
        cleanTex = SystemMessageRegex().Replace(cleanTex, "");
        return cleanTex.Trim().FixMalformedEndTags();
    }

    /// <summary>
    /// Implements an interactive delay with user cancellation. Allows interrupting long backoff periods.
    /// </summary>
    public static async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...") {
        Console.WriteLine($"\n⏳ [SmartDelay] Warte {seconds} Sekunden: {message}");
        Console.WriteLine("   (Tipp: Du kannst jederzeit [Enter] drücken, um die Wartezeit sofort zu überspringen.)");
        bool delayCanceled = false;
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; delayCanceled = true; }
        Console.CancelKeyPress += cancelHandler;
        IsInSmartDelay = true;
        using var cts = new CancellationTokenSource();
        try {
            var delayTask = Task.Run(async () => {
                int delaySteps = seconds * 10;
                for (int i = 0; i < delaySteps; i++) {
                    if (delayCanceled || cts.Token.IsCancellationRequested) return false;
                    await Task.Delay(100, cts.Token);
                    try {
                        if (!Console.IsInputRedirected && Console.KeyAvailable) {
                            bool enterPressed = false;
                            while (Console.KeyAvailable) {
                                var keyInfo = Console.ReadKey(intercept: true);
                                if (keyInfo.Key == ConsoleKey.Enter) enterPressed = true;
                            }
                            if (enterPressed) {
                                Console.WriteLine("\n[Skip] Wartezeit durch Benutzer (Enter) übersprungen.");
                                return true;
                            }
                            Console.WriteLine($"\n[AI-Model] {message} (Oder drücke Enter für sofortigen Retry/Skip)");
                        }
                    }
                    catch { }
                }
                return true;
            }, cts.Token);

            var inputTask = Task.Run(async () => {
                try {
                    while (!cts.Token.IsCancellationRequested) {
                        bool isRedirected = false;
                        try { isRedirected = Console.IsInputRedirected; } catch { }

                        if (!isRedirected) {
                            await Task.Delay(200, cts.Token);
                            continue;
                        }

                        // [AI Context] When running inside redirected consoles (e.g., IDE terminal, pseudo-terminal),
                        // Console.KeyAvailable throws or returns false. We use ReadLineAsync with cancellation.
                        // [Human] In IDE-Terminals (wie VS Code oder Antigravity) ist die Konsole umgeleitet. Damit Enter trotzdem funktioniert, lesen wir hier asynchron die Eingabe.
                        var lineTask = Console.In.ReadLineAsync(cts.Token).AsTask();
                        await lineTask;
                        return true;
                    }
                }
                catch { }
                return false;
            }, cts.Token);

            var completedTask = await Task.WhenAny(delayTask, inputTask);
            cts.Cancel(); // Cancel the other task

            if (completedTask == inputTask && await inputTask) {
                Console.WriteLine("\n[Skip] Wartezeit durch Benutzer (Enter) übersprungen.");
                return true;
            }

            return await delayTask;
        }
        finally {
            IsInSmartDelay = false;
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// [AI Context] Finds all MP4 videos in the source folder, sorts them chronologically,
    /// and prompts the user whether to start at Video 1 or pick a specific starting video.
    /// [Human] Sucht alle MP4-Dateien und fragt, ob bei Video 1 oder einem späteren Video begonnen werden soll.
    /// </summary>
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

    /// <summary>
    /// [AI Context] Prompts the user interactively for a YouTube URL, title, and video duration.
    /// Automatically calculates overlapping time fragments if the duration exceeds 45 minutes,
    /// enabling chunked transcription without physically slicing or uploading multiple video files.
    /// [Human] Erlaubt es, ein YouTube-Video interaktiv per URL und Längenangabe in Fragmente zu zerlegen.
    /// </summary>
    public static YouTubeTranscriptionTask? CreateInteractiveYouTubeTask(int overlapSeconds = 180) {
        Console.Write("\nBitte gib die YouTube-URL ein: ");
        string url = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url)) {
            Console.WriteLine("[Abbruch] Keine URL eingegeben.");
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
        else if (double.TryParse(durInput, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double mins)) {
            totalSeconds = mins * 60;
        }

        if (totalSeconds <= 0) {
            Console.WriteLine("[Fehler] Ungültige Zeitangabe. Abbruch.");
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

        Console.WriteLine($"\n[INFO] Konstruierte {fragList.Count} Fragment(e) für die YouTube-Transkription:");
        for (int i = 0; i < fragList.Count; i++) {
            Console.WriteLine($"   - {fragList[i].PartTitle}: {fragList[i].StartTime} bis {fragList[i].EndTime}");
        }

        return new() {
            VideoUrl = url,
            OutputName = name,
            Fragments = fragList
        };
    }

    /// <summary>
    /// [AI Context] Synchronizes the model selected in the AutoExtraction session to all refinement steps
    /// in the LatexRefinementSessionConfig, and persists both configurations so the entire pipeline stays unified.
    /// [Human] Synchronisiert das ausgewählte Modell auf alle Schritte des LaTeX-Refinements und speichert beide Config-Dateien ab.
    /// </summary>
    public static void SyncModelToRefinementConfig(string modelName, bool isVertex, LatexRefinementSessionConfig? inMemoryConfig = null) {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        var refConfig = inMemoryConfig ?? ConfigLoader<LatexRefinementSessionConfig>.Load();
        if (isVertex) {
            refConfig.Step1MergeAndTimestamp.Vertex.CurrentModel = modelName;
            refConfig.Step2SpeechRefinement.Vertex.CurrentModel = modelName;
            refConfig.Step3LastRefinement.Vertex.CurrentModel = modelName;
        }
        else {
            refConfig.Step1MergeAndTimestamp.AiStudio.CurrentModel = modelName;
            refConfig.Step2SpeechRefinement.AiStudio.CurrentModel = modelName;
            refConfig.Step3LastRefinement.AiStudio.CurrentModel = modelName;
        }
        ConfigLoader<LatexRefinementSessionConfig>.Save(refConfig);
    }

    private static string FormatSecondsToTime(double totalSec) {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSec));
        return ts.ToString(@"hh\:mm\:ss");
    }

    [GeneratedRegexAttribute(@"```(?:latex|tex)?\s*\n(.*?)\n```", RegexOptions.IgnoreCase | RegexOptions.Singleline, "de-CH")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();

    [GeneratedRegex(@"```(?:latex|tex)?\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex LatexBlockRegex();

    [GeneratedRegex(@"```\r?\n?")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"(?im)^[ \t]*(?:\*|_|%)*\[(?:SYSTEM|AI-MODEL)[^\]]*\][^\r\n]*(?:Segment|Video)\s*complete[^\r\n]*\r?\n?")]
    private static partial Regex SystemMessageRegex();

    [GeneratedRegex(@"\s*[\-\u2010-\u2015]\s*(?:Kopie|Copy)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffixRegex();
}