using System;
using System.Threading.Tasks;
using LectureExtraction.Configuration;

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

            Console.WriteLine("\n==================================================");
            Console.WriteLine("     Welcome to AI Extraction & Processing        ");
            Console.WriteLine($" (Aktives AI Studio Profil für Auto-Extraktion: {autoExtProfileDisplay})");
            Console.WriteLine("==================================================");
            Console.WriteLine("Bitte gewünschten Modus auswählen:");
            Console.WriteLine("  1) 🌐 Google AI Studio (API Key / Developer Endpoints)");
            string vertexDisplay = AppConfig.IsVertexAiEnabled ? "2) ☁️ Google Cloud Vertex AI (Enterprise)" : "2) ☁️ Google Cloud Vertex AI [DEAKTIVIERT - Kostenschutz]";
            Console.WriteLine($"  {vertexDisplay}");
            Console.WriteLine("  3) 🎬 FFmpeg Interactive Manager (Lokale Audio/Video-Verarbeitung)");
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine("  4) 🚀 Automatisierte Content-Extraktion & Verarbeitung");
            Console.WriteLine("  5) ✍️ LaTeX Refinement & Nachbearbeitung (Dedizierter Key)");
            Console.WriteLine("  6) ⚙️ Quellordner (Source Folders) verwalten & ändern");
            Console.WriteLine("  7) 🔑 API-Key Profile (AI Studio & Refinement) verwalten & ändern");
            Console.Write("\nChoice (1-7) or 'exit': ");

            string? mainChoice = Console.ReadLine()?.Trim().ToLower();

            // [AI Context] Handle null (EOF) as an exit signal to prevent infinite loops in non-interactive terminals.
            if (mainChoice == null || mainChoice == "exit" || mainChoice == "quit") {
                break;
            }

            switch (mainChoice) {
                case "1":
                    await SessionFactory.RunDirectAiStudioChatAsync();
                    break;
                case "2":
                    if (!AppConfig.IsVertexAiEnabled) {
                        Console.WriteLine("\n[Kostenschutz] Google Cloud Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false in appsettings.json). Bitte nutze Google AI Studio (Option 1).");
                        break;
                    }
                    await SessionFactory.RunDirectVertexChatAsync();
                    break;
                case "3":
                    await SessionFactory.RunFfmpegSessionAsync();
                    break;
                case "4":
                    await SessionFactory.RunAutoExtractionAsync();
                    break;
                case "5":
                    await SessionFactory.RunLatexRefinementAsync();
                    break;
                case "6":
                    SourceFolderMenu.Show();
                    break;
                case "7":
                    ApiKeyProfileMenu.Show();
                    break;
                default:
                    Console.WriteLine("  [FEHLER] Ungültige Auswahl.");
                    break;
            }
        }
    }
}
