using System;
using System.IO;
using LectureExtraction.Extraction;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Interactive prompts for picking files or navigating into a folder from the console.
/// </summary>
public static class FileSelectionPrompt {
    public static string[] SelectSingleFile(string sourceFolder) {
        string[] inputFiles = Directory.GetFiles(sourceFolder);
        if (inputFiles.Length == 0) {
            Ui.Warn("No files found in the source folder.");
            return [];
        }

        var choices = inputFiles.Select(f => {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            string icon = DirectoryTreeRenderer.GetFileIcon(ext);
            return ($"{icon} {Path.GetFileName(f)}", f);
        });

        var selection = Ui.Select("Verfügbare Dateien im Quellordner:", choices, pageSize: 15);
        if (!selection.IsValue) return [];

        Ui.Success($"Ausgewähltes Ziel: {Path.GetFileName(selection.Value!)}");
        return [selection.Value!];
    }

    public static string[] SelectBatchFiles(string sourceFolder) {
        string[] inputFiles = Directory.GetFiles(sourceFolder);
        if (inputFiles.Length == 0) {
            Ui.Warn("Keine Dateien im Quellordner gefunden.");
            return [];
        }

        Ui.Success($"{inputFiles.Length} Datei(en) für die Stapelverarbeitung gefunden.");
        return inputFiles;
    }

    public static string NavigateDirectory(string startingDir) {
        string currentPath = startingDir;
        if (!Directory.Exists(currentPath)) {
            currentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        while (true) {
            Ui.Info($"Aktueller Pfad: {currentPath}", "Ordnernavigation");

            string[] subDirs = [];
            try {
                subDirs = Directory.GetDirectories(currentPath);
            }
            catch (Exception ex) {
                Ui.Warn($"Fehler beim Lesen des Ordners: {ex.Message}");
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

            var options = new List<(string Label, string? Value)> {
                ("[Diesen Ordner auswählen]", currentPath),
                ("[..] Einen Ordner nach oben", null)
            };

            foreach (var sd in subDirs) {
                string cleaned = FileTreeRenderer.CleanCopySuffix(Path.GetFileName(sd));
                options.Add(($"📁 {cleaned}/", sd));
            }

            // No back entry: ".." *is* the back navigation here, and "Diesen Ordner auswählen"
            // with the unchanged starting folder is the way out without changing anything.
            var choice = Ui.Select("Wähle eine Option oder einen Unterordner:", options, allowBack: false, pageSize: 15);

            if (choice.Value == null) {
                var parent = Directory.GetParent(currentPath);
                if (parent != null) {
                    currentPath = parent.FullName;
                }
                else {
                    Ui.Info("Übergeordneter Ordner nicht vorhanden (Root erreicht).");
                }
                continue;
            }

            if (choice.Value == currentPath) {
                return currentPath;
            }

            if (Directory.Exists(choice.Value)) {
                currentPath = choice.Value;
            }
        }
    }
}
