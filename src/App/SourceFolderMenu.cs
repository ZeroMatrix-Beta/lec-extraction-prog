using System;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Interactive menu enabling users to inspect and update source folders across all session
/// profiles. Extracted from Program.cs (Phase 6), was ConfigureSourceFoldersMenu.
/// [Human] Menü zum Anzeigen und Ändern der Quellordner für alle Sitzungsprofile.
/// </summary>
public static class SourceFolderMenu {
    public static void Show() {
        while (true) {
            var aiStudioConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
            var vertexConfig = ConfigLoader<VertexAutoExtractionConfig>.Load();
            var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
            var latexConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();

            Console.WriteLine("\n==================================================");
            Console.WriteLine("      ⚙️ Quellordner-Konfiguration (JSON)         ");
            Console.WriteLine("==================================================");
            Console.WriteLine($" 1) Google AI Studio Auto-Extraktion: {aiStudioConfig.SourceFolder}");
            Console.WriteLine($" 2) Google Cloud Vertex AI Auto-Extraktion: {vertexConfig.SourceFolder}");
            Console.WriteLine($" 3) FFmpeg Converter: {ffmpegConfig.SourceFolder}");
            Console.WriteLine($" 4) LaTeX Refinement Session Source: {latexConfig.SourceFolder}");
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(" 5) Zurück zum Hauptmenü");
            Console.Write("\nWelchen Quellordner möchten Sie ansehen / ändern? (1-5): ");

            string? choice = Console.ReadLine()?.Trim();
            if (choice == "5" || choice == "exit" || choice == "quit") break;

            switch (choice) {
                case "1":
                    aiStudioConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(aiStudioConfig.SourceFolder, newFolder => {
                        aiStudioConfig.SourceFolder = newFolder;
                        ConfigLoader<AiStudioAutoExtractionConfig>.Save(aiStudioConfig);
                    }, aiStudioConfig.PredefinedSourceFolders);
                    break;
                case "2":
                    vertexConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(vertexConfig.SourceFolder, newFolder => {
                        vertexConfig.SourceFolder = newFolder;
                        ConfigLoader<VertexAutoExtractionConfig>.Save(vertexConfig);
                    }, vertexConfig.PredefinedSourceFolders);
                    break;
                case "3":
                    ffmpegConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(ffmpegConfig.SourceFolder, newFolder => {
                        ffmpegConfig.SourceFolder = newFolder;
                        ConfigLoader<FfmpegSessionConfig>.Save(ffmpegConfig);
                    });
                    break;
                case "4":
                    string currentLatexSource = string.IsNullOrEmpty(latexConfig.SourceFolder) ? AppConfig.LatexRefinementSourceFolder : latexConfig.SourceFolder;
                    latexConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(currentLatexSource, newFolder => {
                        latexConfig.SourceFolder = newFolder;
                        ConfigLoader<LatexRefinementSessionConfig>.Save(latexConfig);
                    });
                    break;
                default:
                    Console.WriteLine("Ungültige Auswahl.");
                    break;
            }
        }
    }
}
