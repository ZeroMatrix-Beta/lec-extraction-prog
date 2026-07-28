using System;
using System.IO;
using LectureExtraction.Extraction;
using Spectre.Console;

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
            return $"{icon} {Path.GetFileName(f)}";
        }).ToArray();

        string selectedChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold text-primary]Verfügbare Dateien im Quellordner:[/]")
                .PageSize(15)
                .AddChoices(choices)
        );

        int idx = Array.IndexOf(choices, selectedChoice);
        if (idx >= 0) {
            Ui.Success($"Ausgewähltes Ziel: {Path.GetFileName(inputFiles[idx])}");
            return [inputFiles[idx]];
        }

        Ui.Error("Invalid selection.");
        return [];
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

            var options = new List<string> {
                "[Diesen Ordner auswählen]",
                "[..] Einen Ordner nach oben"
            };

            foreach (var sd in subDirs) {
                string name = Path.GetFileName(sd);
                string cleaned = FileTreeRenderer.CleanCopySuffix(name);
                options.Add($"📁 {cleaned}/");
            }

            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold text-primary]Wähle eine Option oder einen Unterordner:[/]")
                    .PageSize(15)
                    .AddChoices(options)
            );

            if (choice == "[Diesen Ordner auswählen]") {
                return currentPath;
            }

            if (choice == "[..] Einen Ordner nach oben") {
                var parent = Directory.GetParent(currentPath);
                if (parent != null) {
                    currentPath = parent.FullName;
                }
                else {
                    Ui.Info("Übergeordneter Ordner nicht vorhanden (Root erreicht).");
                }
                continue;
            }

            string selectedFolderCleaned = choice.Replace("📁 ", "").TrimEnd('/');
            string? matchedSubDir = subDirs.FirstOrDefault(sd => FileTreeRenderer.CleanCopySuffix(Path.GetFileName(sd)) == selectedFolderCleaned || Path.GetFileName(sd) == selectedFolderCleaned);
            if (matchedSubDir != null && Directory.Exists(matchedSubDir)) {
                currentPath = matchedSubDir;
            }
        }
    }
}
