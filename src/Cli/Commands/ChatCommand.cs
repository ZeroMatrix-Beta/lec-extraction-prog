using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Chat;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] CLI entry point for interactive chat sessions. Supports directly jumping into a REPL
/// chat with Google AI Studio or Vertex AI from the command line, bypassing the interactive main menu
/// while preserving full interactive keyboard input via InteractivePromptSource.
/// [Human] Startet eine interaktive Chat-Sitzung mit Gemini (AI Studio oder Vertex AI) direkt über
/// die Kommandozeile, optional mit Modell- und Profil-Overrides.
/// </summary>
public static class ChatCommand {
    public static readonly Option<string?> Backend = new("--backend") {
        Description = "AI backend to use: 'aistudio' (default) or 'vertex'."
    };

    public static readonly Option<string?> Model = new("--model") {
        Description = "Model override for this session, e.g. gemini-3.6-flash."
    };

    public static readonly Option<int?> Profile = new("--profile") {
        Description = "API-key profile index for AI Studio (0 = dedicated, 1-3 = test projects)."
    };

    public static readonly Option<bool> SkipSetup = new("--skip-setup") {
        Description = "Skip interactive setup wizard prompts and enter chat directly."
    };

    public static readonly Option<string?> SystemPrompt = new("--system-prompt") {
        Description = "Path to custom system instruction file."
    };

    public static readonly Option<bool> NoSystemPrompt = new("--no-system-prompt") {
        Description = "Do not load any system instructions."
    };

    public static readonly Option<bool> NoHistory = new("--no-history") {
        Description = "Do not preload initial history attachments."
    };

    public static Command Build() {
        var command = new Command("chat", "Start an interactive chat session with Gemini (AI Studio or Vertex AI) from the command line.") {
            Backend, Model, Profile, SkipSetup, SystemPrompt, NoSystemPrompt, NoHistory
        };

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);

            if (context.DryRun) {
                CliOutput.Payload(context, new { dryRun = true, command = "chat" },
                    () => Ui.Info("Chat-Sitzung würde gestartet werden (--dry-run)."));
                return ExitCodes.Success;
            }

            // Restore interactive keyboard prompt source for the chat REPL loop
            Ui.PromptSource = new InteractivePromptSource();

            string backend = parseResult.GetValue(Backend) ?? "aistudio";
            bool skipSetup = parseResult.GetValue(SkipSetup) || context.AssumeYes;
            bool loadSys = !parseResult.GetValue(NoSystemPrompt);
            bool loadHist = !parseResult.GetValue(NoHistory);

            if (backend.Equals("vertex", StringComparison.OrdinalIgnoreCase)) {
                if (!AppConfig.IsVertexAiEnabled) {
                    Ui.Error("Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false in appsettings.json).", "Chat");
                    return ExitCodes.Configuration;
                }

                var config = ConfigLoader<DirectAiChatSessionVertexConfig>.Load();
                if (parseResult.GetValue(Model) is { } modelOverride) {
                    config.CurrentModel = modelOverride;
                }
                if (parseResult.GetValue(SystemPrompt) is { } sysPromptOverride) {
                    config.SystemInstructionPath = sysPromptOverride;
                }
                if (parseResult.GetValue(NoHistory)) {
                    config.HistoryPreloadPaths = [];
                }

                Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
                var attachmentHandler = new AttachmentUploader(client, config.UploadFolder, config.IncludePaths, false, config.GcsBucketName);
                var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
                var chatSession = new DirectAiChatSessionVertex(client, config, sessionLogger, attachmentHandler);

                await chatSession.StartAsync(skipSetup, loadSys, loadHist);
                return ExitCodes.Success;
            }

            if (backend.Equals("aistudio", StringComparison.OrdinalIgnoreCase)) {
                var config = ConfigLoader<DirectAiChatSessionAiStudioConfig>.Load();
                if (parseResult.GetValue(Profile) is { } profileOverride) {
                    config.ActiveApiProfile = profileOverride;
                }
                if (parseResult.GetValue(Model) is { } modelOverride) {
                    config.CurrentModel = modelOverride;
                }
                if (parseResult.GetValue(SystemPrompt) is { } sysPromptOverride) {
                    config.SystemInstructionPath = sysPromptOverride;
                }
                if (parseResult.GetValue(NoHistory)) {
                    config.HistoryPreloadPaths = [];
                }

                string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);
                string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName);
                if (string.IsNullOrEmpty(apiKey)) {
                    Ui.Error($"Der API-Key für Profil {config.ActiveApiProfile} ({envName}) konnte nicht gefunden werden.", "Chat");
                    return ExitCodes.Configuration;
                }

                Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
                var attachmentHandler = new AttachmentUploader(client, config.UploadFolder, config.IncludePaths, true, config.GcsBucketName);
                var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
                var chatSession = new DirectAiChatSessionAiStudio(client, config, sessionLogger, attachmentHandler, isAiStudio: true);

                await chatSession.StartAsync(skipSetup, loadSys, loadHist);
                return ExitCodes.Success;
            }

            Ui.Error($"Unbekanntes Backend '{backend}'. Erlaubt sind 'aistudio' oder 'vertex'.", "Chat");
            return ExitCodes.Usage;
        });

        return command;
    }
}
