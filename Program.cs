using System;
using System.Threading.Tasks;
using DirectChatAiInteraction;
using DirectChatAiInteraction.AiStudio;
using DirectChatAiInteraction.Vertex;
using FfmpegUtilities;
using GoogleGenAi;
using Google.GenAI;
using AutoExtraction;
using Config;
using Infrastructure;

/// <summary>
/// [AI Context] Main application entry point. Orchestrates the execution flow by delegating
/// either to the FFmpeg processing toolkit or the Gemini chat session based on user input.
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird.
/// </summary>
public partial class Program {
    // [AI Context] Master kill switch for Google Cloud Vertex AI to prevent accidental billing/costs.
    // [Human] Wenn auf false gesetzt, sind sämtliche Vertex AI Funktionen in der App (Chat, Extraktion, Refinement) strikt deaktiviert.
    public static bool Activate_Vertex { get; set; } = false;

    static async Task Main() {
        try {
            // [AI Context] Bootstrapper. Demonstrates manual Dependency Injection (DI) pattern.
            // Determines the runtime environment (Vertex vs AI Studio) and wires up the respective isolated dependencies.
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
                string vertexDisplay = Activate_Vertex ? "2) ☁️ Google Cloud Vertex AI (Enterprise)" : "2) ☁️ Google Cloud Vertex AI [DEAKTIVIERT - Kostenschutz]";
                Console.WriteLine($"  {vertexDisplay}");
                Console.WriteLine("  3) 🎬 FFmpeg Interactive Manager (Lokale Audio/Video-Verarbeitung)");
                Console.WriteLine("--------------------------------------------------");
                Console.WriteLine("  4) 🚀 Automatisierte Content-Extraktion & Verarbeitung");
                Console.WriteLine("  5) ✍️ LaTeX Refinement & Nachbearbeitung (Dedizierter Key)");
                Console.WriteLine("  6) ⚙️ Quellordner (Source Folders) verwalten & ändern");
                Console.Write("\nChoice (1-6) or 'exit': ");

                string? mainChoice = Console.ReadLine()?.Trim().ToLower();

                // [AI Context] Handle null (EOF) as an exit signal to prevent infinite loops in non-interactive terminals.
                if (mainChoice == null || mainChoice == "exit" || mainChoice == "quit") {
                    break;
                }

                switch (mainChoice) {
                    case "1":
                        await RunDirectAiStudioChatAsync();
                        break;
                    case "2":
                        if (!Activate_Vertex) {
                            Console.WriteLine("\n[Kostenschutz] Google Cloud Vertex AI ist deaktiviert (Program.Activate_Vertex = false). Bitte nutze Google AI Studio (Option 1).");
                            break;
                        }
                        await RunDirectVertexChatAsync();
                        break;
                    case "3":
                        await RunFfmpegSessionAsync();
                        break;
                    case "4":
                        await RunAutoExtractionAsync();
                        break;
                    case "5":
                        await RunLatexRefinementAsync();
                        break;
                    case "6":
                        ConfigureSourceFoldersMenu();
                        break;
                    default:
                        Console.WriteLine("Invalid choice.");
                        break;
                }
            }
        }
        catch (OperationCanceledException) {
            Console.WriteLine("\n[System] Execution cancelled by user. Exiting cleanly.");
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[FATAL ERROR] The application encountered an unhandled exception and must close.");
            Console.WriteLine($"Type: {ex.GetType().Name}");
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");

            // Keep the console open so the user can actually read the fatal error
            Console.WriteLine("\nPress any key to exit...");
            if (!Console.IsInputRedirected) Console.ReadKey(true);
        }
        finally {
            Console.WriteLine("\n[System] Session ended.");
        }
    }

    private static async Task RunDirectAiStudioChatAsync() {
        var config = ConfigLoader<DirectAiChatSessionAiStudioConfig>.Load();
        string envName = (config.AiStudioApiKeyEnvNames != null && config.AiStudioApiKeyEnvNames.Length > config.ActiveApiProfile)
            ? config.AiStudioApiKeyEnvNames[config.ActiveApiProfile]
            : (config.ActiveApiProfile == 0 ? "API_KEY-automated-content-extraction" : $"API_KEY-ai-studio-test-project-{config.ActiveApiProfile}");
        string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
        Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
        var attachmentHandler = new AttachmentHandler(client, config.UploadFolder, config.IncludePaths, true, config.GcsBucketName);
        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var chatSession = new DirectAiChatSessionAiStudio(client, config, sessionLogger, attachmentHandler, isAiStudio: true);
        await chatSession.StartAsync();
    }

    private static async Task RunDirectVertexChatAsync() {
        var config = ConfigLoader<DirectAiChatSessionVertexConfig>.Load();
        Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
        var attachmentHandler = new AttachmentHandler(client, config.UploadFolder, config.IncludePaths, false, config.GcsBucketName);
        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var chatSession = new DirectAiChatSessionVertex(client, config, sessionLogger, attachmentHandler);
        await chatSession.StartAsync();
    }

    private static async Task RunFfmpegSessionAsync() {
        var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
        var ffmpegMenu = new FfmpegInteractiveSession(ffmpegConfig);
        await ffmpegMenu.StartAsync();
    }

    private static async Task RunAutoExtractionAsync() {
        string? extChoice = "1";
        if (Activate_Vertex) {
            Console.WriteLine("\nWelche API soll für die automatisierte Extraktion genutzt werden?");
            Console.WriteLine(" 1) Google AI Studio");
            Console.WriteLine(" 2) Google Cloud Vertex AI");
            Console.Write("Wahl (1-2) oder 'exit': ");
            extChoice = Console.ReadLine()?.Trim().ToLower();
            if (extChoice == "exit" || extChoice == "quit") return;
        }
        else {
            Console.WriteLine("\n[Kostenschutz] Vertex AI ist deaktiviert (Activate_Vertex = false). Starte automatisch mit Google AI Studio...");
        }

        if (extChoice == "2" && Activate_Vertex) {
            var config = ConfigLoader<VertexAutoExtractionConfig>.Load();
            config.SourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(config.SourceFolder, newFolder => {
                config.SourceFolder = newFolder;
                ConfigLoader<VertexAutoExtractionConfig>.Save(config);
            }, config.PredefinedSourceFolders);
            string selectedModel = ConsoleUiHelper.ConfirmOrChangeModel(config.Model, "Vertex AI Auto-Extraktion", VertexAutoExtractionSession.AvailableModels, newModel => {
                config.Model = newModel;
                ConfigLoader<VertexAutoExtractionConfig>.Save(config);
            });
            if (selectedModel == "__EXIT__") return;
            config.Model = selectedModel;

            Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
            var attachmentHandler = new AttachmentHandler(client, config.SourceFolder, [config.SourceFolder], false, config.GcsBucketName, config.GoogleVideoFps);
            var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
            var latexRefinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();
            var session = new VertexAutoExtractionSession(client, config, attachmentHandler, sessionLogger, latexRefinementConfig);
            await session.StartAsync();
        }
        else {
            var config = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
            config.SourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(config.SourceFolder, newFolder => {
                config.SourceFolder = newFolder;
                ConfigLoader<AiStudioAutoExtractionConfig>.Save(config);
            }, config.PredefinedSourceFolders);
            string selectedModel = ConsoleUiHelper.ConfirmOrChangeModel(config.Model, "AI Studio Auto-Extraktion", AiStudioAutoExtractionSession.AvailableModels, newModel => {
                config.Model = newModel;
                ConfigLoader<AiStudioAutoExtractionConfig>.Save(config);
            });
            if (selectedModel == "__EXIT__") return;
            config.Model = selectedModel;

            string envName = (config.AiStudioApiKeyEnvNames != null && config.AiStudioApiKeyEnvNames.Length > config.ActiveApiProfile)
                ? config.AiStudioApiKeyEnvNames[config.ActiveApiProfile]
                : (config.ActiveApiProfile == 0 ? "API_KEY-automated-content-extraction" : $"API_KEY-ai-studio-test-project-{config.ActiveApiProfile}");
            string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
            Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
            var attachmentHandler = new AttachmentHandler(client, config.SourceFolder, [config.SourceFolder], true, "", config.GoogleVideoFps);
            var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
            var latexRefinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load(); // Load config for refinement
            var session = new AiStudioAutoExtractionSession(client, config, attachmentHandler, sessionLogger, latexRefinementConfig);
            await session.StartAsync();
        }
    }

    private static async Task RunLatexRefinementAsync() {
        var config = ConfigLoader<LatexRefinementSessionConfig>.Load();
        var extractionConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
        await AutoExtraction.RefinementUiHelper.StartInteractiveRefinementAsync(config, extractionConfig);
    }

    // [AI Context] Interactive menu enabling users to inspect and update source folders across all session profiles.
    private static void ConfigureSourceFoldersMenu() {
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
                    aiStudioConfig.SourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(aiStudioConfig.SourceFolder, newFolder => {
                        aiStudioConfig.SourceFolder = newFolder;
                        ConfigLoader<AiStudioAutoExtractionConfig>.Save(aiStudioConfig);
                    }, aiStudioConfig.PredefinedSourceFolders);
                    break;
                case "2":
                    vertexConfig.SourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(vertexConfig.SourceFolder, newFolder => {
                        vertexConfig.SourceFolder = newFolder;
                        ConfigLoader<VertexAutoExtractionConfig>.Save(vertexConfig);
                    }, vertexConfig.PredefinedSourceFolders);
                    break;
                case "3":
                    ffmpegConfig.SourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(ffmpegConfig.SourceFolder, newFolder => {
                        ffmpegConfig.SourceFolder = newFolder;
                        ConfigLoader<FfmpegSessionConfig>.Save(ffmpegConfig);
                    });
                    break;
                case "4":
                    string currentLatexSource = string.IsNullOrEmpty(latexConfig.SourceFolder) ? AppConfig.LatexRefinementSourceFolder : latexConfig.SourceFolder;
                    latexConfig.SourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(currentLatexSource, newFolder => {
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
