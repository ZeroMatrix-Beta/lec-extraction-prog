using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for the conversational AI interface.
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots. Hier werden deine Eingaben gelesen, an Google gesendet und die Antworten in der Konsole ausgegeben.
/// </summary> 
public partial class DirectAiChatSessionAiStudio {
    public static readonly string[] AvailableModels = [
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview",
        "gemini-2.5-flash"
    ];

    private readonly DirectAiChatSessionAiStudioConfig _config;

    // [AI Context] Global state for file resolution. 
    // UploadFolderPath is the base dir for relative paths. HistoryFolderPath is an absolute path.
    // Konfigurierbarer Basis-Pfad für deine Uploads. 
    // Z.B.: @"C:\Users\miche\programming\lec-extraction-prog\uploads"
    private readonly string UploadFolderPath;

    // Absoluter Pfad zum Ordner für die automatisch zu ladende History.
    // Z.B.: @"C:\Users\miche\programming\lec-extraction-prog\history"
    private readonly string[] HistoryPreloadPaths;

    // Standard-Nachricht, die gesendet wird, wenn die History geladen wird.
    private readonly string InitialHistoryPrompt = "Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {0}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards.";

    // [GCS] Der Name deines Google Cloud Storage Buckets
    // Z.B.: "en-linalg-biran-gemini-videos"
    private readonly string GcsBucketName;

    // [Log-Ordner] Status für den aktuellen Programmablauf
    private readonly string LogFolderPath;
    private readonly string SystemInstructionPath;
    private string? _systemInstructionText;
    private readonly DirectAiChatSessionAiStudioGenerationConfig AIParams;
    private readonly bool IsAiStudio;
    private readonly AttachmentUploader _attachmentHandler;
    private readonly SessionLogger _sessionLogger;
    private Client _client;
    private int _activeApiProfile;
    // Owns the running session token totals; they were read nowhere else.
    private readonly ResponseStreamPrinter _streamPrinter = new();
    private string _activeModel = "";

    // [AI Context] Constructor injects config dependencies to isolate state.
    public DirectAiChatSessionAiStudio(Client client, DirectAiChatSessionAiStudioConfig config, SessionLogger logger, AttachmentUploader attachmentHandler, bool isAiStudio) {
        _config = config;
        _client = client;
        _sessionLogger = logger;
        _attachmentHandler = attachmentHandler;
        IsAiStudio = isAiStudio;
        UploadFolderPath = config.UploadFolder;
        HistoryPreloadPaths = config.HistoryPreloadPaths;
        LogFolderPath = config.LogFolder;
        GcsBucketName = config.GcsBucketName;
        SystemInstructionPath = config.SystemInstructionPath;
        _activeApiProfile = config.ActiveApiProfile;
        _activeModel = config.CurrentModel;

        // [AI Context] Creates a localized deep copy of AI parameters.
        // [Human] Kopiert die Standard-Werte, damit wir sie später mit "/set temp" im Chat verändern können, ohne das Original zu überschreiben.
        // Wir legen eine lokale Kopie an, damit /set Befehle nur diese Sitzung modifizieren
        AIParams = new DirectAiChatSessionAiStudioGenerationConfig {
            Temperature = config.AI.Temperature,
            TopP = config.AI.TopP,
            TopK = config.AI.TopK,
            MaxOutputTokens = config.AI.MaxOutputTokens,
            UseGoogleSearch = config.UseGoogleSearch
        };
    }

