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
        var allHistoryFiles = new List<string>();
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
    /// Prints a list of file paths in a structured, folder-like tree format.
    /// </summary>
    public static void PrintFileTree(List<string> filePaths) {
        if (filePaths == null || filePaths.Count == 0) return;

        var grouped = filePaths.OrderBy(p => p).GroupBy(p => Path.GetDirectoryName(p));

        foreach (var group in grouped) {
            Console.WriteLine($"  📁 {group.Key}");
            var files = group.ToList();
            for (int i = 0; i < files.Count; i++) {
                string filename = Path.GetFileName(files[i]);
                string prefix = (i == files.Count - 1) ? "└──" : "├──";
                Console.WriteLine($"      {prefix} 📄 {filename}");
            }
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
        try {
            int delaySteps = seconds * 10;
            for (int i = 0; i < delaySteps; i++) {
                if (delayCanceled) return false;
                await Task.Delay(100);
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
            return true;
        }
        finally {
            IsInSmartDelay = false;
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    [GeneratedRegexAttribute(@"```(?:latex|tex)?\s*\n(.*?)\n```", RegexOptions.IgnoreCase | RegexOptions.Singleline, "de-CH")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();

    [GeneratedRegex(@"```(?:latex|tex)?\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex LatexBlockRegex();

    [GeneratedRegex(@"```\r?\n?")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"(?im)^[ \t]*(?:\*|_|%)*\[(?:SYSTEM|AI-MODEL)[^\]]*\][^\r\n]*(?:Segment|Video)\s*complete[^\r\n]*\r?\n?")]
    private static partial Regex SystemMessageRegex();
}