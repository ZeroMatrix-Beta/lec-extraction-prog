using System;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Chat;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Media;
using LectureExtraction.Refinement;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Wires up each of MainMenu's session types: loads the relevant config(s), resolves
/// credentials, builds the Google GenAI client, and constructs + starts the session. Extracted from
/// Program.cs (Phase 6) — this is the "manual Dependency Injection" bootstrapping the file's original
/// doc comment described, now named and separated from the menu loop that triggers it.
/// [Human] Baut jede der Sessions aus dem Hauptmenü zusammen: lädt Konfiguration, löst Credentials auf,
/// erstellt den Google-Client und startet die Session.
/// </summary>
public static class SessionFactory {
    public static async Task RunDirectAiStudioChatAsync() {
        var config = ConfigLoader<DirectAiChatSessionAiStudioConfig>.Load();
        int profile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
            config.ActiveApiProfile,
            "Direct AI Studio Chat",
            newProfile => {
                config.ActiveApiProfile = newProfile;
                ConfigLoader<DirectAiChatSessionAiStudioConfig>.Save(config);
            },
            config.AiStudioApiKeyEnvNames
        );
        config.ActiveApiProfile = profile;

        string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);

        string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
        Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
        var attachmentHandler = new AttachmentUploader(client, config.UploadFolder, config.IncludePaths, true, config.GcsBucketName);
        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var chatSession = new DirectAiChatSessionAiStudio(client, config, sessionLogger, attachmentHandler, isAiStudio: true);
        await chatSession.StartAsync();
    }

    public static async Task RunDirectVertexChatAsync() {
        var config = ConfigLoader<DirectAiChatSessionVertexConfig>.Load();
        Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
        var attachmentHandler = new AttachmentUploader(client, config.UploadFolder, config.IncludePaths, false, config.GcsBucketName);
        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var chatSession = new DirectAiChatSessionVertex(client, config, sessionLogger, attachmentHandler);
        await chatSession.StartAsync();
    }

    public static async Task RunFfmpegSessionAsync() {
        var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
        var ffmpegMenu = new FfmpegInteractiveSession(ffmpegConfig);
        await ffmpegMenu.StartAsync();
    }

    public static async Task RunAutoExtractionAsync() {
        string? extChoice = "1";
        if (AppConfig.IsVertexAiEnabled) {
            Console.WriteLine("\nWelche API soll für die automatisierte Extraktion genutzt werden?");
            Console.WriteLine(" 1) Google AI Studio");
            Console.WriteLine(" 2) Google Cloud Vertex AI");
            Console.Write("Wahl (1-2) oder 'exit': ");
            extChoice = Console.ReadLine()?.Trim().ToLower();
            if (extChoice == "exit" || extChoice == "quit") return;
        }
        else {
            Console.WriteLine("\n[Kostenschutz] Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false in appsettings.json). Starte automatisch mit Google AI Studio...");
        }

        if (extChoice == "2" && AppConfig.IsVertexAiEnabled) {
            var config = ConfigLoader<VertexAutoExtractionConfig>.Load();
            config.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(config.SourceFolder, newFolder => {
                config.SourceFolder = newFolder;
                ConfigLoader<VertexAutoExtractionConfig>.Save(config);
            }, config.PredefinedSourceFolders);
            string selectedModel = ConfigurationPrompts.ConfirmOrChangeModel(config.CurrentModel, "Vertex AI Auto-Extraktion", config.Model, newModel => {
                int idx = Array.IndexOf(config.Model, newModel);
                if (idx >= 0) config.CurrentModelIndex = idx;
                config.CurrentModel = newModel;
                ConfigLoader<VertexAutoExtractionConfig>.Save(config);
                ModelSyncService.SyncModelToRefinementConfig(newModel, isVertex: true);
            });
            if (selectedModel == "__EXIT__") return;
            config.CurrentModel = selectedModel;
            config.CurrentModelIndex = Math.Max(0, Array.IndexOf(config.Model, selectedModel));
            ModelSyncService.SyncModelToRefinementConfig(selectedModel, isVertex: true);

            Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
            var attachmentHandler = new AttachmentUploader(client, config.SourceFolder, [config.SourceFolder], false, config.GcsBucketName, config.GoogleVideoFps);
            var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
            var latexRefinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();
            var session = new VertexAutoExtractionSession(client, config, attachmentHandler, sessionLogger, latexRefinementConfig);
            await session.StartAsync();
        }
        else {
            var config = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
            config.SourceFolder = ConfigurationPrompts.PromptForSourceFolder(config.SourceFolder, newFolder => {
                config.SourceFolder = newFolder;
                ConfigLoader<AiStudioAutoExtractionConfig>.Save(config);
            }, config.PredefinedSourceFolders);
            string selectedModel = ConfigurationPrompts.ConfirmOrChangeModel(config.CurrentModel, "AI Studio Auto-Extraktion", config.Model, newModel => {
                int idx = Array.IndexOf(config.Model, newModel);
                if (idx >= 0) config.CurrentModelIndex = idx;
                config.CurrentModel = newModel;
                ConfigLoader<AiStudioAutoExtractionConfig>.Save(config);
                ModelSyncService.SyncModelToRefinementConfig(newModel, isVertex: false);
            });
            if (selectedModel == "__EXIT__") return;
            config.CurrentModel = selectedModel;
            config.CurrentModelIndex = Math.Max(0, Array.IndexOf(config.Model, selectedModel));
            ModelSyncService.SyncModelToRefinementConfig(selectedModel, isVertex: false);

            int selectedProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                config.ActiveApiProfile,
                "AI Studio Auto-Extraktion",
                newProfile => {
                    config.ActiveApiProfile = newProfile;
                    ConfigLoader<AiStudioAutoExtractionConfig>.Save(config);
                },
                config.AiStudioApiKeyEnvNames
            );
            config.ActiveApiProfile = selectedProfile;

            string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);

            string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
            Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
            var attachmentHandler = new AttachmentUploader(client, config.SourceFolder, [config.SourceFolder], true, "", config.GoogleVideoFps, config.InlineHistoryImages, config.FileActivationDelaySeconds, config.VideoUploadTimeoutSeconds, config.VideoUploadMaxRetries) {
                ClientFactory = () => GoogleAiClientBuilder.BuildAiStudioClient(apiKey)
            };
            var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
            var latexRefinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load(); // Load config for refinement
            var session = new AiStudioAutoExtractionSession(client, config, attachmentHandler, sessionLogger, latexRefinementConfig);
            await session.StartAsync();
        }
    }

    public static async Task RunLatexRefinementAsync() {
        var config = ConfigLoader<LatexRefinementSessionConfig>.Load();
        var extractionConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
        await RefinementUiHelper.StartInteractiveRefinementAsync(config, extractionConfig);
    }
}
