using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        }
        return [.. allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase)];
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
        } catch (Exception ex) {
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
                sb.AppendLine("\n\n=== ATTACHED HISTORY & BENCHMARK PARTS ===");
                foreach (var part in historyParts) {
                    if (part.Text != null) sb.Append(part.Text);
                    else if (part.InlineData != null) sb.AppendLine($"\n[BINARY IMAGE PAYLOAD: {part.InlineData.MimeType}, {part.InlineData.Data?.Length ?? 0} bytes]\n");
                    else if (part.FileData != null) sb.AppendLine($"\n[REMOTE FILE URI: {part.FileData.FileUri}, {part.FileData.MimeType}]\n");
                }
            }
            if (!Directory.Exists(logFolder)) Directory.CreateDirectory(logFolder);
            string dumpPath = Path.Combine(logFolder, "system_instruction_logged.md");
            await System.IO.File.WriteAllTextAsync(dumpPath, sb.ToString());
            Console.WriteLine($"\n  📄 [LOG] System Instruction vollständig auf Festplatte geloggt unter:\n           {dumpPath}");
        } catch (Exception ex) {
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

        // Extract content inside ```latex ... ``` if present, ignoring conversational text outside
        var match = MyRegex().Match(cleanTex);
        if (match.Success) {
            cleanTex = match.Groups[1].Value;
        }
        else {
            // Fallback: just strip the markers if the regex fails to capture a clean block
            // Updated to use Source-Generated Regexes to improve performance and resolve IDE warnings
            cleanTex = LatexBlockRegex().Replace(cleanTex, "");
            cleanTex = CodeBlockRegex().Replace(cleanTex, "");
        }

        // Fuzzy regex to catch variations like "**[SYSTEM] Segment complete.**" with leading spaces or bold markers
        // Updated to use Source-Generated Regex to improve performance and resolve IDE warnings
        cleanTex = SystemMessageRegex().Replace(cleanTex, "");
        return cleanTex.Trim().FixMalformedEndTags();
    }

    /// <summary>
    /// Implements an interactive delay with user cancellation. Allows interrupting long backoff periods.
    /// </summary>
    public static async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...") {
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
                return files.Skip(startIndex).ToArray();
            }
            else {
                Console.WriteLine("[WARNUNG] Ungültige Eingabe. Starte bei Video 1.");
            }
        }

        return files;
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