using System;
using System.Threading.Tasks;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using Spectre.Console;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] The top-level interactive loop: prints the main menu, dispatches to SessionFactory for
/// the session types, or to SourceFolderMenu/ApiKeyProfileMenu for configuration. Extracted from
/// Program.cs (Phase 6), which now only holds Main() and top-level exception handling.
/// [Human] Die Hauptschleife der Konsole: zeigt das Hauptmenü und delegiert an die jeweilige Session bzw.
/// an die Konfigurationsmenüs.
/// </summary>
public static class MainMenu {
    public static async Task RunAsync() {
        while (true) {
            // Lade die Konfiguration für die Auto-Extraktion, um den aktuellen Status anzuzeigen
            var autoExtConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
            string autoExtProfileDisplay = autoExtConfig.ActiveApiProfile == 0
                ? "Dedizierter Key (automated-content-extraction)"
                : $"Profil {autoExtConfig.ActiveApiProfile}";

            Ui.Step("Welcome to AI Extraction & Processing");
            Ui.Detail($"Aktives AI Studio Profil für Auto-Extraktion: {autoExtProfileDisplay}");
            Ui.Blank();

            string choice1 = "1) 🌐 Google AI Studio (API Key / Developer Endpoints)";
            string choice2 = AppConfig.IsVertexAiEnabled
                ? "2) ☁️ Google Cloud Vertex AI (Enterprise)"
                : "2) ☁️ Google Cloud Vertex AI [DEAKTIVIERT - Kostenschutz]";
            string choice3 = "3) 🎬 FFmpeg Interactive Manager (Lokale Audio/Video-Verarbeitung)";
            string choice4 = "4) 🚀 Automatisierte Content-Extraktion & Verarbeitung";
            string choice5 = "5) ✍️ LaTeX Refinement & Nachbearbeitung (Dedizierter Key)";
            string choice6 = "6) ⚙️ Quellordner (Source Folders) verwalten & ändern";
            string choice7 = "7) 🔑 API-Key Profile (AI Studio & Refinement) verwalten & ändern";
            string choiceExit = "8) 🚪 Beenden";

            var prompt = new SelectionPrompt<string>()
                .Title("[bold]Bitte gewünschten Modus auswählen:[/]")
                .PageSize(10);

            prompt.AddChoice(choice1);
            if (AppConfig.IsVertexAiEnabled) {
                prompt.AddChoice(choice2);
            }
            prompt.AddChoice(choice3);
            prompt.AddChoice(choice4);
            prompt.AddChoice(choice5);
            prompt.AddChoice(choice6);
            prompt.AddChoice(choice7);
            prompt.AddChoice(choiceExit);

            if (!AppConfig.IsVertexAiEnabled) {
                Ui.Detail("  " + choice2);
            }

            var selection = AnsiConsole.Prompt(prompt);

            if (selection == choiceExit) {
                break;
            }

            if (selection == choice1) {
                await SessionFactory.RunDirectAiStudioChatAsync();
            }
            else if (selection == choice2 && AppConfig.IsVertexAiEnabled) {
                await SessionFactory.RunDirectVertexChatAsync();
            }
            else if (selection == choice3) {
                await SessionFactory.RunFfmpegSessionAsync();
            }
            else if (selection == choice4) {
                await SessionFactory.RunAutoExtractionAsync();
            }
            else if (selection == choice5) {
                await SessionFactory.RunLatexRefinementAsync();
            }
            else if (selection == choice6) {
                SourceFolderMenu.Show();
            }
            else if (selection == choice7) {
                ApiKeyProfileMenu.Show();
            }
        }
    }
}
