using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.Infrastructure;

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

    public static readonly Option<bool> Pdfs = new("--pdfs") {
        Description = "Keep one finished PDF per lecture (<lecture>.pdf) and archive byte-identical copies of it."
    };

    public static readonly Option<bool> Force = new("--force") {
        Description = "Force cleanup even if files were modified within the last 10 minutes."
    };

    public static Command Build() {
        var command = new Command("clean", "Clean intermediate FFmpeg files, archive duplicate -copy-N files or duplicate PDFs.") {
            Video, Folder, Tmp, Copies, Pdfs, Force
        };

        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            bool cleanTmp = parseResult.GetValue(Tmp);
            bool cleanCopies = parseResult.GetValue(Copies);
            bool cleanPdfs = parseResult.GetValue(Pdfs);
            bool force = parseResult.GetValue(Force);

            if (!cleanTmp && !cleanCopies && !cleanPdfs) {
                Ui.Error("Mindestens eine Option muss angegeben werden: --tmp, --copies oder --pdfs.", "clean");
                Ui.Detail("Beispiel: lecx clean --folder \"<pfad>\" --tmp");
                return ExitCodes.Usage;
            }

            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            List<string> candidateDirs = LectureFolders.Resolve(parseResult.GetValue(Video), parseResult.GetValue(Folder), config);

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
                                Ui.Error($"Fehler beim Löschen von {tmpDir}: {ex.Describe()}", "clean");
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

                // 3. One finished PDF per lecture
                if (cleanPdfs) {
                    var (archivedPdfs, pdfBytes, _) = ProcessPdfs(dir, context.DryRun);
                    dirCopiesArchived += archivedPdfs;
                    dirBytesFreed += pdfBytes;
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
            Ui.Warn($"Aktivitätsprüfung für '{dir}' unvollständig: {ex.Describe()}", "clean");
        }
        return false;
    }

    internal static (int ArchivedCount, long ArchivedBytes) ProcessCopies(string dir, bool dryRun) {
        var copyFiles = Directory.GetFiles(dir, "*-copy*.tex")
            .Where(f => CopySuffixRegex().IsMatch(Path.GetFileNameWithoutExtension(f)))
            .ToArray();
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
                        Ui.Warn($"Kopie '{Path.GetFileName(file)}' konnte nicht archiviert werden: {ex.Describe()}", "clean");
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
                    Ui.Warn($"Neueste Kopie '{Path.GetFileName(newest)}' konnte nicht übernommen werden: {ex.Describe()}", "clean");
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

    /// <summary>
    /// [AI Context] Leaves one finished PDF per lecture, &lt;lecture&gt;.pdf. Until 2026-10-15 a run wrote
    /// the same PDF twice (beside step 3's .tex and as the "final" copy), and a re-run named the final
    /// one after its -copy-N stem. Creates &lt;lecture&gt;.pdf from the newest finished PDF when it is
    /// missing, then archives only byte-identical copies of it; a PDF that differs (an older draft, a
    /// precheck build) is left where it is.
    /// [Human] Behält ein fertiges PDF pro Vorlesung und archiviert identische Kopien.
    /// </summary>
    public static (int ArchivedCount, long ArchivedBytes, bool Created) ProcessPdfs(string dir, bool dryRun) {
        string lecture = Path.GetFileName(dir);
        string canonical = Path.Combine(dir, lecture + ".pdf");
        var others = Directory.GetFiles(dir, "*.pdf")
            .Where(f => !string.Equals(f, canonical, StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("-main", StringComparison.OrdinalIgnoreCase))
            .ToList();

        bool created = false;
        string? source = canonical;
        if (!File.Exists(canonical)) {
            // Only what a finished run writes: the final copy (named after the lecture) or step 4's.
            source = others
                .Where(f => Path.GetFileName(f).StartsWith(lecture, StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(f).StartsWith("step4-", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(f).StartsWith("step5-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (source == null) return (0, 0, false);
            created = true;
            if (dryRun) {
                Ui.Detail($"[Dry-Run] Würde {Path.GetFileName(source)} als {lecture}.pdf übernehmen", "clean");
            }
            else {
                File.Copy(source, canonical);
                Ui.Success($"{lecture}.pdf aus {Path.GetFileName(source)} erstellt", "clean");
            }
        }

        var duplicates = others.Where(f => SameBytes(f, source)).ToList();
        long bytes = duplicates.Sum(f => new FileInfo(f).Length);
        if (duplicates.Count == 0) return (0, 0, created);

        string archiveDir = Path.Combine(dir, "archive", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        if (dryRun) {
            Ui.Detail($"[Dry-Run] Würde {duplicates.Count} identische PDF-Kopie(n) in {lecture} archivieren: {string.Join(", ", duplicates.Select(Path.GetFileName))}", "clean");
            return (duplicates.Count, bytes, created);
        }

        Directory.CreateDirectory(archiveDir);
        foreach (string file in duplicates) {
            File.Move(file, Path.Combine(archiveDir, Path.GetFileName(file)), overwrite: true);
        }
        Ui.Success($"Archiviert: {duplicates.Count} identische PDF-Kopie(n) nach archive/{Path.GetFileName(archiveDir)}/ ({FormatBytes(bytes)})", "clean");
        return (duplicates.Count, bytes, created);
    }

    private static bool SameBytes(string a, string b) {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    private static long GetDirectorySize(string path) {
        try {
            return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) {
            Ui.Warn($"Größe von '{path}' konnte nicht berechnet werden: {ex.Describe()}", "clean");
            return 0;
        }
    }

    public static string FormatBytes(long bytes) {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    [GeneratedRegex(@"-copy-?\d+")]
    private static partial Regex CopySuffixRegex();
}

public sealed record CleanDirectoryResult(string Directory, long BytesFreed, int CopiesArchived);
