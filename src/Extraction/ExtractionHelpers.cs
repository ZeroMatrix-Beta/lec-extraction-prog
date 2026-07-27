using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Shared utility methods to reduce code duplication across different extraction session types.
/// </summary>
public static class ExtractionHelpers {
    /// <summary>
    /// [AI Context] Was copy-pasted byte-identically in both extraction sessions before being
    /// consolidated here. Appends "-copy-N" if the target .tex path already exists on disk.
    /// [Human] Haengt "-copy-N" an den Zielpfad an, falls die Datei schon existiert. War vorher 2x dupliziert.
    /// </summary>
    public static string GetUniqueTexPath(string originalPath) {
        if (!File.Exists(originalPath)) {
            return originalPath;
        }

        Console.WriteLine($"  [Hinweis] Zieldatei '{Path.GetFileName(originalPath)}' existiert bereits.");
        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);
        int copyIndex = 1;
        string newPath;
        do {
            newPath = Path.Combine(dir, $"{baseName}-copy-{copyIndex}{ext}");
            copyIndex++;
        } while (File.Exists(newPath));

        Console.WriteLine($"  [Info] Neue Datei wird erstellt: '{Path.GetFileName(newPath)}'");
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
            Console.WriteLine($"\n  📄 [LOG] System Instruction vollständig auf Festplatte geloggt unter:\n           {dumpPath}");
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        }
    }
}
