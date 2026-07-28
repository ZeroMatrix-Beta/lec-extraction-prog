using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Resolves configured history/system-instruction path lists (files and directories)
/// into concrete file lists, and groups them into batches for chunked loading.
/// </summary>
public static class HistoryFileResolver {
    /// <summary>
    /// Resolves an array of mixed file/directory paths into a distinct list of absolute file paths.
    /// </summary>
    public static List<string> ResolveHistoryFiles(string[] paths) {
        List<string> allHistoryFiles = [];
        if (paths == null) return allHistoryFiles;

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))) {
            if (File.Exists(path))
                allHistoryFiles.Add(Path.GetFullPath(path));
            else if (Directory.Exists(path))
                allHistoryFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f)));
            else
                Ui.Warn($"HistoryPreloadPath nicht gefunden (weder Datei noch Ordner): {path}");
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
}
