using System;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using Spectre.Console;

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

            Ui.Step("Quellordner-Konfiguration (JSON)");

            string choice1 = $"1) Google AI Studio Auto-Extraktion: {aiStudioConfig.SourceFolder}";
            string choice2 = $"2) Google Cloud Vertex AI Auto-Extraktion: {vertexConfig.SourceFolder}";
            string choice3 = $"3) FFmpeg Converter: {ffmpegConfig.SourceFolder}";
            string choice4 = $"4) LaTeX Refinement Session Source: {(string.IsNullOrEmpty(latexConfig.SourceFolder) ? AppConfig.LatexRefinementSourceFolder : latexConfig.SourceFolder)}";
            string choiceExit = "5) 🚪 Zurück zum Hauptmenü";

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Welchen Quellordner möchten Sie ansehen / ändern?[/]")
                    .AddChoices(choice1, choice2, choice3, choice4, choiceExit));

            if (selection == choiceExit) break;

            if (selection == choice1) {
                aiStudioConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(aiStudioConfig.SourceFolder, newFolder => {
                    aiStudioConfig.SourceFolder = newFolder;
                    ConfigLoader<AiStudioAutoExtractionConfig>.Save(aiStudioConfig);
                }, aiStudioConfig.PredefinedSourceFolders);
            }
            else if (selection == choice2) {
                vertexConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(vertexConfig.SourceFolder, newFolder => {
                    vertexConfig.SourceFolder = newFolder;
                    ConfigLoader<VertexAutoExtractionConfig>.Save(vertexConfig);
                }, vertexConfig.PredefinedSourceFolders);
            }
            else if (selection == choice3) {
                ffmpegConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(ffmpegConfig.SourceFolder, newFolder => {
                    ffmpegConfig.SourceFolder = newFolder;
                    ConfigLoader<FfmpegSessionConfig>.Save(ffmpegConfig);
                });
            }
            else if (selection == choice4) {
                string currentLatexSource = string.IsNullOrEmpty(latexConfig.SourceFolder) ? AppConfig.LatexRefinementSourceFolder : latexConfig.SourceFolder;
                latexConfig.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(currentLatexSource, newFolder => {
                    latexConfig.SourceFolder = newFolder;
                    ConfigLoader<LatexRefinementSessionConfig>.Save(latexConfig);
                });
            }
        }
    }
}
