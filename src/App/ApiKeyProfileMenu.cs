using System;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using Spectre.Console;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Interactive menu enabling users to inspect and update API key profiles across all session
/// configurations. Extracted from Program.cs (Phase 6), was ConfigureApiKeysMenu.
/// [Human] Menü zum Anzeigen und Ändern des aktiven API-Key Profils für alle KI-Sitzungen (Auto-Extraktion,
/// Chat, LaTeX Refinement).
/// </summary>
public static class ApiKeyProfileMenu {
    public static void Show() {
        while (true) {
            var aiStudioConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
            var chatConfig = ConfigLoader<DirectAiChatSessionAiStudioConfig>.Load();
            var latexConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();

            string autoExtProfileLabel = aiStudioConfig.ActiveApiProfile == 0 ? "Dedizierter Key (0)" : $"Profil {aiStudioConfig.ActiveApiProfile}";
            string chatProfileLabel = chatConfig.ActiveApiProfile == 0 ? "Dedizierter Key (0)" : $"Profil {chatConfig.ActiveApiProfile}";
            string latexProfileLabel = latexConfig.AiStudioActiveApiProfile == 0 ? "Dedizierter Key (0)" : $"Profil {latexConfig.AiStudioActiveApiProfile}";

            Ui.Step("API-Key Profil Konfiguration (JSON)");

            string choice1 = $"1) Google AI Studio Auto-Extraktion: {autoExtProfileLabel}";
            string choice2 = $"2) Direct AI Studio Chat:          {chatProfileLabel}";
            string choice3 = $"3) LaTeX Refinement Session:       {latexProfileLabel}";
            string choiceExit = "4) 🚪 Zurück zum Hauptmenü";

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Welches API-Key Profil möchten Sie ansehen / ändern?[/]")
                    .AddChoices(choice1, choice2, choice3, choiceExit));

            if (selection == choiceExit) break;

            if (selection == choice1) {
                aiStudioConfig.ActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                    aiStudioConfig.ActiveApiProfile,
                    "AI Studio Auto-Extraktion",
                    newProfile => {
                        aiStudioConfig.ActiveApiProfile = newProfile;
                        ConfigLoader<AiStudioAutoExtractionConfig>.Save(aiStudioConfig);
                    },
                    aiStudioConfig.AiStudioApiKeyEnvNames
                );
            }
            else if (selection == choice2) {
                chatConfig.ActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                    chatConfig.ActiveApiProfile,
                    "Direct AI Studio Chat",
                    newProfile => {
                        chatConfig.ActiveApiProfile = newProfile;
                        ConfigLoader<DirectAiChatSessionAiStudioConfig>.Save(chatConfig);
                    },
                    chatConfig.AiStudioApiKeyEnvNames
                );
            }
            else if (selection == choice3) {
                latexConfig.AiStudioActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                    latexConfig.AiStudioActiveApiProfile,
                    "LaTeX Refinement Session",
                    newProfile => {
                        latexConfig.AiStudioActiveApiProfile = newProfile;
                        ConfigLoader<LatexRefinementSessionConfig>.Save(latexConfig);
                    },
                    latexConfig.AiStudioApiKeyEnvNames
                );
            }
        }
    }
}
