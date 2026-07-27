using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using static System.Console;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] Core REPL manager specifically for Google Cloud Vertex AI interactions.
/// This completely isolates the enterprise execution context from the developer AI Studio context.
/// [Human] Der Manager für Chat-Sitzungen, die über Google Cloud Vertex AI laufen. Getrennt vom normalen AI Studio, da es eigene Abrechnungs- und Zugriffsregeln hat.
/// </summary>
public class DirectAiChatSessionVertex {
    public static readonly string[] AvailableModels = [
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview"
    ];

    private readonly DirectAiChatSessionVertexConfig _config;
    private readonly string UploadFolderPath;
    private readonly string[] HistoryPreloadPaths;
    private readonly string InitialHistoryPrompt = "Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {0}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards.";
    private readonly string GcsBucketName;
    private readonly string LogFolderPath;
    private readonly string SystemInstructionPath;
    private string? _systemInstructionText; // Stores the content of the system instruction file
    private readonly DirectAiChatSessionVertexAIConfig AIParams; // Localized generation parameters for the current session
    private readonly AttachmentHandler _attachmentHandler;
    private readonly SessionLogger _sessionLogger;
    private readonly Client _client;
    private int _sessionTotalInputTokens = 0;
    private int _sessionTotalOutputTokens = 0;
    private int _sessionTotalCachedTokens = 0;
    private string _activeModel = "";

    // [AI Context] Constructor receives injected dependencies. The 'client' here is strictly a Vertex-configured client (GoogleAiClientBuilder.BuildVertexClient).
    public DirectAiChatSessionVertex(Client client, DirectAiChatSessionVertexConfig config, SessionLogger logger, AttachmentHandler attachmentHandler) {
        _config = config;
        _client = client;
        _sessionLogger = logger;
        _attachmentHandler = attachmentHandler;
        UploadFolderPath = config.UploadFolder;
        HistoryPreloadPaths = config.HistoryPreloadPaths;
        LogFolderPath = config.LogFolder;
        GcsBucketName = config.GcsBucketName; // [AI Context] Crucial: The designated Google Cloud Storage bucket used exclusively for Vertex AI multimodal attachments.
        SystemInstructionPath = config.SystemInstructionPath;
        _activeModel = config.CurrentModel;

        AIParams = new DirectAiChatSessionVertexAIConfig {
            Temperature = config.AI.Temperature,
            TopP = config.AI.TopP,
            TopK = config.AI.TopK,
            MaxOutputTokens = config.AI.MaxOutputTokens,
            UseGoogleSearch = config.UseGoogleSearch
        };
    }

