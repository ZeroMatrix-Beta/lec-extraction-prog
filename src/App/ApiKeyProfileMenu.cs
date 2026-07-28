using System;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;

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

            Console.WriteLine("\n==================================================");
            Console.WriteLine("    🔑 API-Key Profil Konfiguration (JSON)        ");
            Console.WriteLine("==================================================");
            Console.WriteLine($" 1) Google AI Studio Auto-Extraktion: {autoExtProfileLabel}");
            Console.WriteLine($" 2) Direct AI Studio Chat:          {chatProfileLabel}");
            Console.WriteLine($" 3) LaTeX Refinement Session:       {latexProfileLabel}");
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(" 4) Zurück zum Hauptmenü");
            Console.Write("\nWelches API-Key Profil möchten Sie ansehen / ändern? (1-4): ");

            string? choice = Console.ReadLine()?.Trim();
            if (choice == "4" || choice == "exit" || choice == "quit") break;

            switch (choice) {
                case "1":
                    aiStudioConfig.ActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                        aiStudioConfig.ActiveApiProfile,
                        "AI Studio Auto-Extraktion",
                        newProfile => {
                            aiStudioConfig.ActiveApiProfile = newProfile;
                            ConfigLoader<AiStudioAutoExtractionConfig>.Save(aiStudioConfig);
                        },
                        aiStudioConfig.AiStudioApiKeyEnvNames
                    );
                    break;
                case "2":
                    chatConfig.ActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                        chatConfig.ActiveApiProfile,
                        "Direct AI Studio Chat",
                        newProfile => {
                            chatConfig.ActiveApiProfile = newProfile;
                            ConfigLoader<DirectAiChatSessionAiStudioConfig>.Save(chatConfig);
                        },
                        chatConfig.AiStudioApiKeyEnvNames
                    );
                    break;
                case "3":
                    latexConfig.AiStudioActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                        latexConfig.AiStudioActiveApiProfile,
                        "LaTeX Refinement Session",
                        newProfile => {
                            latexConfig.AiStudioActiveApiProfile = newProfile;
                            ConfigLoader<LatexRefinementSessionConfig>.Save(latexConfig);
                        },
                        latexConfig.AiStudioApiKeyEnvNames
                    );
                    break;
                default:
                    Console.WriteLine("Ungültige Auswahl.");
                    break;
            }
        }
    }
}
