using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] Implements the 'lecx clean' command to reclaim disk space by purging intermediate
/// FFmpeg segment folders ('tmp/') and safely archiving duplicate '-copy-N.tex' files produced by reruns.
/// Implements the design from Section 3 of docs/console-and-part-selection-implementation-plan.md.
/// [Human] Bereinigt temporäre FFmpeg-Ordner ('tmp/') und archiviert überflüssige Kopien ('-copy-N.tex').
/// </summary>
public static partial class CleanCommand {
    public static readonly Option<string?> Video = new("--video") {
        Description = "Clean files for a single video file or lecture name."
    };

    public static readonly Option<string?> Folder = new("--folder") {
        Description = "Target lecture directory or parent directory containing lecture output folders."
    };

    public static readonly Option<bool> Tmp = new("--tmp") {
        Description = "Delete intermediate tmp/ directories (FFmpeg segments and cache)."
    };

    public static readonly Option<bool> Copies = new("--copies") {
        Description = "Keep the newest of duplicate -copy-N.tex files as canonical and archive older copies."
    };

    public static readonly Option<bool> Force = new("--force") {
        Description = "Force cleanup even if files were modified within the last 10 minutes."
    };

    public static Command Build() {
        var command = new Command("clean", "Clean intermediate FFmpeg files or archive duplicate -copy-N files.") {
            Video, Folder, Tmp, Copies, Force
        };

        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            bool cleanTmp = parseResult.GetValue(Tmp);
            bool cleanCopies = parseResult.GetValue(Copies);
            bool force = parseResult.GetValue(Force);

            if (!cleanTmp && !cleanCopies) {
                Ui.Error("Mindestens eine Option muss angegeben werden: --tmp oder --copies.", "clean");
                Ui.Detail("Beispiel: lecx clean --folder \"<pfad>\" --tmp");
                return ExitCodes.Usage;
            }

            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            List<string> candidateDirs = ResolveTargetDirectories(parseResult, config);

            if (candidateDirs.Count == 0) {
                Ui.Warn("Keine Vorlesungsordner zum Bereinigen gefunden.", "clean");
                return ExitCodes.Success;
            }

            int skippedCount = 0;
            long totalBytesFreed = 0;
            int totalCopiesArchived = 0;
            var details = new List<CleanDirectoryResult>();

            foreach (string dir in candidateDirs) {
                string dirName = Path.GetFileName(dir);

                // 10-Minute Safety Guard
                if (!force && IsRecentlyActive(dir)) {
                    Ui.Warn($"Überspringe '{dirName}': Kürzlich aktiv (innerhalb der letzten 10 Minuten geändert). Nutze --force zum Überschreiben.", "clean");
                    skippedCount++;
                    continue;
                }

                long dirBytesFreed = 0;
                int dirCopiesArchived = 0;

                // 1. Tmp Cleanup
                if (cleanTmp) {
                    string tmpDir = Path.Combine(dir, "tmp");
                    if (Directory.Exists(tmpDir)) {
                        long tmpSize = GetDirectorySize(tmpDir);
                        dirBytesFreed += tmpSize;
                        if (!context.DryRun) {
                            try {
                                Directory.Delete(tmpDir, recursive: true);
                                Ui.Success($"Gelöscht: {dirName}/tmp ({FormatBytes(tmpSize)})", "clean");
                            }
                            catch (Exception ex) {
                                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                                Ui.Error($"Fehler beim Löschen von {tmpDir} ({ex.GetType().Name}): {ex.Message}", "clean");
                            }
                        }
                        else {
                            Ui.Detail($"[Dry-Run] Würde löschen: {dirName}/tmp ({FormatBytes(tmpSize)})", "clean");
                        }
                    }
                }

                // 2. Copies Archiving (Newest wins)
                if (cleanCopies) {
                    var (archivedCount, archivedBytes) = ProcessCopies(dir, context.DryRun);
                    dirCopiesArchived += archivedCount;
                    dirBytesFreed += archivedBytes;
                }

                totalBytesFreed += dirBytesFreed;
                totalCopiesArchived += dirCopiesArchived;
                details.Add(new CleanDirectoryResult(dirName, dirBytesFreed, dirCopiesArchived));
            }

            var summary = new {
                directoriesInspected = candidateDirs.Count,
                directoriesSkipped = skippedCount,
                bytesFreed = totalBytesFreed,
                bytesFreedHuman = FormatBytes(totalBytesFreed),
                copiesArchived = totalCopiesArchived,
                dryRun = context.DryRun,
                details
            };

            CliOutput.Payload(context, summary, () => {
                Ui.Step("Bereinigungs-Zusammenfassung");
                Ui.Detail($"Verarbeitete Ordner: {details.Count}/{candidateDirs.Count}");
                if (skippedCount > 0) Ui.Warn($"Übersprungene Ordner (aktiv): {skippedCount}");
                Ui.Success($"Freigegebener Speicher: {FormatBytes(totalBytesFreed)}");
                if (totalCopiesArchived > 0) Ui.Info($"Archivierte Kopien: {totalCopiesArchived}");
            });

            return skippedCount > 0 ? ExitCodes.Partial : ExitCodes.Success;
        });

        return command;
    }

    private static List<string> ResolveTargetDirectories(ParseResult parseResult, AiStudioAutoExtractionConfig config) {
        string? video = parseResult.GetValue(Video);
        if (!string.IsNullOrWhiteSpace(video)) {
            string baseName = ExtractionHelpers.ComputeOutputFolderName(video);
            string candidate = Path.Combine(config.TargetFolder, baseName);
            if (Directory.Exists(candidate)) return [candidate];
            if (Directory.Exists(video)) return [video];
            return [];
        }

        string? folder = parseResult.GetValue(Folder);
        string root = !string.IsNullOrWhiteSpace(folder)
            ? Path.GetFullPath(folder)
            : (string.IsNullOrWhiteSpace(config.TargetFolder) ? Path.Combine(config.SourceFolder, "extracted_output") : config.TargetFolder);

        if (!Directory.Exists(root)) return [];

        // If the directory itself is a lecture directory (contains step1 or tmp)
        if (Directory.Exists(Path.Combine(root, "tmp")) || Directory.GetFiles(root, "step1-*.tex").Length > 0) {
            return [root];
        }

        // Otherwise scan subdirectories
        var subdirs = Directory.GetDirectories(root);
        List<string> result = [];
        foreach (string subdir in subdirs) {
            if (Directory.Exists(Path.Combine(subdir, "tmp")) ||
                Directory.GetFiles(subdir, "step1-*.tex").Length > 0 ||
                Directory.GetFiles(subdir, "*.pdf").Length > 0) {
                result.Add(subdir);
            }
        }

        return result;
    }

    private static bool IsRecentlyActive(string dir) {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        try {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) {
                if (File.GetLastWriteTimeUtc(file) > cutoff) {
                    return true;
                }
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        }
        return false;
    }

    private static (int ArchivedCount, long ArchivedBytes) ProcessCopies(string dir, bool dryRun) {
        var copyFiles = Directory.GetFiles(dir, "*-copy-*.tex");
        if (copyFiles.Length == 0) return (0, 0);

        // Group by canonical name
        var groups = copyFiles
            .GroupBy(f => CopySuffixRegex().Replace(Path.GetFileName(f), ""))
            .ToList();

        int count = 0;
        long bytes = 0;
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string archiveDir = Path.Combine(dir, "archive", timestamp);

        foreach (var group in groups) {
            string canonicalFileName = group.Key;
            string canonicalPath = Path.Combine(dir, canonicalFileName);

            List<string> allVariants = [.. group];
            if (File.Exists(canonicalPath) && !allVariants.Contains(canonicalPath)) {
                allVariants.Add(canonicalPath);
            }

            // Find newest file in the group
            var newest = allVariants
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .First();

            var toArchive = allVariants.Where(f => !string.Equals(f, newest, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (string file in toArchive) {
                long size = new FileInfo(file).Length;
                bytes += size;
                count++;

                if (!dryRun) {
                    try {
                        if (!Directory.Exists(archiveDir)) Directory.CreateDirectory(archiveDir);
                        string dest = Path.Combine(archiveDir, Path.GetFileName(file));
                        File.Move(file, dest, overwrite: true);
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                    }
                }
            }

            // If the newest file wasn't the canonical file itself, ensure it becomes the canonical file
            if (!string.Equals(newest, canonicalPath, StringComparison.OrdinalIgnoreCase) && !dryRun) {
                try {
                    File.Copy(newest, canonicalPath, overwrite: true);
                    if (!Directory.Exists(archiveDir)) Directory.CreateDirectory(archiveDir);
                    string dest = Path.Combine(archiveDir, Path.GetFileName(newest));
                    File.Move(newest, dest, overwrite: true);
                }
                catch (Exception ex) {
                    Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                    Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                }
            }
        }

        if (count > 0) {
            if (dryRun) {
                Ui.Detail($"[Dry-Run] Würde {count} alte Kopien in {Path.GetFileName(dir)} archivieren ({FormatBytes(bytes)})", "clean");
            }
            else {
                Ui.Success($"Archiviert: {count} Kopien nach archive/{timestamp}/ ({FormatBytes(bytes)})", "clean");
            }
        }

        return (count, bytes);
    }

    private static long GetDirectorySize(string path) {
        try {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch {
            return 0;
        }
    }

    public static string FormatBytes(long bytes) {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    [GeneratedRegex(@"-copy-\d+")]
    private static partial Regex CopySuffixRegex();
}

public sealed record CleanDirectoryResult(string Directory, long BytesFreed, int CopiesArchived);
