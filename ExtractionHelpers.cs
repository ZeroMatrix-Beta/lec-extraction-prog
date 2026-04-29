using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Shared utility methods to reduce code duplication across different extraction session types.
/// </summary>
internal static class ExtractionHelpers {
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
    return allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
  }

  /// <summary>
  /// [AI Context] Regex-based cleanup ensures that even if the output is split across multiple continuation chunks,
  /// all markdown blocks and system messages are fully stripped, preventing compilation errors.
  /// </summary>
  public static string CleanLatexResponse(string rawResponse) {
    string cleanTex = rawResponse;
    cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"```latex\r?\n?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"```\r?\n?", "");
    // Fuzzy regex to catch variations like "**[SYSTEM] Segment complete.**" with leading spaces or bold markers
    cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"(?im)^[ \t]*(?:\*|_|%)*\[(?:SYSTEM|AI-MODEL)[^\]]*\][^\r\n]*(?:Segment|Video)\s*complete[^\r\n]*\r?\n?", "");
    return cleanTex.Trim();
  }

  /// <summary>
  /// Implements an interactive delay with user cancellation. Allows interrupting long backoff periods.
  /// </summary>
  public static async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...") {
    bool delayCanceled = false;
    ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; delayCanceled = true; };
    Console.CancelKeyPress += cancelHandler;
    try {
      int delaySteps = seconds * 10;
      for (int i = 0; i < delaySteps; i++) {
        if (delayCanceled) return false;
        await Task.Delay(100);
        if (!Console.IsInputRedirected && Console.KeyAvailable) {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
          Console.WriteLine($"\n[AI-Model] {message}");
        }
      }
      return true;
    }
    finally {
      Console.CancelKeyPress -= cancelHandler;
    }
  }
}