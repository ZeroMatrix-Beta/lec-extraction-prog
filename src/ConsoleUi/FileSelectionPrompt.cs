using System;
using System.IO;
using LectureExtraction.Extraction;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Interactive prompts for picking files or navigating into a folder from the console.
/// </summary>
public static class FileSelectionPrompt {
    // [AI Context] Interactive file picker returning a single-element array for uniform batch processing compatibility.
    public static string[] SelectSingleFile(string sourceFolder) {
        string[] inputFiles = Directory.GetFiles(sourceFolder);
        if (inputFiles.Length == 0) {
            Console.WriteLine("No files found in the source folder.");
            return [];
        }

        Console.WriteLine("\n📁 Verfügbare Dateien im Quellordner:");
        for (int i = 0; i < inputFiles.Length; i++) {
            string ext = Path.GetExtension(inputFiles[i]).ToLowerInvariant();
            string icon = DirectoryTreeRenderer.GetFileIcon(ext);
            Console.WriteLine($"  {i + 1}. {icon} {Path.GetFileName(inputFiles[i])}");
        }

        Console.Write("\nBitte Datei auswählen (Nummer eingeben): ");
        if (int.TryParse(Console.ReadLine(), out int fileIndex) && fileIndex > 0 && fileIndex <= inputFiles.Length) {
            Console.WriteLine($"\n  🎯 Ausgewähltes Ziel: {Path.GetFileName(inputFiles[fileIndex - 1])}");
            return [inputFiles[fileIndex - 1]];
        }

        Console.WriteLine("Invalid selection.");
        return [];
    }

    // [AI Context] Passive loader. Grabs all valid elements within a flat directory for batch operations.
    public static string[] SelectBatchFiles(string sourceFolder) {
        string[] inputFiles = Directory.GetFiles(sourceFolder);
        if (inputFiles.Length == 0) {
            Console.WriteLine("  [WARNUNG] Keine Dateien im Quellordner gefunden.");
            return [];
        }

        Console.WriteLine($"\n  🚀 {inputFiles.Length} Datei(en) für die Stapelverarbeitung gefunden.");
        return inputFiles;
    }

    /// <summary>
    /// [AI Context] Interactive CLI folder browser allowing the user to descend into subfolders, switch drives, or select directories.
    /// </summary>
    public static string NavigateDirectory(string startingDir) {
        string currentPath = startingDir;
        if (!Directory.Exists(currentPath)) {
            currentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        while (true) {
            Console.WriteLine($"\n--------------------------------------------------");
            Console.WriteLine($" 📂 Aktueller Pfad: {currentPath}");
            Console.WriteLine($"--------------------------------------------------");

            string[] subDirs = [];
            try {
                subDirs = Directory.GetDirectories(currentPath);
            }
            catch (Exception ex) {
                Console.WriteLine($"  [WARNUNG] Fehler beim Lesen des Ordners: {ex.Message}");
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

            Console.WriteLine(" Navigationstipps:");
            Console.WriteLine("  [ENTER] / [s]  => Diesen Ordner auswählen");
            Console.WriteLine("  [..]           => Einen Ordner nach oben gehen");
            Console.WriteLine("  [d:] / [c:]    => Laufwerk wechseln (z. B. 'd:' eingeben)");
            Console.WriteLine("  [Pfadname]     => Unterordner-Name direkt eingeben");

            int shownLimit = 50;
            int count = Math.Min(subDirs.Length, shownLimit);
            if (count > 0) {
                Console.WriteLine($"\nUnterordner (1-{count}):");
                for (int i = 0; i < count; i++) {
                    string name = Path.GetFileName(subDirs[i]);
                    string cleaned = FileTreeRenderer.CleanCopySuffix(name);
                    Console.WriteLine($"  {i + 1}) {cleaned}/");
                }
                if (subDirs.Length > shownLimit) {
                    Console.WriteLine($"  ... und {subDirs.Length - shownLimit} weitere Unterordner");
                }
            }
            else {
                Console.WriteLine("\n(Keine Unterordner vorhanden)");
            }

            Console.Write("\nEingabe (Nummer, Befehl oder Pfad): ");
            string input = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(input) || input.Equals("s", StringComparison.OrdinalIgnoreCase)) {
                return currentPath;
            }

            if (input == "..") {
                var parent = Directory.GetParent(currentPath);
                if (parent != null) {
                    currentPath = parent.FullName;
                }
                else {
                    Console.WriteLine("  [INFO] Übergeordneter Ordner nicht vorhanden (Root erreicht).");
                }
                continue;
            }

            if (input.Length == 2 && input[1] == ':' && char.IsLetter(input[0])) {
                string drivePath = input.ToUpper() + "\\";
                if (Directory.Exists(drivePath)) {
                    currentPath = drivePath;
                }
                else {
                    Console.WriteLine($"  [WARNUNG] Laufwerk '{input}' ist nicht bereit.");
                }
                continue;
            }

            if (int.TryParse(input, out int num) && num >= 1 && num <= count) {
                currentPath = subDirs[num - 1];
                continue;
            }

            string cleanInputPath = input.Trim('\"', '\'');
            if (Directory.Exists(cleanInputPath)) {
                currentPath = Path.GetFullPath(cleanInputPath);
            }
            else {
                string combined = Path.Combine(currentPath, cleanInputPath);
                if (Directory.Exists(combined)) {
                    currentPath = Path.GetFullPath(combined);
                }
                else {
                    Console.WriteLine($"  [WARNUNG] Der Pfad '{input}' wurde nicht gefunden.");
                }
            }
        }
    }
}