    /// <summary>
    /// [AI Context] Asynchronous entry point for the session. Initializes API clients and directory structures.
    /// [Human] Startet die Session, verbindet sich mit Google und erstellt die Log-Ordner für diesen Chat-Verlauf.
    /// </summary>
    public async Task StartAsync() {
        // [AI Context] The setup runs as a three-step machine so every prompt can offer "back": each
        // step knows its predecessor, and a profile change (Restart) rewinds to the first step
        // because the client built from the old profile is stale. The side effects that used to sit
        // between the prompts - bucket cleanup and creating the session log folder - moved behind
        // the last step, so backing out no longer leaves an empty session folder behind.
        // [Human] Das Setup läuft als Schrittkette, damit jede Frage ein "Zurück" anbieten kann.
        int step = 0;
        bool loadedSysPrompt = false;
        string? initialInput = null;

        while (true) {
            switch (step) {
                case 0: {
                    var model = ConfigurationPrompts.ConfirmOrChangeModel(_config.CurrentModel, "AI Studio", _config.Model, newModel => {
                        int idx = Array.IndexOf(_config.Model, newModel);
                        if (idx >= 0) _config.CurrentModelIndex = idx;
                        _config.CurrentModel = newModel;
                        ConfigLoader<DirectAiChatSessionAiStudioConfig>.Save(_config);
                    });
                    if (!model.IsValue) return; // Back at the first step leaves the session
                    _activeModel = model.Value!;
                    step = 1;
                    break;
                }

                case 1: {
                    // [AI Context] Load System Instructions (Persona & Rules) into memory.
                    if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
                        Ui.Blank();
                        Ui.Info("Folgende System Instruction ist konfiguriert:", "Setup");
                        FileTreeRenderer.PrintFileTree([SystemInstructionPath]);
                    }

                    var answer = SetupQuestionPrompt.Ask("[Setup] System Instruction laden?", ChangeApiKeyProfileInteractive);
                    if (answer.IsRestart || answer.IsBack) { step = 0; break; }
                    if (!answer.IsValue) return;

                    loadedSysPrompt = false;
                    _systemInstructionText = null;

                    if (answer.Value) {
                        if (!string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath)) {
                            _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
                            Ui.Success($"System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
                            loadedSysPrompt = true;
                        }
                        else {
                            Ui.Warn($"System-Prompt-Datei nicht gefunden: {SystemInstructionPath}");
                        }
                    }
                    else {
                        Ui.Info("System Instruction wird ignoriert.");
                    }

                    step = 2;
                    break;
                }

                case 2: {
                    var history = GetInitialHistoryCommand(_activeModel);
                    if (history.IsRestart) { step = 0; break; }
                    if (history.IsBack) { step = 1; break; }
                    if (!history.IsValue) return;
                    initialInput = history.Value;
                    step = 3;
                    break;
                }

                default: {
                    // 3b. Bucket beim Start aufräumen (falls von einem vorherigen Absturz noch Videos übrig sind)
                    await CleanupGcsBucketAsync();

                    // [AI Context] Implements session persistence by isolating text/LaTeX outputs in discrete timestamped directories.
                    // [Human] Erstellt für jede neue Chat-Sitzung einen eigenen Ordner, damit nichts aus Versehen überschrieben wird.
                    // 3c. Session Log-Ordner ermitteln und erstellen (folder-1, folder-2...)
                    _sessionLogger.InitializeSession();
                    _sessionLogger.SetSessionMetadata(loadedSysPrompt, initialInput != null);
                    await _sessionLogger.LogSessionSetupAsync();

                    // 4. Starte die Haupt-Chat-Schleife
                    await RunChatSessionAsync(initialInput);
                    return;
                }
            }
        }
    }

    // --- Ausgelagerte Methoden ---

    /// <summary>
    /// [AI Context] Main REPL loop. 
    /// Mutates the 'history' list to maintain conversation state. Catches errors to prevent chat state corruption.
    /// [Human] Hauptschleife des Chats: Liest kontinuierlich Benutzereingaben, verarbeitet Befehle, sendet Nachrichten an die Gemini-API und gibt die gestreamten Antworten in der Konsole aus.
    /// </summary>
    private async Task RunChatSessionAsync(string? initialInput) {
        var history = new List<Content>();

        // [AI Context] Cache initial state to allow memory resets without restarting the runtime.
        // [Human] Speichert den Zustand nach dem ersten Laden ab. So funktioniert der "clear" Befehl!
        var initialHistory = new List<Content>(history); // Den Startzustand merken
        string userName = "AI Studio User";

        Ui.Header($"Chat gestartet ({_activeModel} | API Profil: {_activeApiProfile})");
        WriteCommandHelp();

        while (true) {
            using var turnCts = new CancellationTokenSource();
            void turnCancelHandler(object? sender, ConsoleCancelEventArgs e) {
                e.Cancel = true;
                try { turnCts.Cancel(); } catch { }
            }
            Console.CancelKeyPress += turnCancelHandler;

            try {
                string? input;
                if (initialInput != null) {
                    // [AI Context] Automatically executes the history attachment command on the first loop iteration without requiring user interaction.
                    // Echoed through Ui.Raw, not a markup helper: the generated command carries quoted
                    // file paths and the history prompt's own "[AI-Model: ...]" and "[...]" literals.
                    input = initialInput;
                    Ui.Blank();
                    Ui.RawLine($"{userName}: {input}");
                    initialInput = null; // Nur beim allerersten Durchlauf verwenden
                }
                else {
                    // [AI Context] Flush the input buffer before asking for new input.
                    // Prevents confusing "ghost inputs" if the user typed something while the AI was generating or waiting in a Task.Delay backoff loop.
                    if (!Console.IsInputRedirected) {
                        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                    }
                    // [AI Context] The one prompt in this file that is not a Ui prompt. Spectre's
                    // TextPrompt rejects empty input and re-asks, but an empty line here is a valid
                    // no-op the loop skips with 'continue', and a chat turn may legitimately be any
                    // text at all - including markup-looking text. So: raw label, raw ReadLine.
                    // [Human] Bewusst eine einfache Eingabezeile - ein Spectre-Prompt würde leere
                    // Eingaben ablehnen und den Chat-Rhythmus brechen.
                    Ui.Blank();
                    Ui.Raw($"{userName}: ");
                    input = Console.ReadLine();
                }

                if (string.IsNullOrWhiteSpace(input)) continue;
                if (input.Equals("exit", StringComparison.CurrentCultureIgnoreCase) || input.Equals("quit", StringComparison.CurrentCultureIgnoreCase)) break;

                var parts = new List<Part>();
                string promptText = input;

                // Extract command handling to keep the main loop focused purely on the chat flow
                // [AI Context] Uses a Command/Interceptor pattern. If TryHandleBuiltInCommandsAsync returns true, the input was a local REPL command, avoiding an API call.
                bool isCommandHandled = await TryHandleBuiltInCommandsAsync(input, history, initialHistory, parts, newPrompt => promptText = newPrompt, turnCts.Token);

                // If the command handler took care of everything (or failed gracefully), we skip the API call for this turn.
                if (isCommandHandled) {
                    // The only exception is the 'attach' command, which modifies our parts/prompt and STILL wants to talk to Gemini
                    if (!input.TrimStart('/').StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    // If 'attach' failed (e.g., file not found), 'parts' will be empty and we skip the turn
                    if (parts.Count == 0) continue;
                }

                // 6. Text-Prompt anhängen und an die Historie übergeben
                if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
                else if (parts.Count == 0) continue;

                history.Add(new Content { Role = "user", Parts = parts });

                try {
                    // [AI Context] Hands off to streaming handler. Mutates 'history' internally.
                    // The resilience logic is now inside StreamGeminiResponseAsync.
                    await StreamGeminiResponseAsync(_activeModel, history, input, promptText, userName);
                }
                catch (Exception ex) {
                    // This block now catches unrecoverable errors re-thrown by the resilience helper.
                    Ui.Blank();
                    Ui.Error("Der Fehler konnte nicht durch einen automatischen Retry behoben werden.", "Abbruch");
                    Ui.Error($"Originaler Fehlertext: {ex.Message}");

                    // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
                    if (history.Count > 0 && history.Last().Role == "user") {
                        history.RemoveAt(history.Count - 1);
                    }
                }
            }
            finally {
                Console.CancelKeyPress -= turnCancelHandler;
            }
        }

        Ui.Blank();
        Ui.Info("Chat beendet. Räume temporäre Dateien im Cloud Storage auf...");
        await CleanupGcsBucketAsync();
    }

    /// <summary>
    /// [AI Context] The setup-time entry point for switching the API key profile, offered as a menu
    /// entry by <see cref="SetupQuestionPrompt"/>. Same effect as typing <c>change-key N</c> inside
    /// the chat, but reachable without knowing the command exists.
    /// [Human] Wechselt das API-Key Profil während des Setups über das Menü statt per Tippbefehl.
    /// </summary>
    private void ChangeApiKeyProfileInteractive() {
        var profile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
            _activeApiProfile,
            "Direct AI Studio Chat",
            envNames: _config.AiStudioApiKeyEnvNames,
            allowBack: false);

        if (profile.IsValue && profile.Value != _activeApiProfile) {
            ApplyApiKeyProfile(profile.Value);
        }
    }

    private static void WriteCommandHelp() {
        Ui.Step("📋 Befehle");
        Ui.Detail("📜 help / commands         -> Zeigt diese Befehlsübersicht erneut an");
        Ui.Detail("🚪 exit / quit             -> Beendet den Chat");
        Ui.Detail("🧹 clear / reset           -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
        Ui.Detail("📎 attach datei1 | Frage   -> Hängt Dateien an und stellt eine Frage dazu.");
        Ui.Detail("                           (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
        Ui.Detail("🌡️  set temp [wert]         -> Ändert die Temperatur für die nächste Antwort (z.B. set temp 0.5)");
        Ui.Detail("🔢 set tokens [wert]       -> Ändert das MaxOutputTokens-Limit dynamisch (z.B. set tokens 8192)");
        Ui.Detail("🧠 set thinking-budget [w] -> Setzt das Thinking Budget für Gemini 2.5 (z.B. 4096)");
        Ui.Detail("🧠 set thinking-level [l]  -> Setzt das Thinking Level für Gemini 3.x (z.B. HIGH)");
        Ui.Detail("🔍 set grounding [on/off]  -> Aktiviert/Deaktiviert Google Search Grounding (Websuche)");
        Ui.Detail("🤖 set model [name/index]  -> Ändert das aktive Modell mitten im Chat");
        Ui.Detail("🔑 change-key [0-3]        -> Wechselt das API-Key Profil für diese Session (0 für dediziert)");
    }

    /// <summary>
    /// [AI Context] Intercepts and executes local REPL commands (e.g., /clear, /set temp) to avoid sending them as prompts to the AI.
    /// [Human] Verarbeitet alle eingebauten /- oder Kommando-Befehle, um die Hauptschleife sauber zu halten. Returns true, wenn der Input ein Befehl war.
    /// </summary>
    /// <summary>
    /// [AI Context] Recognises the input with <see cref="ChatCommandParser"/> and applies it. Parsing
    /// is separate from applying so the command surface is testable without a live session - the nine
    /// hand-written handlers this replaces each did both, which is how <c>set thinking-budget</c> and
    /// <c>set thinking-level</c> stayed broken: their arguments were sliced at hand-counted offsets
    /// two and one characters short of their prefixes, so both always took the error branch.
    /// [Human] Erkennt und führt die eingebauten Befehle aus; das Zerlegen passiert getrennt und
    /// getestet im ChatCommandParser.
    /// </summary>
    private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText, CancellationToken cancellationToken) {
        var command = ChatCommandParser.Parse(input);

        // The parser reports a bad argument rather than throwing; the command was still recognised,
        // so the turn is consumed either way and never reaches the model.
        if (!command.IsValid) {
            Ui.Error(command.Error!); // IsValid is exactly "Error == null"
            return true;
        }

        switch (command.Kind) {
            case ChatCommandKind.Help:
                WriteCommandHelp();
                return true;

            case ChatCommandKind.Clear:
                history.Clear();
                history.AddRange(initialHistory);
                Ui.Blank();
                Ui.Info("Gedächtnis gelöscht! Gemini startet komplett frisch.");
                return true;

            case ChatCommandKind.SetTemperature:
                AIParams.Temperature = command.Number;
                Ui.Info($"Temperatur für die nächste(n) Antwort(en) auf {AIParams.Temperature:F1} gesetzt.");
                return true;

            case ChatCommandKind.SetMaxTokens:
                AIParams.MaxOutputTokens = command.Integer;
                Ui.Info($"MaxOutputTokens für die nächste(n) Antwort(en) auf {AIParams.MaxOutputTokens} gesetzt.");
                return true;

            case ChatCommandKind.SetThinkingBudget:
                AIParams.ThinkingBudget = command.Integer;
                Ui.Info($"ThinkingBudget für die nächste(n) Antwort(en) auf {AIParams.ThinkingBudget} gesetzt (relevant für Gemini 2.5 Modelle).");
                return true;

            case ChatCommandKind.SetThinkingLevel:
                AIParams.ThinkingLevel = command.Text;
                Ui.Info($"ThinkingLevel für die nächste(n) Antwort(en) auf '{AIParams.ThinkingLevel}' gesetzt (relevant für Gemini 3.x Modelle).");
                return true;

            case ChatCommandKind.SetGrounding:
                AIParams.UseGoogleSearch = command.Flag;
                // Written as if/else rather than a ternary so both strings stay literal arguments of a
                // Ui call - dump-ui-strings.sh matches on that shape, and a ternary hides them.
                if (command.Flag) {
                    Ui.Info("Google Search Grounding für die nächste(n) Antwort(en) AKTIVIERT.");
                }
                else {
                    Ui.Info("Google Search Grounding für die nächste(n) Antwort(en) DEAKTIVIERT.");
                }
                return true;

            case ChatCommandKind.ChangeApiKeyProfile:
                ApplyApiKeyProfile(command.Integer);
                return true;

            case ChatCommandKind.SetModel:
                ApplySetModel(command.Text);
                return true;

            case ChatCommandKind.Attach: {
                var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(input.TrimStart('/'), cancellationToken: cancellationToken);

                // Handled but failed: returning true with empty 'parts' makes the main loop skip the
                // turn cleanly rather than sending a prompt with nothing attached.
                if (!success) return true;

                parts.AddRange(attachmentParts);
                updatePromptText(parsedPrompt);
                return true;
            }

            default:
                return false; // Not a built-in command - send it to the model
        }
    }

    /// <summary>
    /// [AI Context] Applies the <c>set model</c> command. Keeps its interactive picker: with no
    /// argument it lists the configured models and reads a choice, which is also the only way to
    /// reach a freetext model name such as a Gemma build - so the list carries an explicit
    /// "manuell eingeben" entry rather than relying on the old picker's undocumented behaviour of
    /// treating any unparseable answer as a model name.
    /// [Human] Wechselt das Modell; ohne Argument mit Auswahlliste plus Freitext-Eintrag.
    /// </summary>
    private void ApplySetModel(string arg) {
        string newModel = string.IsNullOrEmpty(arg)
            ? ChatModelPrompt.Pick(AvailableModels)
            : ChatModelPrompt.Resolve(arg, AvailableModels);

        if (string.IsNullOrEmpty(newModel)) return;

        _activeModel = newModel;
        Ui.Info($"Aktives Modell für die nächste(n) Antwort(en) auf '{_activeModel}' geändert.");

        if (Ui.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", true)) {
            int idx = Array.IndexOf(AvailableModels, _activeModel);
            if (idx >= 0) _config.CurrentModelIndex = idx;
            _config.CurrentModel = _activeModel;
            ConfigLoader<DirectAiChatSessionAiStudioConfig>.Save(_config);
            Ui.Success("💾 Das neue Modell wurde permanent in der Konfiguration gespeichert.");
        }
        else {
            Ui.Info("Die Änderung ist nur vorübergehend.");
        }
    }

    /// <summary>
    /// [AI Context] Response streaming & state update.
    /// Side-effects: Mutates 'history' list by appending the assistant's full response. Appends raw text to 'chat_log.md'.
    /// [Human] Streamt die Antwort von Gemini asynchron in die Konsole und speichert das Ergebnis in der Historie und einem Logfile.
    /// </summary>
    private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText, string userName) {
        Ui.Blank();
        Ui.Raw($"{selectedModel} (Drücke Strg+C zum Abbrechen): ");

        var (config, apiContents) = BuildChatRequestConfig(selectedModel, history);
        var (fullResponse, inputTokens, outputTokens, cachedTokens) =
            await _streamPrinter.StreamAsync(_client, selectedModel, apiContents, config, WaitForQuotaWindowAsync);

        // 7. KI-Antwort in die Historie aufnehmen
        if (!string.IsNullOrWhiteSpace(fullResponse)) {
            history.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
            await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse, userName, inputTokens, outputTokens, cachedTokens);
        }
        else {
            // [AI Context] Falls abgebrochen wurde, bevor die KI etwas gesagt hat,
            // müssen wir die User-Nachricht entfernen, um "Consecutive User Message"-Errors zu vermeiden.
            history.RemoveAt(history.Count - 1);
        }
    }

    /// <summary>
    /// [AI Context] Maps current dynamic AI params (temperature, thinking, grounding) and the system
    /// instruction onto the request. Gemma models (pre-v4) don't support the 'system' role, so for
    /// those the instruction is prepended into the first user message instead of the dedicated field.
    /// [Human] Baut die Anfrage-Konfiguration und den ggf. für Gemma angepassten History-Kontext.
    /// </summary>
    private (GenerateContentConfig Config, List<Content> ApiContents) BuildChatRequestConfig(string selectedModel, List<Content> history) {
        var config = new GenerateContentConfig {
            Temperature = AIParams.Temperature,
            TopP = AIParams.TopP,
            TopK = AIParams.TopK,
            MaxOutputTokens = AIParams.MaxOutputTokens
        };

        if (AIParams.UseGoogleSearch) {
            config.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        // [AI Context] Safely inject Thinking parameters.
        if (ModelCapabilities.SupportsThinking(selectedModel)) {
            bool isGemini25 = selectedModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(AIParams.ThinkingLevel)) {
                config.ThinkingConfig = new ThinkingConfig { ThinkingLevel = AIParams.ThinkingLevel };
            }
            else if (AIParams.ThinkingBudget.HasValue) {
                config.ThinkingConfig = new ThinkingConfig { ThinkingBudget = AIParams.ThinkingBudget };
            }
        }

        var apiContents = history; // By default, use the original history

        // Pass the Director's Cut Protocol as an absolute System Instruction
        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
            // Gemma models (pre-v4) don't support the 'system' role.
            // We prepend the instruction to the first user message instead.
            if (ModelCapabilities.RequiresSystemInstructionInFirstUserTurn(selectedModel)) {
                if (!history.Any(c => c.Role == "model")) { // isFirstTurn
                    var modifiedHistory = new List<Content>();
                    bool prepended = false;
                    foreach (var content in history) {
                        if (!prepended && content.Role == "user") {
                            var newParts = content.Parts?.ToList() ?? [];
                            newParts.Insert(0, new Part { Text = $"System Instruction:\n{_systemInstructionText}\n\n---\n\nUser Request:\n" });
                            modifiedHistory.Add(new Content { Role = "user", Parts = newParts });
                            prepended = true;
                        }
                        else {
                            modifiedHistory.Add(content);
                        }
                    }
                    apiContents = modifiedHistory;
                    config.SystemInstruction = null; // Ensure it's not sent in the dedicated field
                }
            }
            else {
                // For all other models (Gemini, Gemma v4+), use the standard system instruction field.
                config.SystemInstruction = new Content { Role = "system", Parts = [new() { Text = _systemInstructionText }] };
            }
        }

        return (config, apiContents);
    }

    /// <summary>
    /// [AI Context] Rate-Limit &amp; Quota Guardrail: Always wait 130s before every
    /// GenerateContentStreamAsync request to Google AI Studio. HasJustUploaded is intentionally NOT
    /// checked here – the 130s in AttachmentUploader does not replace this per-request delay. Vertex
    /// has no equivalent, which is why this stays at the call site rather than inside
    /// <see cref="ResponseStreamPrinter"/>. Returns false when the user cancels the wait.
    /// [Human] Wir warten VOR JEDEM AI-Studio-Request 130 Sekunden, egal ob gerade eine Datei
    /// hochgeladen wurde oder nicht.
    /// </summary>
    private static async Task<bool> WaitForQuotaWindowAsync() {
        bool proceed = true;
        if (!InteractiveDelay.IsInSmartDelay) {
            proceed = await InteractiveDelay.SmartDelayAsync(130, "Warte 130 Sekunden vor API-Request an Google AI Studio (Token-Refill Schutz für Max-Token/Quota)...");
        }
        AttachmentUploader.HasJustUploaded = false;
        return proceed;
    }

    /// <summary>
    /// [AI Context] Searches configured directories for previous chat histories and prepares an attachment command to preload context.
    /// [Human] Fragt den Nutzer, ob eine bestehende History geladen werden soll, und baut den entsprechenden /attach Befehl zusammen.
    /// </summary>
    private PromptResult<string?> GetInitialHistoryCommand(string selectedModel) {
        if (HistoryPreloadPaths == null || HistoryPreloadPaths.Length == 0) {
            return PromptResult.FromValue<string?>(null);
        }

        var allHistoryFiles = new List<string>();
        var notFoundPaths = new List<string>();

        foreach (var path in HistoryPreloadPaths.Where(p => !string.IsNullOrWhiteSpace(p))) {
            if (System.IO.File.Exists(path)) {
                allHistoryFiles.Add(Path.GetFullPath(path));
            }
            else if (Directory.Exists(path)) {
                allHistoryFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f)));
            }
            else {
                notFoundPaths.Add(path);
            }
        }

        if (notFoundPaths.Count > 0) {
            Ui.Blank();
            Ui.Warn("Folgende History-Pfade wurden nicht gefunden:", "Setup");
            foreach (var path in notFoundPaths) {
                Ui.Detail($"- {path}");
            }
        }

        var distinctFiles = allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Verhindert, dass die System Instruction versehentlich als History geladen wird, 
        // falls der Nutzer sie physisch im History-Ordner abgelegt hat.
        if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
            distinctFiles = [.. distinctFiles.Where(f => !string.Equals(f, Path.GetFullPath(SystemInstructionPath), StringComparison.OrdinalIgnoreCase))];
        }

        if (distinctFiles.Count == 0) {
            return PromptResult.FromValue<string?>(null);
        }

        Ui.Blank();
        Ui.Info("Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:", "Setup");
        FileTreeRenderer.PrintFileTree(distinctFiles);

        var answer = SetupQuestionPrompt.Ask("Sollen diese Dateien als History geladen werden?", ChangeApiKeyProfileInteractive);
        if (!answer.IsValue) return new PromptResult<string?>(answer.Outcome, null);

        if (!answer.Value) return PromptResult.FromValue<string?>(null);

        // Die `historyFiles` enthalten bereits die vollen, absoluten Pfade.
        // Wir können sie direkt verwenden und für den Befehl in Anführungszeichen setzen.
        string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        return PromptResult.FromValue<string?>($"attach {fileList} | {string.Format(InitialHistoryPrompt, selectedModel)}");
    }

    /// <summary>
    /// [AI Context] Dynamically swaps the active API key profile during a session to manage quotas
    /// without restarting. Both entry points reach it here - the typed <c>change-key N</c> command
    /// (recognised and range-checked by <see cref="ChatCommandParser"/>) and the setup menu's
    /// <see cref="ChangeApiKeyProfileInteractive"/> - so the two cannot drift apart.
    /// [Human] Baut den Client mit dem Key des gewählten Profils neu auf.
    /// </summary>
    private void ApplyApiKeyProfile(int newProfile) {
        string? newApiKey;
        if (newProfile == 0) {
            // [AI Context] Profile 0 is a convention for the dedicated, high-quota extraction key.
            newApiKey = GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-automated-content-extraction");
        }
        else {
            newApiKey = GoogleAiClientBuilder.ResolveApiKey(newProfile);
        }

        if (!string.IsNullOrEmpty(newApiKey)) {
            _client = GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
            _attachmentHandler.UpdateClient(_client);
            _activeApiProfile = newProfile;
            Ui.Success($"API-Key Profil für diese Session erfolgreich auf {newProfile} gewechselt!");
        }
        else {
            Ui.Error($"Konnte API-Key für Profil {newProfile} nicht finden. Der Wechsel wurde abgebrochen.");
        }
    }

    /// <summary>
    /// [AI Context] Financial guardrail: Purges all files in the designated GCS bucket to prevent long-term storage billing.
    /// [Human] Löscht alle Dateien im konfigurierten Google Cloud Storage Bucket. Wird beim Start (für Dateileichen) und beim Beenden (für aktuelle Uploads) aufgerufen.
    /// </summary>
    private async Task CleanupGcsBucketAsync() {
        // The free-tier guard stays here: whether this session has a bucket at all is the session's
        // own knowledge, not the purge's. Everything below it is shared with the Vertex chat session.
        if (IsAiStudio) return; // Prevent free-tier from pinging GCS

        await GcsWorkspace.PurgeAsync(GcsBucketName, _config.VerboseConsoleOutput);
    }

}