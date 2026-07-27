using System;
using System.Collections.Generic;
using System.IO;
using LectureExtraction.Extraction;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Renders visual directory tree previews and maps file extensions to semantic icons.
/// </summary>
public static class DirectoryTreeRenderer {
    // [AI Context] Maps file extensions to semantic emoji icons for intuitive visual UI scanning.
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
    /// [AI Context] Renders a visual directory tree or outline depending on folder density.
    /// Small directories display all contents; large directories render a structured summary.
    /// [Human] Zeigt eine übersichtliche Vorschau des Quellordners mit Ordner- und Datei-Icons an.
    /// </summary>
    public static void DisplayDirectoryPreview(string folderPath, int maxSmallLimit = 20) {
        try {
            if (!Directory.Exists(folderPath)) {
                Console.WriteLine($"  ⚠️ [WARNUNG] Ordner existiert nicht: {folderPath}");
                return;
            }

            string[] subDirs = Directory.GetDirectories(folderPath);
            string[] files = Directory.GetFiles(folderPath);
            int totalItems = subDirs.Length + files.Length;

            if (totalItems == 0) {
                Console.WriteLine($"\n  📁 Verzeichnis-Vorschau: {folderPath} [Leerer Ordner]");
                return;
            }

            if (totalItems <= maxSmallLimit) {
                Console.WriteLine($"\n  📁 Verzeichnis-Vorschau: {folderPath} ({files.Length} Datei(en), {subDirs.Length} Ordner)");
            }
            else {
                Console.WriteLine($"\n  📁 Verzeichnis-Übersicht: {folderPath} (Insgesamt {files.Length} Datei(en), {subDirs.Length} Ordner)");
            }

            RenderDirectoryTree(folderPath, folderPath, "    ", 0, maxDepth: 4, maxSmallLimit);
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        }
    }

    /// <summary>
    /// [AI Context] Internal representation of a directory tree item for rendering previews.
    /// </summary>
    private readonly struct TreePreviewItem {
        public bool IsDir { get; init; }
        public bool IsMsg { get; init; }
        public string Name { get; init; }
        public string Path { get; init; }
    }

    /// <summary>
    /// [AI Context] Recursively renders files and folders in a clean hierarchical tree view.
    /// Limits depth and item counts dynamically based on current folder density and depth level.
    /// </summary>
    private static void RenderDirectoryTree(string rootFolder, string currentDir, string indent, int currentDepth, int maxDepth, int maxSmallLimit) {
        string[] files;
        string[] subDirs;
        try {
            files = Directory.GetFiles(currentDir);
            subDirs = Directory.GetDirectories(currentDir);
        }
        catch (Exception ex) {
            Console.WriteLine($"{indent}⚠️ [Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"{indent}Originaler Fehlertext: {ex.Message}");
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

        int total = files.Length + subDirs.Length;
        if (total == 0) return;

        int maxFilesToShow = (currentDepth == 0)
            ? (total <= maxSmallLimit ? 50 : 15)
            : 10;
        int maxDirsToShow = (currentDepth == 0)
            ? (total <= maxSmallLimit ? 50 : 10)
            : 5;

        int shownFiles = Math.Min(files.Length, maxFilesToShow);
        bool hasMoreFiles = files.Length > shownFiles;
        int shownDirs = Math.Min(subDirs.Length, maxDirsToShow);
        bool hasMoreDirs = subDirs.Length > shownDirs;

        List<TreePreviewItem> items = [];

        // 1. Files first
        for (int i = 0; i < shownFiles; i++) {
            string fileName = Path.GetFileName(files[i]);
            string cleanedFileName = FileTreeRenderer.CleanCopySuffix(fileName);
            items.Add(new() { IsDir = false, IsMsg = false, Name = cleanedFileName, Path = files[i] });
        }
        if (hasMoreFiles) {
            items.Add(new() { IsDir = false, IsMsg = true, Name = $"... und {files.Length - shownFiles} weitere Datei(en)", Path = "" });
        }

        // 2. Folders next
        if (currentDepth < maxDepth) {
            for (int i = 0; i < shownDirs; i++) {
                string dirName = Path.GetFileName(subDirs[i]);
                string cleanedDirName = FileTreeRenderer.CleanCopySuffix(dirName);
                items.Add(new() { IsDir = true, IsMsg = false, Name = cleanedDirName, Path = subDirs[i] });
            }
            if (hasMoreDirs) {
                items.Add(new() { IsDir = true, IsMsg = true, Name = $"... und {subDirs.Length - shownDirs} weitere Unterordner", Path = "" });
            }
        }
        else if (subDirs.Length > 0) {
            items.Add(new() { IsDir = true, IsMsg = true, Name = $"... ({subDirs.Length} Unterordner)", Path = "" });
        }

        for (int i = 0; i < items.Count; i++) {
            var item = items[i];
            bool isLast = (i == items.Count - 1);
            string branch = isLast ? "└── " : "├── ";

            if (item.IsMsg) {
                if (item.IsDir) {
                    Console.WriteLine($"{indent}{branch}📁 {item.Name}");
                }
                else {
                    Console.WriteLine($"{indent}{branch}💬 {item.Name}");
                }
            }
            else if (item.IsDir) {
                Console.WriteLine($"{indent}{branch}📁 {item.Name}/");
                string childIndent = indent + (isLast ? "    " : "│   ");
                RenderDirectoryTree(rootFolder, item.Path, childIndent, currentDepth + 1, maxDepth, maxSmallLimit);
            }
            else {
                string ext = Path.GetExtension(item.Path).ToLowerInvariant();
                string icon = GetFileIcon(ext);
                string rawRelPath = Path.GetRelativePath(rootFolder, item.Path);
                string relPath = FileTreeRenderer.NormalizeRelativePath(rawRelPath);
                string label = (!string.IsNullOrEmpty(relPath) && !string.Equals(relPath, item.Name, StringComparison.OrdinalIgnoreCase))
                    ? $"{item.Name} ({relPath})"
                    : item.Name;
                Console.WriteLine($"{indent}{branch}{icon} {label}");
            }
        }
    }
}
