using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Globalization;
using System.Threading.Tasks; // Removed DirectChatAiInteraction as SessionLogger is now in Infrastructure
using Infrastructure;
using Google.GenAI;
using Google.GenAI.Types;
using Config; // Added for LatexRefinementConfig
using DocumentUtilities; // Added for LatexTimestampHelper

namespace AutoExtraction;

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline. 
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos. 
/// Schau bitte auch das entsprechende .json-File an!
/// </summary>
public class AiStudioAutoExtractionSession {
    private Client _client;
    private readonly AiStudioAutoExtractionConfig _config;
    private readonly AttachmentHandler _attachmentHandler;
    private readonly SessionLogger _sessionLogger;
    private readonly LatexRefinementSessionConfig _latexRefinementConfig;
    private double _speed = 1.0;
    private string _systemInstructionText = "";
    // [AI Context] Cached payloads to avoid redundant uploads and API calls across multiple video chunks.
    private List<Part> _historyParts = new List<Part>();
    // [AI Context] Stores the acknowledged history prompt and the model's confirmation, statically prepended to all subsequent API calls.
    private List<Content> _sessionPreamble = new List<Content>();
    private bool _historyWasLoaded = false;
    // [AI Context] Stateful history exclusively for the REPL loop's debug chat.
    private List<Content> _debugChatHistory = new List<Content>();
    private int _sessionTotalInputTokens = 0;
    private int _sessionTotalOutputTokens = 0;

