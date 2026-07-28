using System;
using System.Collections.Generic;
using System.IO;
using LectureExtraction.Extraction;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Renders visual directory tree previews and maps file extensions to semantic icons.
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

    public static void DisplayDirectoryPreview(string folderPath, int maxSmallLimit = 20) {
        try {
            if (!Directory.Exists(folderPath)) {
                Ui.Warn($"Ordner existiert nicht: {folderPath}");
                return;
            }

            string[] subDirs = Directory.GetDirectories(folderPath);
            string[] files = Directory.GetFiles(folderPath);
            int totalItems = subDirs.Length + files.Length;

            if (totalItems == 0) {
                Ui.Info($"Verzeichnis-Vorschau: {folderPath} [Leerer Ordner]");
                return;
            }

            if (totalItems <= maxSmallLimit) {
                Ui.Info($"Verzeichnis-Vorschau: {folderPath} ({files.Length} Datei(en), {subDirs.Length} Ordner)");
            }
            else {
                Ui.Info($"Verzeichnis-Übersicht: {folderPath} (Insgesamt {files.Length} Datei(en), {subDirs.Length} Ordner)");
            }

            RenderDirectoryTree(folderPath, folderPath, "    ", 0, maxDepth: 4, maxSmallLimit);
        }
        catch (Exception ex) {
            Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private readonly struct TreePreviewItem {
        public bool IsDir { get; init; }
        public bool IsMsg { get; init; }
        public string Name { get; init; }
        public string Path { get; init; }
    }

    private static void RenderDirectoryTree(string rootFolder, string currentDir, string indent, int currentDepth, int maxDepth, int maxSmallLimit) {
        string[] files;
        string[] subDirs;
        try {
            files = Directory.GetFiles(currentDir);
            subDirs = Directory.GetDirectories(currentDir);
        }
        catch (Exception ex) {
            Ui.Warn($"{indent}[Exception gefangen] {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

        int total = files.Length + subDirs.Length;
        if (total == 0) return;

        // [AI Context] This preview runs every time a folder menu is opened, so it is a glance, not
        // an inventory - the "... und N weitere" line below carries the real count. A lecture
        // source folder holds 50+ videos with long names; printing them all buried the menu it was
        // meant to introduce.
        // [Human] Die Vorschau soll einen schnellen Eindruck geben, keine vollständige Liste - die
        // Gesamtzahl steht in der Kopfzeile und in "... und N weitere".
        int maxFilesToShow = (currentDepth == 0)
            ? (total <= maxSmallLimit ? 12 : 8)
            : 5;
        int maxDirsToShow = (currentDepth == 0)
            ? (total <= maxSmallLimit ? 12 : 8)
            : 5;

        int shownFiles = Math.Min(files.Length, maxFilesToShow);
        bool hasMoreFiles = files.Length > shownFiles;
        int shownDirs = Math.Min(subDirs.Length, maxDirsToShow);
        bool hasMoreDirs = subDirs.Length > shownDirs;

        List<TreePreviewItem> items = [];

        for (int i = 0; i < shownFiles; i++) {
            string fileName = Path.GetFileName(files[i]);
            string cleanedFileName = FileTreeRenderer.CleanCopySuffix(fileName);
            items.Add(new() { IsDir = false, IsMsg = false, Name = cleanedFileName, Path = files[i] });
        }
        if (hasMoreFiles) {
            items.Add(new() { IsDir = false, IsMsg = true, Name = $"... und {files.Length - shownFiles} weitere Datei(en)", Path = "" });
        }

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
                    Ui.Detail($"{indent}{branch}📁 {item.Name}");
                }
                else {
                    Ui.Detail($"{indent}{branch}💬 {item.Name}");
                }
            }
            else if (item.IsDir) {
                Ui.Detail($"{indent}{branch}📁 {item.Name}/");
                string childIndent = indent + (isLast ? "    " : "│   ");
                RenderDirectoryTree(rootFolder, item.Path, childIndent, currentDepth + 1, maxDepth, maxSmallLimit);
            }
            else {
                string ext = Path.GetExtension(item.Path).ToLowerInvariant();
                string icon = GetFileIcon(ext);
                string rawRelPath = Path.GetRelativePath(rootFolder, item.Path);

                // [AI Context] The relative path only carries information when the file sits in a
                // subfolder - for a file directly in the root it just repeats the name. The old
                // guard compared against the normalized path, which is prefixed with "./", so it
                // never matched and every top-level file printed as "name.mp4 (./name.mp4)".
                // [Human] Der relative Pfad wird nur angezeigt, wenn die Datei in einem Unterordner
                // liegt - sonst wiederholt er nur den Dateinamen.
                bool isInSubfolder = rawRelPath.Contains(Path.DirectorySeparatorChar) || rawRelPath.Contains(Path.AltDirectorySeparatorChar);
                string label = isInSubfolder
                    ? $"{item.Name} ({FileTreeRenderer.NormalizeRelativePath(rawRelPath)})"
                    : item.Name;
                Ui.Detail($"{indent}{branch}{icon} {label}");
            }
        }
    }
}
