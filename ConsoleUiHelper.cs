using System;
using System.IO;

namespace FfmpegUtilities {
    /// <summary>
    /// [AI Context] Encapsulates UI/Console rendering logic away from core processing loops.
    /// Ensures the FfmpegToolkit remains completely headless.
    /// [Human] Hilfsklasse, um saubere Textmenüs für die Datei-Auswahl zu zeichnen, ohne den eigentlichen Converter-Code zu vermüllen.
    /// </summary>
    public static class ConsoleUiHelper {
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
                string icon = GetFileIcon(ext);
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
                    for (int i = 0; i < subDirs.Length; i++) {
                        string dirName = Path.GetFileName(subDirs[i]);
                        Console.WriteLine($"    📁 {dirName}/");
                    }
                    for (int i = 0; i < files.Length; i++) {
                        string fileName = Path.GetFileName(files[i]);
                        string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                        string icon = GetFileIcon(ext);
                        Console.WriteLine($"    {icon} {fileName}");
                    }
                }
                else {
                    Console.WriteLine($"\n  📁 Verzeichnis-Übersicht: {folderPath} (Insgesamt {files.Length} Datei(en), {subDirs.Length} Ordner)");

                    int maxDirsToShow = Math.Min(subDirs.Length, 8);
                    for (int i = 0; i < maxDirsToShow; i++) {
                        string dirName = Path.GetFileName(subDirs[i]);
                        try {
                            int innerFiles = Directory.GetFiles(subDirs[i]).Length;
                            int innerDirs = Directory.GetDirectories(subDirs[i]).Length;
                            Console.WriteLine($"    📁 {dirName}/ ({innerFiles} Datei(en), {innerDirs} Ordner)");
                        }
                        catch {
                            Console.WriteLine($"    📁 {dirName}/");
                        }
                    }
                    if (subDirs.Length > maxDirsToShow) {
                        Console.WriteLine($"    ... und {subDirs.Length - maxDirsToShow} weitere Unterordner");
                    }

                    int maxFilesToShow = Math.Min(files.Length, 10);
                    for (int i = 0; i < maxFilesToShow; i++) {
                        string fileName = Path.GetFileName(files[i]);
                        string ext = Path.GetExtension(files[i]).ToLowerInvariant();
                        string icon = GetFileIcon(ext);
                        Console.WriteLine($"    {icon} {fileName}");
                    }
                    if (files.Length > maxFilesToShow) {
                        Console.WriteLine($"    ... und {files.Length - maxFilesToShow} weitere Datei(en)");
                    }
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            }
        }

        /// <summary>
        /// [AI Context] Interactive prompt verifying or updating the configured source directory.
        /// Displays a visual preview with icons and allows persisting the new path to configuration JSON.
        /// [Human] Fragt den Benutzer interaktiv (mit j/n), ob der voreingestellte Quellordner genutzt werden soll, zeigt Vorschau-Icons und speichert Änderungen auf Wunsch in der JSON-Konfiguration.
        /// </summary>
        public static string ConfirmOrChangeSourceFolder(string currentFolder, Action<string>? onFolderChanged = null) {
            Console.WriteLine($"\n==================================================");
            Console.WriteLine($" 📁 Voreingestellter Quellordner: {currentFolder}");
            Console.WriteLine($"==================================================");

            DisplayDirectoryPreview(currentFolder);

            Console.Write("\nMöchten Sie diesen voreingestellten Quellordner verwenden? (j/n, Standard: j): ");
            string? choice = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (choice == "n" || choice == "nein" || choice == "no") {
                Console.Write("\nBitte neuen Pfad für den Quellordner eingeben: ");
                string? newPath = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(newPath)) {
                    newPath = newPath.Trim('\"', '\'');

                    if (!Directory.Exists(newPath)) {
                        Console.Write($"Der Ordner '{newPath}' existiert nicht. Möchten Sie ihn erstellen? (j/n, Standard: j): ");
                        string? createChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                        if (createChoice != "n" && createChoice != "nein") {
                            try {
                                Directory.CreateDirectory(newPath);
                                Console.WriteLine($"  [OK] Ordner erstellt: {newPath}");
                            }
                            catch (Exception ex) {
                                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                                return currentFolder;
                            }
                        }
                        else {
                            Console.WriteLine("  [INFO] Behalte bisherigen Ordner bei.");
                            return currentFolder;
                        }
                    }

                    currentFolder = newPath;
                    Console.WriteLine($"\n  🎯 Neuer Quellordner ausgewählt: {currentFolder}");
                    DisplayDirectoryPreview(currentFolder);

                    if (onFolderChanged != null) {
                        onFolderChanged.Invoke(currentFolder);
                        Console.WriteLine("  💾 [INFO] Der neue Pfad wurde in der Konfiguration (JSON) gespeichert.");
                    }
                }
            }

            return currentFolder;
        }
    }
}