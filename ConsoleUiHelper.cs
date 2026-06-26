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
                string icon = ext switch {
                    ".mp4" or ".mkv" or ".avi" or ".mov" => "🎬",
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "🎵",
                    ".pdf" => "📕",
                    ".tex" or ".md" or ".txt" => "📄",
                    _ => "📎"
                };
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
    }
}