    public AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) {
        _client = client;
        _config = config;
        _attachmentHandler = attachmentHandler;
        _sessionLogger = sessionLogger;
        _latexRefinementConfig = latexRefinementConfig; // Initialized
    }

    public async Task StartAsync() {
        // [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
        Console.WriteLine($"\n[AutoExtraction] Starte AI Studio Extraction Session...");
        Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
        Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");
        if (_config.ActiveApiProfile == 0) {
            Console.WriteLine($"[AutoExtraction] API-Key: Dedizierter Key für automatisierte Extraktion (API_KEY-automated-content-extraction)");
        }
        else {
            Console.WriteLine($"[AutoExtraction] API-Key: Profil {_config.ActiveApiProfile} (API_KEY-ai-studio-test-project-{_config.ActiveApiProfile})");
        }

        if (!Directory.Exists(_config.SourceFolder)) {
            Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
            return;
        }

        // If no specific target folder is provided in config, create one inside the source folder.
        if (string.IsNullOrWhiteSpace(_config.TargetFolder)) {
            _config.TargetFolder = Path.Combine(_config.SourceFolder, "extracted_output");
        }

        if (!Directory.Exists(_config.TargetFolder)) {
            Directory.CreateDirectory(_config.TargetFolder);
        }

        string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
        string filenamePatternRegex = @"^(\d{2,4}-)?\d{2}-\d{2}-(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)?\.[a-z0-9]+$";
        foreach (var f in filesToProcess) {
            string fileName = Path.GetFileName(f).ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, filenamePatternRegex)) {
                Console.WriteLine($"\n[WARNUNG] Video entspricht nicht dem Datums-Namensschema: {Path.GetFileName(f)}");
                Console.WriteLine("Erwartetes Format z.B.: 04-12-monday.mp4 oder 06-04-12-montag.mp4 oder 2006-04-12-montag.mp4");
            }
        }

        await ReplLoopAsync();
    }

    private async Task SetupContextAndProcessAsync(string[] files) {
        if (files == null || files.Length == 0) {
            Console.WriteLine("Keine Dateien ausgewählt.");
            return;
        }

        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (_config.SystemInstructionPaths != null && _config.SystemInstructionPaths.Any()) {
                Console.WriteLine("\nFolgende System Instruction-Dateien sind konfiguriert:");

                // Resolve all files from configured paths, handling directories
                var resolvedInstructionFiles = ExtractionHelpers.ResolveHistoryFiles(_config.SystemInstructionPaths);

                if (resolvedInstructionFiles.Any()) {
                    foreach (var file in resolvedInstructionFiles) {
                        Console.WriteLine($"  - {file}");
                    }
                    Console.Write("System Instructions laden? (j/n): ");
                    if (Console.ReadLine()?.Trim().ToLower() == "j") {
                        var instructionBuilder = new System.Text.StringBuilder();
                        foreach (var filePath in resolvedInstructionFiles) {
                            instructionBuilder.AppendLine(await System.IO.File.ReadAllTextAsync(filePath));
                            Console.WriteLine($"  [INFO] System Instruction geladen: {Path.GetFileName(filePath)}");
                        }
                        _systemInstructionText = instructionBuilder.ToString();
                    }
                }
                else {
                    Console.WriteLine("  [WARNUNG] Keine System Instruction-Dateien gefunden oder konfiguriert.");
                }
            }
        }

        if (!_historyWasLoaded) {
            var distinctFiles = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (distinctFiles.Any()) {
                Console.WriteLine("\nFolgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
                foreach (var file in distinctFiles) {
                    Console.WriteLine($"  - {file}");
                }
                Console.Write("Sollen diese Dateien als History geladen und für die Session hochgeladen werden? (j/n): ");
                if (Console.ReadLine()?.Trim().ToLower() == "j") {
                    Console.WriteLine("\n  [INFO] Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
                    string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
                    var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}");
                    if (success && attachmentParts.Any()) {
                        _historyParts.AddRange(attachmentParts);
                        _historyWasLoaded = true;
                        Console.WriteLine("  [INFO] History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
                        if (!await AcknowledgeHistoryAsync(fileList)) return;
                    }
                    else {
                        Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
                    }
                }
            }
        }

        _sessionLogger.SetSessionMetadata(!string.IsNullOrEmpty(_systemInstructionText), _historyWasLoaded);
        _sessionLogger.InitializeSession();
        await _sessionLogger.LogSessionSetupAsync();

        await ProcessFilesAsync(files);
    }

    /// <summary>
    /// [AI Context] Interactive control loop for the AutoExtraction mode. 
    /// Allows developers to dynamically adjust FFmpeg speeds, trigger specific files, or chat directly with the configured model for prompt debugging before launching a massive batch job.
    /// [Human] Eine interaktive Konsole, um vor dem großen Batch-Start Parameter (wie Video-Speed) zu testen oder den Prompt zu debuggen.
    /// </summary>
    private async Task ReplLoopAsync() {
        Console.WriteLine("\nBefehle:");
        Console.WriteLine("  1) Befehle anzeigen");
        Console.WriteLine("  2) Video-Geschwindigkeit setzen (z.B. 'set speed 1.5' oder nur '2'). Standard: 1.2");
        Console.WriteLine("  3) Einzelnes Video interaktiv auswählen und konvertieren");
        Console.WriteLine("  4) Alle Videos im Quellordner konvertieren");
        Console.WriteLine("  5) Beenden (exit/quit)");
        Console.WriteLine("  6) API-Key Profil wechseln (z.B. 'change-key 2', 0 für dediziert) (aktuell: " + (_config.ActiveApiProfile == 0 ? "dediziert" : $"Profil {_config.ActiveApiProfile}") + ")");
        Console.WriteLine("  7) Modell auswählen (aktuell: " + _config.Model + ")");
        Console.WriteLine("  (Alles andere wird als normaler Chat-Prompt zum Debuggen an Gemini gesendet)");
        Console.WriteLine("\nHinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");

        while (true) {
            if (!Console.IsInputRedirected) {
                while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            }
            Console.Write("\nAutoExt> ");
            string input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;

            string normalizedInput = input.TrimStart('/');
            if (normalizedInput == "5" || normalizedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

            if (normalizedInput == "1" || normalizedInput.Equals("show commands", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("\nBefehle:");
                Console.WriteLine("  1) Befehle anzeigen");
                Console.WriteLine("  2) Video-Geschwindigkeit setzen (z.B. 'set speed 1.5' oder nur '2'). Standard: 1.2");
                Console.WriteLine("  3) Einzelnes Video interaktiv auswählen und konvertieren");
                Console.WriteLine("  4) Alle Videos im Quellordner konvertieren");
                Console.WriteLine("  5) Beenden (exit/quit)");
                Console.WriteLine("  6) API-Key Profil wechseln (z.B. 'change-key 2', 0 für dediziert) (aktuell: " + (_config.ActiveApiProfile == 0 ? "dediziert" : $"Profil {_config.ActiveApiProfile}") + ")");
                Console.WriteLine("  7) Modell auswählen (aktuell: " + _config.Model + ")");
                Console.WriteLine("  (Alles andere wird als normaler Chat-Prompt zum Debuggen an Gemini gesendet)");
                Console.WriteLine("\nHinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");
            }
            else if (normalizedInput == "2" || normalizedInput.StartsWith("2 ") || normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) {
                string val = "";
                if (normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) val = normalizedInput.Substring(9).Trim();
                else if (normalizedInput.StartsWith("2 ")) val = normalizedInput.Substring(2).Trim();
                else if (normalizedInput == "2") {
                    Console.Write("Neuer Speed-Wert (z.B. 1.5): ");
                    val = Console.ReadLine()?.Trim() ?? "";
                }

                if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s)) {
                    _speed = s;
                    Console.WriteLine($"Speed gesetzt auf {_speed}x");
                }
                else {
                    Console.WriteLine("Ungültiger Wert für speed.");
                }
            }
            else if (normalizedInput == "3" || normalizedInput.Equals("convert chosen video", StringComparison.OrdinalIgnoreCase)) {
                var files = FfmpegUtilities.ConsoleUiHelper.SelectSingleFile(_config.SourceFolder);
                if (files.Length > 0) {
                    await SetupContextAndProcessAsync(files);
                }
            }
            else if (normalizedInput == "4" || normalizedInput.Equals("convert all videos", StringComparison.OrdinalIgnoreCase)) {
                var files = Directory.GetFiles(_config.SourceFolder, "*.mp4");
                await SetupContextAndProcessAsync(files);
            }
            else if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                _debugChatHistory.Clear();
                Console.WriteLine("  [INFO] Debug-Chat Verlauf gelöscht.");
            }
            else if (normalizedInput == "6" || normalizedInput.StartsWith("6 ") || normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase) || normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) {
                string val = "";
                if (normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase)) {
                    val = normalizedInput.Substring("change-key".Length).Trim();
                }
                else if (normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) {
                    val = normalizedInput.Substring("change key".Length).Trim();
                }
                else if (normalizedInput.StartsWith("6 ")) {
                    val = normalizedInput.Substring(2).Trim();
                }

                if (string.IsNullOrEmpty(val)) {
                    Console.Write("Neues API-Key Profil (0-3): ");
                    val = Console.ReadLine()?.Trim() ?? "";
                }

                if (int.TryParse(val, out int newProfile) && newProfile >= 0 && newProfile <= 3) {
                    string? newApiKey;
                    if (newProfile == 0) {
                        newApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-automated-content-extraction");
                    }
                    else {
                        newApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKey(newProfile);
                    }

                    if (!string.IsNullOrEmpty(newApiKey)) {
                        _client = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
                        _attachmentHandler.UpdateClient(_client);
                        _config.ActiveApiProfile = newProfile;
                        Console.WriteLine($"  [INFO] API-Key erfolgreich auf Profil {newProfile} gewechselt!");
                    }
                }
                else {
                    Console.WriteLine("  [Fehler] Bitte eine gültige Profilnummer (0, 1, 2 oder 3) angeben.");
                }
            }
            else if (normalizedInput == "7" || normalizedInput.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) {
                _config.Model = SelectModel();
                Console.WriteLine($"  [INFO] Modell für diese Session auf '{_config.Model}' gesetzt.");
            }
            else {
                await DebugChatAsync(input); // Chat erhält den originalen Input
            }
        }
    }

    private string SelectModel() {
        Console.WriteLine($"\n=== Model Selection (AI Studio) ===");
        Console.WriteLine("Wähle ein Modell:");
        Console.WriteLine(" 1) gemini-3.1-flash-lite-preview");
        Console.WriteLine(" 2) gemini-3-flash-preview");
        Console.WriteLine(" 3) gemini-3.1-pro-preview");
        Console.WriteLine(" 4) gemini-2.5-flash");
        Console.WriteLine(" 5) gemini-2.5-flash-lite");
        Console.WriteLine(" 6) gemini-2.5-pro");
        Console.WriteLine(" 7) gemma-3-27b-it");
        Console.WriteLine(" 8) gemini-1.5-flash");
        Console.WriteLine(" 9) gemini-1.5-pro");
        Console.WriteLine("10) gemini-robotics-er-1.6-preview");
        Console.WriteLine("11) gemini-3.5-flash"); // Added Gemini 3.5 Flash
        Console.Write($"Auswahl (1-11) [Aktuell: {_config.Model}]: ");

        string choice = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(choice)) return _config.Model;

        return choice switch {
            "1" => "gemini-3.1-flash-lite-preview",
            "2" => "gemini-3-flash-preview",
            "3" => "gemini-3.1-pro-preview",
            "4" => "gemini-2.5-flash",
            "5" => "gemini-2.5-flash-lite",
            "6" => "gemini-2.5-pro",
            "7" => "gemma-3-27b-it",
            "8" => "gemini-1.5-flash",
            "9" => "gemini-1.5-pro",
            "10" => "gemini-robotics-er-1.6-preview",
            "11" => "gemini-3.5-flash", // Added Gemini 3.5 Flash
            _ => choice.Contains("-") ? choice : _config.Model
        };
    }

    /// <summary>
    /// [AI Context] A dedicated REPL chat for testing prompts against the model without initializing the full FFmpeg pipeline.
    /// Contains identical retry/backoff logic to the main extraction loop to accurately simulate API conditions.
    /// [Human] Der Debug-Chat. Hier kannst du mit der KI schreiben und testen, wie sie auf Prompts reagiert, bevor du hunderte Videos durchjagst.
    /// </summary>
    private async Task DebugChatAsync(string input) {
        _debugChatHistory.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = input } } });

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        if (_config.Model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) || _config.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
            if (_config.ThinkingBudget.HasValue || !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (!string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig.ThinkingLevel = _config.ThinkingLevel;
                } else if (_config.ThinkingBudget.HasValue) {
                    requestConfig.ThinkingConfig.ThinkingBudget = _config.ThinkingBudget;
                }
            }
        }

        Console.Write($"\n[Debug Chat] {_config.Model} (Strg+C zum Abbrechen): ");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
        Console.CancelKeyPress += cancelHandler;

        int maxRetries = 8;
        int backoff = 45;
        string fullResponse = "";
        bool exceptionCaught = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            fullResponse = "";
            bool isGenerating = true;
            var inputInterceptorTask = Task.Run(async () => {
                while (isGenerating) {
                    if (!ExtractionHelpers.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
                        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                        Console.WriteLine("\n[AI-Model] Still waiting for the acknowledgment / response. Please wait...");
                    }
                    await Task.Delay(100);
                }
            });

            try {
                if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");
                int requestInputTokens = 0;
                int requestOutputTokens = 0;

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, _debugChatHistory, requestConfig);
                await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
                    if (cts.IsCancellationRequested) break;
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Console.Write(txt);
                    fullResponse += txt;
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                    }
                }

                _sessionTotalInputTokens += requestInputTokens;
                _sessionTotalOutputTokens += requestOutputTokens;
                Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens} (inkl. Thinking Tokens)");
                Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

                Console.WriteLine();
                isGenerating = false;
                await inputInterceptorTask;
                break; // Erfolg
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
                isGenerating = false;
                await inputInterceptorTask;
                exceptionCaught = true;
                break;
            }
            catch (Exception ex) {
                isGenerating = false;
                await inputInterceptorTask;

                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

                bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("502") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
                if (isOverloaded && attempt < maxRetries) {
                    // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
                    // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
                    // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
                    // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
                    int waitTime;
                    string contextMsg = " [Debug Chat]";
                    // [Human] Sonderbehandlung für "high demand"-Fehler: Feste Wartezeit von 3 Minuten.
                    if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
                        waitTime = 180; // 3 Minuten
                        Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        backoff = waitTime;
                    }
                    else if (attempt == 1) {
                        var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
                        if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                            waitTime = serverSuggestedDelay + 20;
                            Console.WriteLine($"\n[Rate Limit]{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        }
                        else {
                            waitTime = backoff;
                            Console.WriteLine($"\n[Rate Limit / Überlastung]{contextMsg} Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        }
                        backoff = waitTime;
                    }
                    else {
                        backoff += 30;
                        waitTime = backoff;
                        Console.WriteLine($"\n[Rate Limit]{contextMsg} Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                    }
                    if (!await ExtractionHelpers.SmartDelayAsync(waitTime)) { exceptionCaught = true; break; }
                }
                else {
                    Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
                    // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
                    _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
                    break;
                }
            }
        }

        Console.CancelKeyPress -= cancelHandler;

        if (exceptionCaught || cts.IsCancellationRequested) {
            Console.WriteLine("\n\n[INFO] Debug-Chat durch Benutzer abgebrochen.");
        }

        if (!string.IsNullOrWhiteSpace(fullResponse)) {
            _debugChatHistory.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
        }
        else if (_debugChatHistory.Any() && _debugChatHistory.Last().Role == "user") {
            // Falls abgebrochen wurde, bevor die KI etwas gesagt hat, die User-Nachricht entfernen.
            _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
        }
    }

    /// <summary>
    /// [AI Context] Forces a real API call to explicitly acknowledge the history payload. 
    /// This guarantees the model context is correctly primed before batch processing starts and provides immediate visual feedback.
    /// [Human] Sendet die geladenen History-Dateien an Gemini und wartet auf eine Bestätigung. So stellen wir sicher, dass die KI den Kontext gefressen hat, bevor es losgeht.
    /// </summary>
    private async Task<bool> AcknowledgeHistoryAsync(string loadedFiles = "") {
        var historyPromptParts = new List<Part>(_historyParts);
        historyPromptParts.Add(new Part { Text = $"Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {_config.Model}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards." });
        var userContent = new Content { Role = "user", Parts = historyPromptParts };

        _sessionPreamble.Add(userContent);

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature, // Use config value, or hardcode 0.0 for initial acknowledgment? Let's use config.
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens // Use config value, or hardcode a smaller value for acknowledgment? Let's use config.
        };
        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
        }
        if (_config.Model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) || _config.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
            if (_config.ThinkingBudget.HasValue || !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (!string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig.ThinkingLevel = _config.ThinkingLevel;
                } else if (_config.ThinkingBudget.HasValue) {
                    requestConfig.ThinkingConfig.ThinkingBudget = _config.ThinkingBudget;
                }
            }
        }

        Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.Model}: ");
        int backoff = 45;
        int maxRetries = 10;
        bool success = false;
        string fullResponse = "";
        int finalInputTokens = 0;
        int finalOutputTokens = 0;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            fullResponse = "";
            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
            Console.CancelKeyPress += cancelHandler;

            try {
                if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");

                int requestInputTokens = 0;
                int requestOutputTokens = 0;

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, _sessionPreamble, requestConfig);
                await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
                    if (cts.IsCancellationRequested) break;
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Console.Write(txt);
                    fullResponse += txt;
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                    }
                }

                _sessionTotalInputTokens += requestInputTokens;
                _sessionTotalOutputTokens += requestOutputTokens;
                finalInputTokens = requestInputTokens;
                finalOutputTokens = requestOutputTokens;
                Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens} (inkl. Thinking Tokens)");
                Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

                Console.WriteLine();
                success = true;
                break;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("\n[INFO] Bestätigung durch Benutzer abgebrochen.");
                break;
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("502") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
                if (isOverloaded && attempt < maxRetries) {
                    // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
                    // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
                    // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
                    // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
                    int waitTime;
                    string contextMsg = " [History Bestätigung]";
                    // [Human] Sonderbehandlung für "high demand"-Fehler: Feste Wartezeit von 3 Minuten.
                    if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
                        waitTime = 180; // 3 Minuten
                        Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        backoff = waitTime;
                    }
                    else if (attempt == 1) {
                        var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
                        if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                            waitTime = serverSuggestedDelay + 20;
                            Console.WriteLine($"\n[Rate Limit]{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        }
                        else {
                            waitTime = backoff;
                            Console.WriteLine($"\n[Rate Limit / Überlastung]{contextMsg} Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        }
                        backoff = waitTime;
                    }
                    else {
                        backoff += 30;
                        waitTime = backoff;
                        Console.WriteLine($"\n[Rate Limit]{contextMsg} Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                    }
                    if (!await ExtractionHelpers.SmartDelayAsync(waitTime)) { break; }
                }
                else {
                    Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
                    break;
                }
            }
            finally {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        if (success && !string.IsNullOrWhiteSpace(fullResponse)) {
            _sessionPreamble.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
            string logMsg = $"[History Acknowledgment] Angehängte Dateien: {loadedFiles}\n\nPrompt:\n{historyPromptParts.Last().Text}";
            await _sessionLogger.LogChatAsync(logMsg, logMsg, _config.Model, fullResponse, "AutoExtractionSetup", finalInputTokens, finalOutputTokens);
            return true;
        }
        else {
            Console.WriteLine("\n[FEHLER] Konnte Bestätigung für History nicht erhalten. Breche Extraktion ab.");
            _sessionPreamble.Clear();
            _historyWasLoaded = false;
            return false;
        }
    }

    /// <summary>
    /// [AI Context] Executes the batch processing workflow.
    /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
    /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
    /// </summary>
    private async Task ProcessFilesAsync(string[] files) {
        // Chronologisch aufsteigend sortieren anhand des Dateinamens
        files = files.OrderBy(f => VideoDateParser.Parse(f).Date).ToArray();

        var toolkit = new FfmpegUtilities.FfmpegToolkit();

        // [AI Context] We use a bounded channel (capacity 1) to synchronize the FFmpeg Producer task and the Gemini Consumer task.
        // This allows FFmpeg to prepare the *next* video while Gemini is waiting for the API to process the *current* video, maximizing throughput.
        // [Human] Wir nutzen einen 'Kanal' (Channel), um FFmpeg (Videobearbeitung) und Gemini (KI-Analyse) parallel laufen zu lassen.
        // Während die KI das erste Video analysiert, schneidet FFmpeg im Hintergrund schon das zweite. Das spart enorm Zeit!
        var channel = Channel.CreateBounded<(string originalFile, string fileSpecificOutputFolder, string tmpFolderForFile, List<(string FilePath, double StartTime)> parts, bool isCached, double fullOriginalVideoDuration)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
        var producerTask = Task.Run(async () => {
            foreach (var file in files) {
                string baseName = Path.GetFileNameWithoutExtension(file);
                baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"-speed-[\d\.]+-compressed$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"-compressed$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                // Create a file-specific output folder within the main target folder
                string fileSpecificOutputFolder = Path.Combine(_config.TargetFolder, baseName);
                if (!Directory.Exists(fileSpecificOutputFolder)) {
                    Directory.CreateDirectory(fileSpecificOutputFolder);
                }
                // Create a file-specific temporary folder inside the file-specific output folder
                string tmpFolderForFile = Path.Combine(fileSpecificOutputFolder, "tmp");
                if (!Directory.Exists(tmpFolderForFile)) {
                    Directory.CreateDirectory(tmpFolderForFile);
                }

                // Audio extraction was moved to the Consumer loop to run in parallel with API calls

                // Removed dateStr from filename pattern for caching to work across days for 2-hour window
                var cachedParts = Directory.GetFiles(tmpFolderForFile, $"{baseName}-part*.mp4").ToList();

                double fullOriginalVideoDuration = await toolkit.GetVideoDurationAsync(file); // Get original video duration
                TimeSpan cacheDuration = TimeSpan.FromHours(48); // Set cache duration to 48 hours (2 days)
                bool useCache = false;

                if (cachedParts.Count > 0) {
                    var fileInfo = new FileInfo(cachedParts[0]);
                    if ((DateTime.Now - fileInfo.LastWriteTime) <= cacheDuration) {
                        // [AI Context] Defend against incomplete caches from interrupted FFmpeg runs.
                        // We expect exactly 3 parts. We also check if the files are actually valid (not 0 bytes)
                        // [Human] Wenn ein alter Lauf abgebrochen ist, liegen vielleicht nur 1-2 Teile im Cache, oder sie sind 0 Bytes groß. Das wird hier verhindert!
                        bool allFilesValid = true;
                        foreach (var cp in cachedParts) {
                            if (new FileInfo(cp).Length < 1024) { // less than 1KB is definitely invalid for a video
                                allFilesValid = false;
                                break;
                            }
                        }

                        if (cachedParts.Count >= _config.NumberOfParts && allFilesValid) {
                            useCache = true;
                        }
                        else {
                            Console.WriteLine($"\n  [Cache] Ignoriere unvollständigen oder defekten Cache für '{Path.GetFileName(file)}' ({cachedParts.Count} Teil(e), valid: {allFilesValid}). FFmpeg wird neu gestartet...");
                            foreach (var f in cachedParts) { try { System.IO.File.Delete(f); } catch { } }
                        }
                    }
                }

                if (useCache) {
                    Console.WriteLine($"\n[Cache] FFmpeg übersprungen für '{file}'. Verwende folgende gecachte Dateien (jünger als 48h):");
                    cachedParts.Sort();

                    // Determine the duration of the video that was actually split (either pre-compressed input or processed output)
                    double speedVideoDuration;
                    bool wasInputFilePreCompressedWhenCached = System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file).ToLowerInvariant(), @"(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (wasInputFilePreCompressedWhenCached) {
                        // If the input file was pre-compressed, its duration is what was effectively "processed" and split.
                        speedVideoDuration = await toolkit.GetVideoDurationAsync(file);
                    }
                    else {
                        // Otherwise, it was the output of ProcessGeneralVideoAsync that was cached.
                        string expectedProcessedVideoPath = Path.Combine(tmpFolderForFile, $"{baseName}-speed-{_speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}-compressed.mp4");
                        speedVideoDuration = await toolkit.GetVideoDurationAsync(expectedProcessedVideoPath);
                    }
                    double segmentLengthForCached = (speedVideoDuration > 0) ? (speedVideoDuration + (_config.NumberOfParts - 1) * _config.OverlapSeconds) / _config.NumberOfParts : 0; 
                    var cachedPartsWithTimes = new List<(string FilePath, double StartTime)>();
                    for (int i = 0; i < cachedParts.Count; i++) {
                        double startTime = (segmentLengthForCached > 0 && i > 0) ? i * (segmentLengthForCached - _config.OverlapSeconds) : 0;
                        Console.WriteLine($"  - {cachedParts[i]} (Est. Start: {startTime.ToString("F2", CultureInfo.InvariantCulture)}s)");
                        cachedPartsWithTimes.Add((cachedParts[i], startTime));
                    }

                    await channel.Writer.WriteAsync((file, fileSpecificOutputFolder, tmpFolderForFile, cachedPartsWithTimes, true, fullOriginalVideoDuration));
                    continue;
                }

                // Determine if the file is already in a "compressed" format
                bool isPreCompressed = System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file).ToLowerInvariant(), @"(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                string? videoToSplit;
                if (isPreCompressed) {
                    Console.WriteLine($"\n[FFmpeg Producer] {Path.GetFileName(file)} ist bereits als komprimiert markiert. Überspringe Vorverarbeitung, starte direkt Splitting...");
                    videoToSplit = file; // Use the original file directly for splitting
                }
                else {
                    Console.WriteLine($"\n[FFmpeg Producer] Starte Vorverarbeitung für {Path.GetFileName(file)} ({_speed}x Speed, 1 FPS, Mono)...");
                    videoToSplit = await toolkit.ProcessGeneralVideoAsync(file, tmpFolderForFile, speedMultiplier: _speed, fps: 1, downmixToMono: true, scaleTo720p: false, overwrite: true);
                    if (videoToSplit == null) {
                        Console.WriteLine($"  [FFmpeg Producer] Vorverarbeitung für {Path.GetFileName(file)} fehlgeschlagen. Überspringe Datei.");
                        continue;
                    }
                }

                Console.WriteLine($"\n[FFmpeg Producer] Starte Splitting für {Path.GetFileName(videoToSplit)} in {_config.NumberOfParts} Teile ({_config.OverlapSeconds}s Overlap)...");
                var rawPartsWithTimes = await toolkit.ProcessSplitVideoAsync(videoToSplit, tmpFolderForFile, parts: _config.NumberOfParts, overlapSeconds: _config.OverlapSeconds, downmixToMono: false, streamCopy: true, overwrite: true);

                if (rawPartsWithTimes.Any()) {
                    List<(string FilePath, double StartTime)> safePartsWithTimes = new List<(string, double)>();
                    for (int i = 0; i < rawPartsWithTimes.Count; i++) {
                        string safePartPath = Path.Combine(tmpFolderForFile, $"{baseName}-part{i + 1}.mp4");
                        
                        if (!string.Equals(rawPartsWithTimes[i].FilePath, safePartPath, StringComparison.OrdinalIgnoreCase)) {
                            if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
                            System.IO.File.Move(rawPartsWithTimes[i].FilePath, safePartPath);
                        }
                        
                        safePartsWithTimes.Add((safePartPath, rawPartsWithTimes[i].StartTime));
                    }
                    await channel.Writer.WriteAsync((file, fileSpecificOutputFolder, tmpFolderForFile, safePartsWithTimes, false, fullOriginalVideoDuration));
                }
            }
            channel.Writer.Complete(); // Signalisiert dem Fließband: "Feierabend, es kommen keine Videos mehr."
        });

        // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
        // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
        bool hasErrors = false;

        await foreach (var (file, fileSpecificOutputFolder, tmpFolderForFile, partsWithTimes, isCached, fullOriginalVideoDuration) in channel.Reader.ReadAllAsync()) {
            // Ensure the file-specific output folder exists before starting processing
            if (!Directory.Exists(fileSpecificOutputFolder)) {
                Directory.CreateDirectory(fileSpecificOutputFolder);
            }


            Console.WriteLine($"\n[Gemini Consumer] === Starte API-Extraktion für {Path.GetFileName(file)} ===");
            List<string> generatedTexFiles = new List<string>();
            string baseName = Path.GetFileNameWithoutExtension(file);
            baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"-speed-[\d\.]+-compressed$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            baseName = System.Text.RegularExpressions.Regex.Replace(baseName, @"-compressed$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string fullOutputTextRaw = ""; // Stores text as is, no timestamp adjustment
            string fullOutputTextOffsetted = ""; // Stores text with timestamps adjusted by partStartTimeSeconds
            int fileTotalInputTokens = 0;
            int fileTotalOutputTokens = 0;
            bool fileProcessingSuccess = true;
            TimeSpan cacheDuration = TimeSpan.FromHours(2); // Define cache duration once
            Task audioExtractionTask = null;
            Action startAudioTask = () => {
                if (_config.GenerateAudioFile && audioExtractionTask == null) {
                    string expectedAudioPath = Path.Combine(fileSpecificOutputFolder, $"{Path.GetFileNameWithoutExtension(file)}_audio.aac");
                    bool useCachedAudio = false;
                    if (System.IO.File.Exists(expectedAudioPath)) {
                        TimeSpan audioCacheDuration = TimeSpan.FromHours(48);
                        if ((DateTime.Now - System.IO.File.GetLastWriteTime(expectedAudioPath)) <= audioCacheDuration) {
                            useCachedAudio = true;
                        }
                    }
                    if (useCachedAudio) {
                        Console.WriteLine($"\n[Cache] Vorhandene Audio-Datei (jünger als 48h) gefunden: {Path.GetFileName(expectedAudioPath)}. Überspringe Audio-Extraktion.");
                    } else {
                        audioExtractionTask = Task.Run(async () => {
                            Console.WriteLine($"\n[FFmpeg] Starte parallele Audio-Extraktion im Hintergrund für {Path.GetFileName(file)}...");
                            await new FfmpegUtilities.FfmpegToolkit().ExtractAudioAsAacAsync(file, fileSpecificOutputFolder);
                            Console.WriteLine($"\n[FFmpeg] Audio-Extraktion für {Path.GetFileName(file)} abgeschlossen.");
                        });
                    }
                }
            };

            for (int i = 0; i < partsWithTimes.Count; i++) {
                string safePartPath = partsWithTimes[i].FilePath;
                double partStartTimeSeconds = partsWithTimes[i].StartTime;
                string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}.tex");

                Console.WriteLine($"\nVerarbeite Teil {i + 1}/{partsWithTimes.Count}: {Path.GetFileName(safePartPath)}");
                // Check if the .tex file already exists and is not older than 2 hours
                if (System.IO.File.Exists(targetPartPath) && (DateTime.Now - System.IO.File.GetLastWriteTime(targetPartPath)) <= cacheDuration) {
                    Console.WriteLine($"  [Resume] Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(targetPartPath)}. Überspringe API-Extraktion für diesen Teil.");
                    string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
                    generatedTexFiles.Add(targetPartPath);
                    fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampHelper.ExtractContentWithoutTimestampHeader(existingTex); // For raw output
                    if (_config.GenerateOffsetFiles) {
                        fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampHelper.AdjustTimestamps(LatexTimestampHelper.ExtractContentWithoutTimestampHeader(existingTex), partStartTimeSeconds); // For offsetted output
                    }
                    startAudioTask();
                    continue;
                }

                (string texOutput, int partInputTokens, int partOutputTokens) result;

                if (i > 0) {
                    // Start delay and upload in parallel for subsequent parts
                    var delayTask = Task.Run(async () => {
                        Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
                        await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...");
                    });

                    var uploadTask = PrepareAndUploadPartAsync(safePartPath, i + 1, partsWithTimes.Count, file, toolkit);

                    // Wait for both to complete. The upload will run concurrently with the delay.
                    await Task.WhenAll(delayTask, uploadTask);

                    var (uploadSuccess, parsedPrompt, attachmentParts) = uploadTask.Result;
                    if (!uploadSuccess) {
                        Console.WriteLine($"  [Fehler] Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                        fileProcessingSuccess = false;
                        hasErrors = true;
                        break;
                    }

                    result = await GenerateTexFromUploadedPartAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles, partStartTimeSeconds);

                    startAudioTask();
                }
                else {
                    // For the first part, no delay is needed, just upload and process.
                    var (uploadSuccess, parsedPrompt, attachmentParts) = await PrepareAndUploadPartAsync(safePartPath, i + 1, partsWithTimes.Count, file, toolkit);
                    if (!uploadSuccess) {
                        Console.WriteLine($"  [Fehler] Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                        fileProcessingSuccess = false;
                        hasErrors = true;
                        break;
                    }

                    startAudioTask();

                    result = await GenerateTexFromUploadedPartAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles, partStartTimeSeconds);
                }

                fileTotalInputTokens += result.partInputTokens;
                fileTotalOutputTokens += result.partOutputTokens;

                if (!string.IsNullOrWhiteSpace(result.texOutput)) {
                    string cleanTex = ExtractionHelpers.CleanLatexResponse(result.texOutput);

                    // Store the raw output for the combined file without offset
                    fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Tokens: Input {result.partInputTokens}, Output {result.partOutputTokens}) ---\n" + cleanTex;
                    if (_config.GenerateOffsetFiles) {
                        fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Tokens: Input {result.partInputTokens}, Output {result.partOutputTokens}) ---\n" + LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds); // Accumulate offsetted text for new parts
                    }

                    // Prepend the start time to the individual part .tex file
                    string partHeader = $"% ==========================================\n" +
                                        $"% AutoExtraction Source Part: {Path.GetFileName(safePartPath)}\n" +
                                        $"% Model: {_config.Model}\n" +
                                        $"% Temperature: {_config.Temperature}\n" +
                                        $"% TopP: {_config.TopP}\n" +
                                        $"% TopK: {_config.TopK}\n" +
                                        $"% MaxOutputTokens: {_config.MaxOutputTokens}\n" +
                                        (_config.ThinkingBudget.HasValue ? $"% ThinkingBudget: {_config.ThinkingBudget.Value}\n" : "") +
                                        (!string.IsNullOrEmpty(_config.ThinkingLevel) ? $"% ThinkingLevel: {_config.ThinkingLevel}\n" : "") +
                                        $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                        $"% PART_START_SECONDS: {partStartTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}\n" +
                                        $"% Tokens (Input: {result.partInputTokens}, Output: {result.partOutputTokens})\n" +
                                        $"% ==========================================\n\n";
                    string uniqueTargetPartPath = GetUniqueTexPath(targetPartPath);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + cleanTex);

                    if (_config.GenerateOffsetFiles) {
                        // NEW: Save the offsetted version of this individual part
                        string offsettedPartContent = LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                        string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}-offset.tex");
                        string uniqueTargetPartPathOffset = GetUniqueTexPath(targetPartPathOffset);
                        await System.IO.File.WriteAllTextAsync(uniqueTargetPartPathOffset, partHeader + offsettedPartContent);
                        Console.WriteLine($"  [Erfolg] Offset-korrigierter Teil gespeichert unter: {Path.GetFileName(uniqueTargetPartPathOffset)}");
                    }
                    generatedTexFiles.Add(uniqueTargetPartPath);
                }
                else {
                    Console.WriteLine($"\n[FEHLER] Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
                    fileProcessingSuccess = false;
                    hasErrors = true;
                    // Clean up individual part files if processing failed mid-way
                    foreach (var f in generatedTexFiles) {
                        try { System.IO.File.Delete(f); } catch { /* Ignore */ }
                    }
                    // Try to delete the file-specific output folder if it's empty or contains only temporary stuff
                    if (Directory.Exists(fileSpecificOutputFolder) && !Directory.EnumerateFileSystemEntries(fileSpecificOutputFolder).Any()) {
                        Directory.Delete(fileSpecificOutputFolder);
                    }
                    break;
                }
            }

            if (fileProcessingSuccess) {
                string targetFilePath = Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
                string targetFilePathOffset = Path.Combine(fileSpecificOutputFolder, $"{Path.GetFileNameWithoutExtension(file)}-offset.tex");

                string uniqueTargetFilePath = GetUniqueTexPath(targetFilePath);
                string header = $"% ==========================================\n" +
                                $"% AutoExtraction Source: {Path.GetFileName(file)}\n" +
                                $"% Model: {_config.Model}\n" +
                                $"% Temperature: {_config.Temperature}\n" +
                                $"% TopP: {_config.TopP}\n" +
                                $"% TopK: {_config.TopK}\n" +
                                $"% MaxOutputTokens: {_config.MaxOutputTokens}\n" +
                                (_config.ThinkingBudget.HasValue ? $"% ThinkingBudget: {_config.ThinkingBudget.Value}\n" : "") +
                                (!string.IsNullOrEmpty(_config.ThinkingLevel) ? $"% ThinkingLevel: {_config.ThinkingLevel}\n" : "") +
                                $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                $"% Total Tokens (Input: {fileTotalInputTokens}, Output: {fileTotalOutputTokens})\n" +
                                $"% ==========================================\n\n";
                await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, header + fullOutputTextRaw);
                Console.WriteLine($"\n[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}");

                string refinementTargetFile = uniqueTargetFilePath;

                if (_config.GenerateOffsetFiles) {
                    // New: Generate the offset version
                    // Note: The last part's StartTime is used as a reference point, but the overall offset should be partStartTimeSeconds from the respective part.
                    // We already accumulated the correctly offsetted text in fullOutputTextOffsetted within the loop.
                    string uniqueTargetFilePathOffset = GetUniqueTexPath(targetFilePathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, header + fullOutputTextOffsetted);
                    Console.WriteLine($"[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}");
                    refinementTargetFile = uniqueTargetFilePathOffset;
                }

                // Trigger LatexRefinementSession immediately for the generated offset file, if enabled.
                // Warten, bis das Audio fertig ist, bevor das Refinement startet,
                // da das Refinement die Audiodatei für die API benötigt!
                if (audioExtractionTask != null) {
                    Console.WriteLine($"\n[AutoExtraction] Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...");
                    await audioExtractionTask;
                }

                // LatexRefinementSession uses its own dedicated API key, so we need to resolve it.
                string refinementApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKeyByName(_latexRefinementConfig?.ApiKeyEnvName ?? "API_KEY-latex-refinement") ?? "no-key";
                Client refinementClient = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);

                // Check for the most recent audio file by looking at modified times, or simply look for the exact name.
                // Since ExtractAudioAsAacAsync might create -copy-1 if it exists, let's just grab the newest .aac file in the folder.
                var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                string audioFilePath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault() 
                                       ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");

                Console.WriteLine($"\n[AutoExtraction] Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
                // Pass the AI Studio client for refinement, as VertexAutoExtractionSession requires an AI Studio client for this
                var refinementSession = new DirectChatAiInteraction.LatexRefinementSession(
                    refinementClient, 
                    _latexRefinementConfig, 
                    refinementTargetFile, 
                    _config, 
                    audioFilePath);
                
                await refinementSession.StartAsync();
            }
        }

        // Warten, bis der Producer-Task sauber beendet wurde (fängt Fehler ab)
        await producerTask;

        if (hasErrors) {
            Console.WriteLine("\n[AutoExtraction] Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).");
        }
        else {
            Console.WriteLine("\n[AutoExtraction] Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!");
        }
    }

    private string GetUniqueTexPath(string originalPath) {
        if (!System.IO.File.Exists(originalPath)) {
            return originalPath;
        }

        Console.WriteLine($"  [Hinweis] Zieldatei '{Path.GetFileName(originalPath)}' existiert bereits.");
        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);
        int copyIndex = 1;
        string newPath;
        do {
            newPath = Path.Combine(dir, $"{baseName}-copy-{copyIndex}{ext}");
            copyIndex++;
        } while (System.IO.File.Exists(newPath));

        Console.WriteLine($"  [Info] Neue Datei wird erstellt: '{Path.GetFileName(newPath)}'");
        return newPath;
    }

    private async Task<(bool success, string? parsedPrompt, List<Part> attachmentParts)> PrepareAndUploadPartAsync(string partFile, int partNumber, int totalParts, string originalFileName, FfmpegUtilities.FfmpegToolkit toolkit) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string prompt = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
        prompt = $"The lecture being transcribed is from {dateInfo.Weekday}, {dateInfo.DateString}. " + prompt;

        double partDurationSeconds = await toolkit.GetVideoDurationAsync(partFile);
        TimeSpan t = TimeSpan.FromSeconds(partDurationSeconds);
        string durationString = string.Format("{0:D2} minutes and {1:D2} seconds", t.Minutes, t.Seconds);

        prompt += $"\n\nAs a reminder: You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long.";

        if (partNumber == 1) {
            prompt += "\n\nNote: 'Part 1' simply refers to the first video chunk of this specific recording, NOT necessarily the very first lecture of the entire course. Do NOT hallucinate introductory speeches or course overviews if they are not actually spoken in the video.";
        } else {
            prompt += "\n\nNote: Start the transcription EXACTLY where the professor starts in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction whatsoever.";
        }

        prompt += $"\n\nIMPORTANT: Do NOT calculate any time offset for the 'spoken-clean' environment. You may start normally at 00:00:00. Ensure that the final timestamp in your very last `spoken-clean` block perfectly matches the {durationString} length of this video segment! Furthermore, do NOT calculate any time scaling factor for the speed adjustments. Just transcribe the timestamps exactly as they appear in the video player.";
        prompt += "\n\nWhen in doubt, transcribe more content into the 'spoken-clean' environment rather than less. Do NOT attempt to merge the current part with the previous parts. A dedicated post-processing AI-routine will handle the final merging and duplicate removal later. Just focus on transcribing the currently uploaded video. Ensure that related mathematical derivations and explanations are grouped together within a single 'math-stroke' environment to keep the logical flow cohesive, self-contained and unbroken.";
        prompt += "\n\nAfter transcribing, meticulously review your generated LaTeX code for any compilation errors, syntax issues, or formatting mistakes, and perform a thorough spell check before providing the final output.";
        prompt += "\n\nCRITICAL RULE: The provided video file is the ONLY source of content. Do NOT invent, hallucinate, or include any external information, formulas, or explanations that are not explicitly present or spoken in this specific video segment.";

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
        if (!uploadSuccess || !attachmentParts.Any()) return (false, null, new List<Part>());

        return (true, parsedPrompt, attachmentParts);
    }

    private async Task<(string texOutput, int inputTokens, int outputTokens)> GenerateTexFromUploadedPartAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles, double partStartTimeSeconds) {
        var userPromptParts = new List<Part>();

        if (previousTexFiles.Any()) {
            Console.WriteLine("  [Kontext] Sende folgende bereits generierte .tex-Dateien als Kontext mit:");
            string contextText = "Here are the context files from the previous parts of the lecture. Please note that these files might contain compilation errors from previous, incomplete, or flawed extractions. Treat them as contextual reference material, but do not assume perfect LaTeX syntax or content validity.\n\n";
            foreach (var texFile in previousTexFiles) {
                Console.WriteLine($"    - {Path.GetFileName(texFile)}");
                string content = await System.IO.File.ReadAllTextAsync(texFile);
                contextText += $"=== REFERENCE CONTEXT: {Path.GetFileName(texFile)} ===\n{content}\n=== END OF REFERENCE CONTEXT ===\n\n";
            }
            userPromptParts.Add(new Part { Text = contextText.TrimEnd() });
        }

        userPromptParts.AddRange(attachmentParts);

        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        var history = new List<Content>();
        history.AddRange(_sessionPreamble);
        history.Add(new Content { Role = "user", Parts = userPromptParts });

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
        if (_config.Model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) || _config.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
            if (_config.ThinkingBudget.HasValue || !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (!string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig.ThinkingLevel = _config.ThinkingLevel;
                } else if (_config.ThinkingBudget.HasValue) {
                    requestConfig.ThinkingConfig.ThinkingBudget = _config.ThinkingBudget;
                }
            }
        }

        string fullResponse = "";
        int currentRequest = 1;
        int maxRequestsPerPart = 6;
        int interactionInputTokens = 0;
        int interactionOutputTokens = 0;

        string logContext = $"[Part {partNumber}] {Path.GetFileName(originalFileName)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
        if (previousTexFiles.Any()) {
            logContext += $"\n[Kontext-Dateien]: {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
        }
        logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";
        string currentLogPrompt = logContext;

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
        Console.CancelKeyPress += cancelHandler;

        while (true) {
            Console.WriteLine($"  [API] Sende Anfrage für Part {partNumber} an {_config.Model} (Request {currentRequest}/{maxRequestsPerPart})...");
            string chunkResp = "";
            int requestInputTokens = 0;
            int requestOutputTokens = 0;
            bool callSuccess = false;

            try {
                callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
                    streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.Model, history, requestConfig),
                    onChunkReceived: async (chunk) => {
                        string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                        Console.Write(txt); // The variable txt is already updated from `chunk.Text ?? ...`, no change needed here.
                        chunkResp += txt;
                        if (chunk.UsageMetadata != null) {
                            if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                            if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        }
                        await Task.CompletedTask;
                    },
                      cancellationToken: cts.Token,
                      retryContext: $"Teil {partNumber} von {Path.GetFileName(originalFileName)}"
                );
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Fahre mit nächstem Teil fort.");
                Console.WriteLine($"Finaler Fehler: {ex.Message}");
                break;
            }

            if (!callSuccess) {
                Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
                break;
            }

            interactionInputTokens += requestInputTokens;
            interactionOutputTokens += requestOutputTokens;
            _sessionTotalInputTokens += requestInputTokens;
            _sessionTotalOutputTokens += requestOutputTokens;

            Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens} (inkl. Thinking Tokens)");
            Console.WriteLine($"  [Part Total Tokens] Input: {interactionInputTokens} | Output: {interactionOutputTokens} (inkl. Thinking Tokens)");
            Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

            fullResponse += chunkResp;
            await _sessionLogger.LogChatAsync(currentLogPrompt, currentLogPrompt, _config.Model, chunkResp, "AutoExtraction", requestInputTokens, requestOutputTokens);

            bool segmentComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool videoComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (videoComplete) break;

            if (currentRequest >= maxRequestsPerPart) {
                Console.WriteLine($"\n\n[WARNUNG] Maximale Anzahl an Requests ({maxRequestsPerPart}) für diesen Teil erreicht. Breche ab.\n  Teil: {partFile}");
                break;
            }

            string continuePrompt = segmentComplete ? "Continue" :
                $"[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
                $"```latex\n{(chunkResp.Length > 300 ? "...\n" + chunkResp.Substring(chunkResp.Length - 300) : chunkResp)}\n```\n\n" +
                "Please \"continue\" exactly where you left off...";

            if (segmentComplete) Console.WriteLine("\n  [AutoExtraction] Segment-Limit erreicht. Sende 'Continue'...");
            else Console.WriteLine("\n  [AutoExtraction] Unerwartetes Ende der Antwort (Max Tokens?). Bereite automatisierten 'Continue'-Prompt vor...");

            Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

            history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = chunkResp } } });
            history.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = continuePrompt } } });
            currentLogPrompt = $"[Continue Prompt für Part {partNumber}]:\n{continuePrompt}";

            Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor der Fortsetzung, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
            if (!await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...")) {
                Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= cancelHandler;
        return (fullResponse, interactionInputTokens, interactionOutputTokens);
    }
}