    /// <summary>
    /// [AI Context] Asynchronous entry point for the Vertex session. Initializes clients and enforces rigorous bucket cleanup.
    /// [Human] Startet die Vertex-Session, verbindet sich mit Google Cloud und stellt sicher, dass der GCS Bucket für Datei-Uploads komplett leer ist.
    /// </summary>
    public async Task StartAsync() {
        while (true) {
            string selectedModel = ConsoleUiHelper.ConfirmOrChangeModel(_config.CurrentModel, "Vertex AI", _config.Model, newModel => {
                int idx = Array.IndexOf(_config.Model, newModel);
                if (idx >= 0) _config.CurrentModelIndex = idx;
                _config.CurrentModel = newModel;
                ConfigLoader<DirectAiChatSessionVertexConfig>.Save(_config);
            });
            if (selectedModel == "__EXIT__") return;
            _activeModel = selectedModel;

            WriteLine("\n[System] Initiating Vertex AI Enterprise Session...");

            // ALWAYS clean up the bucket completely before starting a session (crash recovery)
            await ForcePurgeGcsBucketAsync();

            _sessionLogger.InitializeSession();

            bool loadedSysPrompt = false;
            if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
                WriteLine("\n[Setup] Folgende System Instruction ist konfiguriert:");
                ExtractionHelpers.PrintFileTree([SystemInstructionPath]);
            }
            string sysPromptChoice = PromptWithCommands("[Setup] System Instruction laden? (j/n): ");
            if (sysPromptChoice == "__EXIT__") return;

            if (sysPromptChoice.Trim().Equals("j", StringComparison.CurrentCultureIgnoreCase)) {
                if (!string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath)) {
                    _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
                    WriteLine($"  [INFO] System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
                    loadedSysPrompt = true;
                }
                else {
                    WriteLine($"  [WARNUNG] System-Prompt-Datei nicht gefunden: {SystemInstructionPath}");
                }
            }
            else {
                WriteLine("  [INFO] System Instruction wird ignoriert.");
            }

            string? initialInput = GetInitialHistoryCommand(_activeModel);
            if (initialInput == "__EXIT__") return;

            bool loadedHistory = initialInput != null;

            _sessionLogger.SetSessionMetadata(loadedSysPrompt, loadedHistory);
            await _sessionLogger.LogSessionSetupAsync();

            await RunChatSessionAsync(initialInput);
            break; // Session beendet, zurück zu Program.cs
        }
    }

    /// <summary>
    /// [AI Context] Main REPL loop for Vertex. Manages state, handles commands, and invokes streaming responses.
    /// [Human] Hauptschleife für den Vertex-Chat. Nimmt Eingaben entgegen und verarbeitet sie fortlaufend.
    /// </summary>
    private async Task RunChatSessionAsync(string? initialInput) {
        var history = new List<Content>();
        var initialHistory = new List<Content>(history);
        string userName = "Vertex AI User";

        WriteLine($"\n--- Vertex Chat gestartet ({_activeModel}) ---");
        ShowCommands();

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
                    input = initialInput;
                    WriteLine($"\n{userName}: {input}");
                    initialInput = null;
                }
                else {
                    Write($"\n{userName}: ");
                    input = ReadLine();
                }

                if (string.IsNullOrWhiteSpace(input)) continue;
                if (input.Equals("exit", StringComparison.CurrentCultureIgnoreCase) || input.Equals("quit", StringComparison.CurrentCultureIgnoreCase)) break;

                var parts = new List<Part>();
                string promptText = input;

                bool isCommandHandled = await TryHandleBuiltInCommandsAsync(input, history, initialHistory, parts, newPrompt => promptText = newPrompt, turnCts.Token);

                if (isCommandHandled) {
                    if (!input.TrimStart('/').StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) continue;
                    if (parts.Count == 0) continue;
                }

                if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
                else if (parts.Count == 0) continue;

                history.Add(new Content { Role = "user", Parts = parts });

                try {
                    await StreamGeminiResponseAsync(_activeModel, history, input, promptText, userName);
                }
                catch (Exception ex) {
                    WriteLine($"\n[Vertex Error]: {ex.Message}");

                    if (ex.Message.Contains("Service agents are being provisioned", StringComparison.OrdinalIgnoreCase)) {
                        WriteLine($"\n[Vertex Info]: Google Cloud richtet gerade im Hintergrund die Zugriffsrechte (Service Agents) für deinen Bucket ein. Das passiert meistens nur beim allerersten Mal im Projekt. Bitte warte einfach 2-3 Minuten und versuche die Anfrage dann erneut!");
                    }

                    // Letzte Nachricht aus der History löschen, damit es bei erneuter Frage nicht zu Fehlerkaskaden kommt
                    if (history.Count > 0 && history.Last().Role == "user") {
                        history.RemoveAt(history.Count - 1);
                    }
                }
            }
            finally {
                Console.CancelKeyPress -= turnCancelHandler;
            }
        }

        WriteLine("\n[INFO] Chat beendet. Räume GCS Bucket komplett auf...");

        // ALWAYS clean up the bucket at the end of the session to save costs.
        await ForcePurgeGcsBucketAsync();
    }

    private static string PromptWithCommands(string promptMessage) {
        while (true) {
            Write(promptMessage);
            string? input = ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input)) continue;

            string normalizedInput = input.TrimStart('/');
            if (normalizedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) {
                return "__EXIT__";
            }

            // Placeholder for future setup-time commands in Vertex mode
            // if (input.StartsWith("some-vertex-command")) { ...; continue; }

            return input; // Return the non-command input
        }
    }

    private static void ShowCommands() {
        WriteLine("\n📋 Befehle:");
        WriteLine("  📜 help / commands         -> Zeigt diese Befehlsübersicht erneut an");
        WriteLine("  🚪 exit / quit             -> Beendet den Chat");
        WriteLine("  🧹 clear / reset           -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
        WriteLine("  📎 attach datei1 | Frage   -> Hängt Dateien an und stellt eine Frage dazu.");
        WriteLine("  🌡️  set temp [wert]         -> Ändert die Temperatur dynamisch");
        WriteLine("  🔢 set tokens [wert]       -> Ändert das MaxOutputTokens-Limit dynamisch");
        WriteLine("  🧠 set thinking-budget [w] -> Setzt das Thinking Budget für Gemini 2.5 (z.B. 4096)");
        WriteLine("  🧠 set thinking-level [l]  -> Setzt das Thinking Level für Gemini 3.x (z.B. HIGH)");
        WriteLine("  🔍 set grounding [on/off]  -> Aktiviert/Deaktiviert Google Search Grounding (Websuche)");
        WriteLine("  🤖 set model [name/index]  -> Ändert das aktive Modell mitten im Chat");
    }

    /// <summary>
    /// [AI Context] Intercepts and executes local REPL commands (e.g., /clear, /set temp) to avoid sending them as prompts to the AI.
    /// [Human] Fängt spezielle Befehle ab, bevor sie an die KI geschickt werden (z.B. zum Löschen der Historie).
    /// </summary>
    private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText, CancellationToken cancellationToken) {
        string normalizedInput = input.TrimStart('/');

        if (normalizedInput.Equals("help", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("commands", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("show commands", StringComparison.OrdinalIgnoreCase)) {
            ShowCommands();
            return true;
        }

        if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("reset", StringComparison.OrdinalIgnoreCase)) {
            history.Clear();
            history.AddRange(initialHistory);
            WriteLine("\n[INFO] Gedächtnis gelöscht! Vertex Modell startet frisch.");
            return true;
        }

        if (normalizedInput.StartsWith("set temp ", StringComparison.OrdinalIgnoreCase)) {
            string tempValueStr = normalizedInput[9..].Trim();
            if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f) {
                AIParams.Temperature = newTemp;
                WriteLine($"[INFO] Temperatur auf {AIParams.Temperature:F1} gesetzt.");
            }
            return true;
        }

        if (normalizedInput.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase)) {
            string tokenValueStr = normalizedInput[11..].Trim();
            if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1) {
                AIParams.MaxOutputTokens = newTokens;
                WriteLine($"[INFO] MaxOutputTokens auf {AIParams.MaxOutputTokens} gesetzt.");
            }
            return true;
        }

        if (normalizedInput.StartsWith("set thinking-budget ", StringComparison.OrdinalIgnoreCase)) {
            string budgetValueStr = normalizedInput[18..].Trim();
            if (int.TryParse(budgetValueStr, out int newBudget) && newBudget >= 0) {
                AIParams.ThinkingBudget = newBudget;
                WriteLine($"[INFO] ThinkingBudget auf {AIParams.ThinkingBudget} gesetzt (relevant für Gemini 2.5 Modelle).");
            }
            else {
                WriteLine($"[Fehler] Ungültiger Wert für ThinkingBudget '{budgetValueStr}'. Bitte eine positive ganze Zahl angeben.");
            }
            return true;
        }

        if (normalizedInput.StartsWith("set thinking-level ", StringComparison.OrdinalIgnoreCase)) {
            string levelValueStr = normalizedInput[17..].Trim().ToUpper();
            var validLevels = new[] { "MINIMAL", "LOW", "MEDIUM", "HIGH" };
            if (validLevels.Contains(levelValueStr)) {
                AIParams.ThinkingLevel = levelValueStr;
                WriteLine($"[INFO] ThinkingLevel auf '{AIParams.ThinkingLevel}' gesetzt (relevant für Gemini 3.x Modelle).");
            }
            else {
                WriteLine($"[Fehler] Ungültiger Wert für ThinkingLevel '{levelValueStr}'. Gültige Werte sind: MINIMAL, LOW, MEDIUM, HIGH.");
            }
            return true;
        }

        if (normalizedInput.StartsWith("set grounding ", StringComparison.OrdinalIgnoreCase)) {
            string val = normalizedInput[14..].Trim().ToLowerInvariant();
            if (val == "on" || val == "true" || val == "ja" || val == "yes" || val == "1") {
                AIParams.UseGoogleSearch = true;
                WriteLine("[INFO] Google Search Grounding für die nächste(n) Antwort(en) AKTIVIERT.");
            }
            else if (val == "off" || val == "false" || val == "nein" || val == "no" || val == "0") {
                AIParams.UseGoogleSearch = false;
                WriteLine("[INFO] Google Search Grounding für die nächste(n) Antwort(en) DEAKTIVIERT.");
            }
            else {
                WriteLine("[Fehler] Ungültiger Wert für grounding. Bitte 'on' oder 'off' ausgeben.");
            }
            return true;
        }

        if (normalizedInput.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) {
            string arg = normalizedInput.Length > 9 ? normalizedInput[9..].Trim() : "";
            string newModel = "";
            if (string.IsNullOrEmpty(arg)) {
                WriteLine("\nVerfügbare Modelle:");
                for (int i = 0; i < AvailableModels.Length; i++) {
                    WriteLine($" {i + 1}) {AvailableModels[i]}");
                }
                Write($"Bitte Modell auswählen (1-{AvailableModels.Length}): ");
                string? choice = ReadLine()?.Trim();
                if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= AvailableModels.Length) {
                    newModel = AvailableModels[idx - 1];
                }
                else if (!string.IsNullOrEmpty(choice)) {
                    newModel = choice;
                }
            }
            else {
                if (int.TryParse(arg, out int idx) && idx >= 1 && idx <= AvailableModels.Length) {
                    newModel = AvailableModels[idx - 1];
                }
                else {
                    newModel = arg;
                }
            }

            if (!string.IsNullOrEmpty(newModel)) {
                _activeModel = newModel;
                WriteLine($"[INFO] Aktives Modell für die nächste(n) Antwort(en) auf '{_activeModel}' geändert.");

                Write("Möchten Sie diese Änderung permanent in der Konfiguration speichern? (j/n, Standard: j): ");
                string? saveChoice = ReadLine()?.Trim().ToLowerInvariant();
                if (saveChoice != "n" && saveChoice != "nein" && saveChoice != "no") {
                    int idx = Array.IndexOf(AvailableModels, _activeModel);
                    if (idx >= 0) _config.CurrentModelIndex = idx;
                    _config.CurrentModel = _activeModel;
                    ConfigLoader<DirectAiChatSessionVertexConfig>.Save(_config);
                    WriteLine("  💾 [INFO] Das neue Modell wurde permanent in der Konfiguration gespeichert.");
                }
                else {
                    WriteLine("  [INFO] Die Änderung ist nur vorübergehend.");
                }
            }
            return true;
        }

        if (normalizedInput.StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) {
            var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(normalizedInput, cancellationToken: cancellationToken);

            if (!success) return true;

            parts.AddRange(attachmentParts);
            updatePromptText(parsedPrompt);
            return true;
        }

        return false;
    }

    /// <summary>
    /// [AI Context] Streams the response from Vertex AI back to the console and logs the output tokens.
    /// [Human] Holt sich die Antwort Stück für Stück von der Vertex API und schreibt sie flüssig in die Konsole.
    /// </summary>
    private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText, string userName) {
        Write($"\n[Vertex] {selectedModel} (Drücke Strg+C zum Abbrechen): ");
        string fullResponse = "";

        int inputTokens = 0;
        int outputTokens = 0;
        int cachedTokens = 0;

        var config = new GenerateContentConfig {
            Temperature = AIParams.Temperature,
            TopP = AIParams.TopP,
            TopK = AIParams.TopK,
            MaxOutputTokens = AIParams.MaxOutputTokens
        };

        if (AIParams.UseGoogleSearch) {
            config.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        // [AI Context] Safely inject Thinking parameters ONLY for gemini-3-flash-preview.
        // gemini-3.5-flash will bypass thinking configuration to keep extraction fast and stable.
        if (ModelCapabilities.SupportsThinking(selectedModel)) {
            bool isGemini25 = selectedModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(AIParams.ThinkingLevel)) {
                config.ThinkingConfig = new ThinkingConfig { ThinkingLevel = AIParams.ThinkingLevel };
            }
            else if (AIParams.ThinkingBudget.HasValue) {
                int budget = AIParams.ThinkingBudget.Value;
                if (budget > 32768) budget = 32768;
                config.ThinkingConfig = new ThinkingConfig { ThinkingBudget = budget };
            }
        }

        var apiContents = history; // By default, use the original history

        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
            config.SystemInstruction = new Content { Role = "system", Parts = [new() { Text = _systemInstructionText }] };
            // Gemma models (pre-v4) don't support the 'system' role.
            // We prepend the instruction to the first user message instead.
            if (selectedModel.StartsWith("gemma", StringComparison.OrdinalIgnoreCase) && !selectedModel.Contains("gemma-4")) {
                bool isFirstTurn = !history.Any(c => c.Role == "model");
                if (isFirstTurn) {
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
                    config.SystemInstruction = null;
                }
            }
            else {
                config.SystemInstruction = new Content { Role = "system", Parts = [new() { Text = _systemInstructionText }] };
            }
        }

        bool exceptionCaught = false;
        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) {
            e.Cancel = true; // Verhindert das harte Beenden
            try { cts.Cancel(); } catch { }
        }
        Console.CancelKeyPress += cancelHandler;

        bool isGenerating = true;
        var inputInterceptorTask = Task.Run(async () => {
            while (isGenerating) {
                if (!ExtractionHelpers.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
                    while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                    WriteLine("\n[AI-Model] Still waiting for the acknowledgment / response. Please wait...");
                }
                await Task.Delay(100);
            }
        });

        GroundingMetadata? accumulatedGrounding = null;

        try {
            bool success = await ApiResilience.ExecuteStreamWithRetryAsync(
                streamFactory: () => _client.Models.GenerateContentStreamAsync(model: selectedModel, contents: apiContents, config: config),
                onChunkReceived: async (chunk) => {
                    string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Write(chunkText); // The variable chunkText is already updated from `chunk.Text ?? ...`, no change needed here.
                    fullResponse += chunkText;

                    var metadata = chunk.Candidates?[0]?.GroundingMetadata;
                    if (metadata != null) {
                        accumulatedGrounding = metadata;
                    }

                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) inputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) outputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) cachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                    await Task.CompletedTask;
                },
                cancellationToken: cts.Token,
                maxRetries: 5,
                retryContext: "Chat-Antwort"
            );
            if (!success) exceptionCaught = true;

            if (accumulatedGrounding != null) {
                WriteLine("\n\n🔍 [Google Search Grounding] Quellen:");
                if (accumulatedGrounding.WebSearchQueries != null && accumulatedGrounding.WebSearchQueries.Count > 0) {
                    WriteLine($"  Suchanfragen: {string.Join(", ", accumulatedGrounding.WebSearchQueries.Select(q => $"\"{q}\""))}");
                }
                if (accumulatedGrounding.GroundingChunks != null) {
                    int refIdx = 1;
                    foreach (var chunkRef in accumulatedGrounding.GroundingChunks) {
                        if (chunkRef.Web != null) {
                            WriteLine($"   [{refIdx}] {chunkRef.Web.Title} - {chunkRef.Web.Uri} ({chunkRef.Web.Domain})");
                            refIdx++;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled") || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
            exceptionCaught = true;
        }
        finally {
            isGenerating = false;
            await inputInterceptorTask;
            Console.CancelKeyPress -= cancelHandler;

            if (inputTokens > 0 || outputTokens > 0) {
                _sessionTotalInputTokens += inputTokens;
                _sessionTotalOutputTokens += outputTokens;
                _sessionTotalCachedTokens += cachedTokens;
                WriteLine($"\n[Request Tokens] Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {(Math.Max(0, inputTokens - cachedTokens)):N0} | Output: {outputTokens:N0} (inkl. Thinking Tokens)");
                WriteLine($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {(Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens)):N0} | Output: {_sessionTotalOutputTokens:N0}");
            }

            if (exceptionCaught || cts.IsCancellationRequested) {
                WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen.");
            }
            else {
                WriteLine();
            }
        }

        if (!string.IsNullOrWhiteSpace(fullResponse)) {
            history.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
            await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse, userName, inputTokens, outputTokens, cachedTokens);
        }
        else {
            history.RemoveAt(history.Count - 1);
        }
    }

    /// <summary>
    /// [AI Context] Scans history directories to automatically append context to the beginning of the chat session.
    /// [Human] Sucht nach alten Chat-Verläufen, um sie gleich beim Start als Erinnerung für das Modell hochzuladen.
    /// </summary>
    private string? GetInitialHistoryCommand(string selectedModel) {
        if (HistoryPreloadPaths == null || HistoryPreloadPaths.Length == 0) {
            return null;
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
            WriteLine($"\n[Setup-Warnung] Folgende History-Pfade wurden nicht gefunden:");
            foreach (var path in notFoundPaths) {
                WriteLine($"  - {path}");
            }
        }

        var distinctFiles = allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Verhindert, dass die System Instruction versehentlich als History geladen wird
        if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
            distinctFiles = [.. distinctFiles.Where(f => !string.Equals(f, Path.GetFullPath(SystemInstructionPath), StringComparison.OrdinalIgnoreCase))];
        }

        if (distinctFiles.Count == 0) return null;

        WriteLine($"\n[Setup] Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
        ExtractionHelpers.PrintFileTree(distinctFiles);

        string historyChoice = PromptWithCommands("Sollen diese Dateien als History geladen werden? (j/n): ");
        if (historyChoice == "__EXIT__") return historyChoice;

        bool loadHistory = historyChoice.Trim().Equals("j", StringComparison.CurrentCultureIgnoreCase);

        if (!loadHistory) return null;

        string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        return $"attach {fileList} | {string.Format(InitialHistoryPrompt, selectedModel)}";
    }

    /// <summary>
    /// [AI Context] Deep cleans the assigned Vertex AI Bucket. Crucial for managing storage costs and cleaning up crashed sessions.
    /// [Human] Löscht radikal alle Dateien aus dem Cloud Bucket. Das ist bei Vertex besonders wichtig, um horrende Speicherkosten zu vermeiden!
    /// </summary>
    private async Task ForcePurgeGcsBucketAsync() {
        if (string.IsNullOrWhiteSpace(GcsBucketName)) return;

        try {
            // StorageClient utilizes Application Default Credentials
            var storageClient = await StorageClient.CreateAsync();
            WriteLine($"  [GCS] Verifying Bucket '{GcsBucketName}' and purging ALL files...");

            var objects = storageClient.ListObjectsAsync(GcsBucketName);
            int count = 0;

            await foreach (var obj in objects) {
                await storageClient.DeleteObjectAsync(GcsBucketName, obj.Name);
                count++;
            }

            if (count > 0) {
                WriteLine($"  [GCS] Successfully deleted {count} file(s) to secure billing.");
            }
            else {
                WriteLine($"  [GCS] Bucket is already empty.");
            }
        }
        catch (Exception ex) {
            WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            WriteLine($"Originaler Fehlertext: {ex.Message}");

            WriteLine($"\n  --- GCS ERROR DUMP ---");
            WriteLine($"{ex}");
            WriteLine($"  ----------------------\n");

            if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.Sockets.SocketException ||
                ex.Message.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("host is known", StringComparison.OrdinalIgnoreCase)) {
                WriteLine($"  [GCS ERROR] Netzwerkfehler beim Zugriff auf '{GcsBucketName}'. Möglicherweise sind Sie nicht mit dem Internet verbunden! Originalfehler: {ex.Message}");
            }
            else if (ex.Message.Contains("billing account", StringComparison.OrdinalIgnoreCase)) {
                WriteLine($"  [GCS ERROR] Zugriff auf Bucket '{GcsBucketName}' verweigert. Dem Projekt fehlt ein aktives Rechnungskonto (Billing Account)! Originalfehler: {ex.Message}");
            }
            else {
                WriteLine($"  [GCS ERROR] Failed to access or purge bucket '{GcsBucketName}': {ex.Message}");
            }
        }
    }
}