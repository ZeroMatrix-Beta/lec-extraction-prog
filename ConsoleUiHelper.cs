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

            System.Collections.Generic.List<TreePreviewItem> items = [];

            // 1. Files first
            for (int i = 0; i < shownFiles; i++) {
                string fileName = Path.GetFileName(files[i]);
                string cleanedFileName = AutoExtraction.ExtractionHelpers.CleanCopySuffix(fileName);
                items.Add(new() { IsDir = false, IsMsg = false, Name = cleanedFileName, Path = files[i] });
            }
            if (hasMoreFiles) {
                items.Add(new() { IsDir = false, IsMsg = true, Name = $"... und {files.Length - shownFiles} weitere Datei(en)", Path = "" });
            }

            // 2. Folders next
            if (currentDepth < maxDepth) {
                for (int i = 0; i < shownDirs; i++) {
                    string dirName = Path.GetFileName(subDirs[i]);
                    string cleanedDirName = AutoExtraction.ExtractionHelpers.CleanCopySuffix(dirName);
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
                    string relPath = AutoExtraction.ExtractionHelpers.NormalizeRelativePath(rawRelPath);
                    string label = (!string.IsNullOrEmpty(relPath) && !string.Equals(relPath, item.Name, StringComparison.OrdinalIgnoreCase))
                        ? $"{item.Name} ({relPath})"
                        : item.Name;
                    Console.WriteLine($"{indent}{branch}{icon} {label}");
                }
            }
        }

        /// <summary>
        /// <summary>
        /// [AI Context] Interactive prompt verifying or updating the configured source directory.
        /// Displays predefined folders if configured, launches the folder explorer, or allows direct path input.
        /// </summary>
        public static string ConfirmOrChangeSourceFolder(string currentFolder, Action<string>? onFolderChanged = null, string[]? predefinedFolders = null) {
            Console.WriteLine($"\n==================================================");
            Console.WriteLine($" 📁 Quellordner-Auswahl");
            Console.WriteLine($"==================================================");
            Console.WriteLine($" Aktueller Quellordner: {currentFolder}");

            if (predefinedFolders != null && predefinedFolders.Length > 0) {
                Console.WriteLine("\nVordefinierte Quellordner:");
                for (int i = 0; i < predefinedFolders.Length; i++) {
                    Console.WriteLine($"  {i + 1}) {predefinedFolders[i]}");
                }
                Console.WriteLine($"  {predefinedFolders.Length + 1}) 🔍 Interaktiver Ordner-Explorer");
                Console.WriteLine($"  {predefinedFolders.Length + 2}) ✍️ Pfad manuell eingeben");
                Console.WriteLine($"  [ENTER]          => Bisherigen Ordner '{currentFolder}' verwenden");

                Console.Write($"\nAuswahl (1-{predefinedFolders.Length + 2}) [Standard: ENTER]: ");
                string choice = Console.ReadLine()?.Trim() ?? "";

                if (string.IsNullOrEmpty(choice)) {
                    DisplayDirectoryPreview(currentFolder);
                    return currentFolder;
                }

                if (int.TryParse(choice, out int choiceNum)) {
                    if (choiceNum >= 1 && choiceNum <= predefinedFolders.Length) {
                        currentFolder = predefinedFolders[choiceNum - 1];
                        Console.WriteLine($"\n  🎯 Quellordner ausgewählt: {currentFolder}");
                        DisplayDirectoryPreview(currentFolder);
                        onFolderChanged?.Invoke(currentFolder);
                        return currentFolder;
                    }
                    else if (choiceNum == predefinedFolders.Length + 1) {
                        currentFolder = NavigateDirectory(currentFolder);
                        Console.WriteLine($"\n  🎯 Quellordner ausgewählt: {currentFolder}");
                        DisplayDirectoryPreview(currentFolder);
                        onFolderChanged?.Invoke(currentFolder);
                        return currentFolder;
                    }
                    else if (choiceNum == predefinedFolders.Length + 2) {
                        // Proceed to manual input
                    }
                    else {
                        Console.WriteLine("  [INFO] Ungültige Auswahl. Behalte bisherigen Ordner bei.");
                        DisplayDirectoryPreview(currentFolder);
                        return currentFolder;
                    }
                }
                else {
                    Console.WriteLine("  [INFO] Ungültige Auswahl. Behalte bisherigen Ordner bei.");
                    DisplayDirectoryPreview(currentFolder);
                    return currentFolder;
                }
            }
            else {
                Console.WriteLine($"  1) Bisherigen Ordner '{currentFolder}' verwenden");
                Console.WriteLine("  2) 🔍 Interaktiver Ordner-Explorer");
                Console.WriteLine("  3) ✍️ Pfad manuell eingeben");
                Console.Write("\nAuswahl (1-3) [Standard: 1]: ");
                string choice = Console.ReadLine()?.Trim() ?? "";

                if (choice == "2") {
                    currentFolder = NavigateDirectory(currentFolder);
                    Console.WriteLine($"\n  🎯 Quellordner ausgewählt: {currentFolder}");
                    DisplayDirectoryPreview(currentFolder);
                    onFolderChanged?.Invoke(currentFolder);
                    return currentFolder;
                }
                else if (choice == "3") {
                    // Proceed to manual input
                }
                else {
                    DisplayDirectoryPreview(currentFolder);
                    return currentFolder;
                }
            }

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
                    Console.Write("Möchten Sie diese Änderung permanent in der Konfiguration speichern? (j/n, Standard: j): ");
                    string? saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (saveChoice != "n" && saveChoice != "nein" && saveChoice != "no") {
                        onFolderChanged.Invoke(currentFolder);
                        Console.WriteLine("  💾 [INFO] Der neue Pfad wurde in der Konfiguration (JSON) gespeichert.");
                    } else {
                        Console.WriteLine("  [INFO] Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                    }
                }
            }

            return currentFolder;
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
                        string cleaned = AutoExtraction.ExtractionHelpers.CleanCopySuffix(name);
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

        /// <summary>
        /// [AI Context] Interactive prompt verifying or updating the configured model.
        /// Allows persisting the new model to configuration JSON.
        /// </summary>
        public static string ConfirmOrChangeModel(string currentModel, string apiType, string[] availableModels, Action<string>? onModelChanged = null) {
            Console.WriteLine($"\n==================================================");
            Console.WriteLine($" 🤖 Voreingestelltes Modell ({apiType}): {currentModel}");
            Console.WriteLine($"==================================================");
            
            Console.Write("\nMöchten Sie dieses voreingestellte Modell verwenden? (j/n, Standard: j): ");
            string? choice = Console.ReadLine()?.Trim().ToLowerInvariant();
            
            if (choice == "n" || choice == "nein" || choice == "no") {
                Console.WriteLine("\nVerfügbare Modelle:");
                for (int i = 0; i < availableModels.Length; i++) {
                    Console.WriteLine($" {i + 1}) {availableModels[i]}");
                }
                
                Console.Write($"\nBitte Modell auswählen (1-{availableModels.Length}) [Standard: {currentModel}]: ");
                string? modelChoice = Console.ReadLine()?.Trim();
                
                if (modelChoice == "exit" || modelChoice == "quit") return "__EXIT__";
                
                string newModel;
                if (int.TryParse(modelChoice, out int index) && index >= 1 && index <= availableModels.Length) {
                    newModel = availableModels[index - 1];
                }
                else {
                    newModel = currentModel;
                }
                
                Console.WriteLine($"\n  🎯 Neues Modell ausgewählt: {newModel}");
                
                if (onModelChanged != null) {
                    Console.Write("Möchten Sie diese Änderung permanent in der Konfiguration speichern? (j/n, Standard: j): ");
                    string? saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                    if (saveChoice != "n" && saveChoice != "nein" && saveChoice != "no") {
                        onModelChanged.Invoke(newModel);
                        Console.WriteLine("  💾 [INFO] Das neue Modell wurde in der Konfiguration (JSON) gespeichert.");
                    } else {
                        Console.WriteLine("  [INFO] Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                    }
                }
                
                return newModel;
            }
            
            return currentModel;
        }
    }
}