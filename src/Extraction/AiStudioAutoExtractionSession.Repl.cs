using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Globalization;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;
using LectureExtraction.Refinement;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Interactive REPL: menu printing, command dispatch, and the standalone debug-chat used to
/// test prompts against the model without initializing the full FFmpeg pipeline. Split out of
/// AiStudioAutoExtractionSession.cs (Phase 4.5) — self-contained debug/menu functionality with no
/// coupling to the batch pipeline beyond reading session fields/config.
/// [Human] Der REPL-Teil der Session: Menü, Befehls-Handler und Debug-Chat.
/// </summary>
public partial class AiStudioAutoExtractionSession {
    /// <summary>
    /// [AI Context] Interactive control loop for the AutoExtraction mode.
    /// Allows developers to dynamically adjust FFmpeg speeds, trigger specific files, or chat directly with the configured model for prompt debugging before launching a massive batch job.
    /// [Human] Eine interaktive Konsole, um vor dem großen Batch-Start Parameter (wie Video-Speed) zu testen oder den Prompt zu debuggen.
    /// </summary>
    private void PrintCommandsMenu() {
        Console.WriteLine("\n📋 Befehle:");
        Console.WriteLine("  1) 📜 Befehle anzeigen");
        Console.WriteLine("  2) ⚡ Video-Geschwindigkeit setzen (z.B. 'set speed 1.5' oder nur '2'). Standard: 1.2");
        Console.WriteLine("  3) 🎬 Einzelnes Video interaktiv auswählen und konvertieren");
        Console.WriteLine("  4) 🚀 Alle Videos im Quellordner konvertieren");
        Console.WriteLine("  5) 🚪 Beenden (exit/quit)");
        Console.WriteLine("  6) 📺 YouTube-Video transkribieren (per URL oder Config)");
        Console.WriteLine("  7) 🤖 Modell auswählen (aktuell: " + _config.CurrentModel + ")");
        Console.WriteLine("  8) 🔧 Latex Refinement interaktiv starten (Debugging)");
        Console.WriteLine("  9) 🔑 API-Key Profil wechseln (z.B. 'change-key 2', 0 für dediziert) (aktuell: " + (_config.ActiveApiProfile == 0 ? "dediziert" : $"Profil {_config.ActiveApiProfile}") + ")");
        Console.WriteLine("  (Alles andere wird als normaler Chat-Prompt zum Debuggen an Gemini gesendet)");
        Console.WriteLine("\n💡 Hinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");
    }

    private async Task ReplLoopAsync() {
        PrintCommandsMenu();

        while (true) {
            if (!Console.IsInputRedirected) {
                while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            }
            Console.Write("\nAutoExt> ");
            string input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;

            string normalizedInput = input.TrimStart('/');
            if (normalizedInput == "5" || normalizedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

            if (TryHandleReplShowCommands(normalizedInput)) continue;
            if (TryHandleReplSetSpeed(normalizedInput)) continue;
            if (await TryHandleReplConvertChosenVideoAsync(normalizedInput)) continue;
            if (await TryHandleReplConvertAllVideosAsync(normalizedInput)) continue;
            if (TryHandleReplClearDebugHistory(normalizedInput)) continue;
            if (await TryHandleReplYouTubeAsync(normalizedInput)) continue;
            if (TryHandleReplSetModel(normalizedInput)) continue;
            if (await TryHandleReplRunRefinementAsync(normalizedInput)) continue;
            if (TryHandleReplChangeKey(normalizedInput)) continue;

            await DebugChatAsync(input); // Chat erhält den originalen Input
        }
    }

    private bool TryHandleReplShowCommands(string normalizedInput) {
        if (normalizedInput != "1" && !normalizedInput.Equals("show commands", StringComparison.OrdinalIgnoreCase)) return false;
        PrintCommandsMenu();
        return true;
    }

    private bool TryHandleReplSetSpeed(string normalizedInput) {
        if (normalizedInput != "2" && !normalizedInput.StartsWith("2 ") && !normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) return false;

        string speedInput = "";
        if (normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) speedInput = normalizedInput[9..].Trim();
        else if (normalizedInput.StartsWith("2 ")) speedInput = normalizedInput[2..].Trim();
        else if (normalizedInput == "2") {
            Console.Write("Neuer Speed-Wert (z.B. 1.5): ");
            speedInput = Console.ReadLine()?.Trim() ?? "";
        }

        if (double.TryParse(speedInput, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedSpeed)) {
            _speed = parsedSpeed;
            Console.WriteLine($"Speed gesetzt auf {_speed}x");
        }
        else {
            Console.WriteLine("Ungültiger Wert für speed.");
        }
        return true;
    }

    private async Task<bool> TryHandleReplConvertChosenVideoAsync(string normalizedInput) {
        if (normalizedInput != "3" && !normalizedInput.Equals("convert chosen video", StringComparison.OrdinalIgnoreCase)) return false;
        var files = FileSelectionPrompt.SelectSingleFile(_config.SourceFolder);
        if (files.Length > 0) {
            await SetupContextAndProcessAsync(files);
        }
        return true;
    }

    private async Task<bool> TryHandleReplConvertAllVideosAsync(string normalizedInput) {
        if (normalizedInput != "4" && !normalizedInput.Equals("convert all videos", StringComparison.OrdinalIgnoreCase)) return false;
        var files = VideoBatchSelector.SelectAndFilterVideosForBatch(_config.SourceFolder);
        if (files.Length > 0) {
            await SetupContextAndProcessAsync(files);
        }
        return true;
    }

    private bool TryHandleReplClearDebugHistory(string normalizedInput) {
        if (!normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase)) return false;
        _debugChatHistory.Clear();
        Console.WriteLine("  [INFO] Debug-Chat Verlauf gelöscht.");
        return true;
    }

    private async Task<bool> TryHandleReplYouTubeAsync(string normalizedInput) {
        if (normalizedInput != "6" && !normalizedInput.Equals("youtube", StringComparison.OrdinalIgnoreCase)) return false;
        await ProcessYouTubeTasksAsync();
        return true;
    }

    private bool TryHandleReplSetModel(string normalizedInput) {
        if (normalizedInput != "7" && !normalizedInput.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) return false;
        SelectModel();
        ConfigLoader<AiStudioAutoExtractionConfig>.Save(_config);
        ModelSyncService.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: false, _latexRefinementConfig);
        Console.WriteLine($"  [INFO] Modell für diese Session auf '{_config.CurrentModel}' gesetzt und für die gesamte Pipeline (AutoExtraction & LatexRefinement) in beiden JSON-Konfigurationen gespeichert.");
        return true;
    }

    private async Task<bool> TryHandleReplRunRefinementAsync(string normalizedInput) {
        if (normalizedInput != "8" && !normalizedInput.Equals("run refinement", StringComparison.OrdinalIgnoreCase)) return false;
        await RefinementUiHelper.StartInteractiveRefinementAsync(_latexRefinementConfig, _config);
        return true;
    }

    private bool TryHandleReplChangeKey(string normalizedInput) {
        if (normalizedInput != "9" && !normalizedInput.StartsWith("9 ") && !normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase) && !normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) return false;

        string profileInput = "";
        if (normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase)) {
            profileInput = normalizedInput["change-key".Length..].Trim();
        }
        else if (normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) {
            profileInput = normalizedInput["change key".Length..].Trim();
        }
        else if (normalizedInput.StartsWith("9 ")) {
            profileInput = normalizedInput[2..].Trim();
        }

        if (string.IsNullOrEmpty(profileInput)) {
            Console.Write("Neues API-Key Profil (0-3): ");
            profileInput = Console.ReadLine()?.Trim() ?? "";
        }

        if (int.TryParse(profileInput, out int newProfile) && newProfile >= 0 && newProfile <= 3) {
            string? newApiKey;
            if (newProfile == 0) {
                newApiKey = GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-automated-content-extraction");
            }
            else {
                newApiKey = GoogleAiClientBuilder.ResolveApiKey(newProfile);
            }

            if (!string.IsNullOrEmpty(newApiKey)) {
                _client = GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
                _attachmentHandler.UpdateClient(_client);
                _config.ActiveApiProfile = newProfile;
                ConfigLoader<AiStudioAutoExtractionConfig>.Save(_config);
                Console.WriteLine($"  [INFO] API-Key erfolgreich auf Profil {newProfile} gewechselt und in Konfiguration gespeichert!");
            }
        }
        else {
            Console.WriteLine("  [Fehler] Bitte eine gültige Profilnummer (0, 1, 2 oder 3) angeben.");
        }
        return true;
    }

    /// <summary>
    /// [AI Context] Interactive model picker that reads models from _config.Model[] array in the configured order.
    /// The user's selection is persisted via CurrentModelIndex so it survives restarts.
    /// [Human] Das Startmenü in der Konsole. Modelle werden aus der JSON-Config gelesen – einfach dort die Liste anpassen.
    /// </summary>
    private void SelectModel() {
        string[] models = _config.Model;
        if (models.Length == 0) {
            Console.WriteLine("  [WARNUNG] Keine Modelle in der Konfiguration vorhanden.");
            return;
        }

        Console.WriteLine($"\n=== Model Selection (AI Studio) ===");
        Console.WriteLine("Wähle ein Modell:");
        for (int i = 0; i < models.Length; i++) {
            string marker = (i == _config.CurrentModelIndex) ? " [aktiv]" : "";
            Console.WriteLine($" {i + 1}) {models[i]}{marker}");
        }
        Console.Write($"Auswahl (1-{models.Length}) [Aktuell: {_config.CurrentModel}]: ");

        string choice = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(choice)) return;

        if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= models.Length) {
            _config.CurrentModelIndex = idx - 1;
            ModelSyncService.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: false, _latexRefinementConfig);
        }
        else if (choice.Contains('-')) {
            // Freetext model name – find or append
            int found = Array.IndexOf(models, choice);
            if (found >= 0) {
                _config.CurrentModelIndex = found;
            }
            else {
                Console.WriteLine($"  [INFO] Modell '{choice}' nicht in der Liste gefunden. Auswahl unverändert.");
            }
            ModelSyncService.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: false, _latexRefinementConfig);
        }
    }

    /// <summary>
    /// [AI Context] A dedicated REPL chat for testing prompts against the model without initializing the full FFmpeg pipeline.
    /// Contains identical retry/backoff logic to the main extraction loop to accurately simulate API conditions.
    /// [Human] Der Debug-Chat. Hier kannst du mit der KI schreiben und testen, wie sie auf Prompts reagiert, bevor du hunderte Videos durchjagst.
    /// </summary>
    private async Task DebugChatAsync(string input) {
        _debugChatHistory.Add(new Content { Role = "user", Parts = [new() { Text = input }] });

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        if (ModelCapabilities.SupportsThinking(_config.CurrentModel)) {
            bool isGemini25 = _config.CurrentModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
            }
            else if (_config.ThinkingBudget.HasValue) {
                int budget = _config.ThinkingBudget.Value;
                if (budget > 32768) budget = 32768;
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = budget };
            }
        }

        var (fullResponse, exceptionCaught, wasCancelled) = await StreamDebugChatResponseAsync(requestConfig);

        if (exceptionCaught || wasCancelled) {
            Console.WriteLine("\n\n[INFO] Debug-Chat durch Benutzer abgebrochen.");
        }
        else if (!string.IsNullOrWhiteSpace(fullResponse)) {
            _debugChatHistory.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
        }
        else if (_debugChatHistory.Count > 0 && _debugChatHistory.Last().Role == "user") {
            // Falls abgebrochen wurde, bevor die KI etwas gesagt hat, die User-Nachricht entfernen.
            _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
        }
    }

    /// <summary>
    /// [AI Context] Streams the debug-chat response with a hand-rolled retry/backoff loop (distinct from
    /// ApiResilience.ExecuteStreamWithRetryAsync used elsewhere): network errors wait 5 minutes, "high
    /// demand" waits a flat 3 minutes, the first rate-limit reads the server-suggested delay + 20s
    /// buffer, and subsequent rate-limits increment linearly by 30s rather than backing off exponentially.
    /// [Human] Streamt die Debug-Chat-Antwort mit eigener Retry-/Backoff-Logik (Netzwerkfehler, Rate-Limits).
    /// </summary>
    private async Task<(string FullResponse, bool ExceptionCaught, bool WasCancelled)> StreamDebugChatResponseAsync(GenerateContentConfig requestConfig) {
        Console.Write($"\n[Debug Chat] {_config.CurrentModel} (Strg+C zum Abbrechen): ");

        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
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
                    if (!InteractiveDelay.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
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
                int requestCachedTokens = 0;

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.CurrentModel, _debugChatHistory, requestConfig);
                await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
                    if (cts.IsCancellationRequested) break;
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Console.Write(txt);
                    fullResponse += txt;
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) requestCachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                }

                _sessionTotalInputTokens += requestInputTokens;
                _sessionTotalOutputTokens += requestOutputTokens;
                _sessionTotalCachedTokens += requestCachedTokens;
                Console.WriteLine($"\n  [Request Tokens]       Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {Math.Max(0, requestInputTokens - requestCachedTokens):N0} | Output: {requestOutputTokens:N0} (inkl. Thinking Tokens)");
                Console.WriteLine($"  [Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens):N0} | Output: {_sessionTotalOutputTokens:N0}");

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

                bool isOverloaded = ApiResilience.IsTransientError(ex);
                if (isOverloaded && attempt < maxRetries) {
                    // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
                    // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
                    // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
                    // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
                    int waitTime;
                    string contextMsg = " [Debug Chat]";
                    string delayMessage = "Still waiting for the acknowledgment / processing...";

                    if (ApiResilience.IsNetworkConnectionError(ex)) {
                        waitTime = 300; // 5 Minuten
                        Console.WriteLine($"\n[Netzwerk-Fehler]{contextMsg} Verbindung unterbrochen ({ex.GetType().Name}: {ex.Message}).");
                        Console.WriteLine($"  Keine Panik! Du hast jetzt 300 Sekunden (5 Minuten) Zeit, um deinen Hotspot oder deine Internetverbindung zu reparieren...");
                        Console.WriteLine($"  --> Sobald die Verbindung wieder steht, drücke ENTER, um sofort weiterzumachen! (Versuch {attempt + 1}/{maxRetries})");
                        delayMessage = "Warte auf Wiederherstellung der Internetverbindung / Hotspot...";
                    }
                    else if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
                        waitTime = 180; // 3 Minuten
                        Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        backoff = waitTime;
                    }
                    else if (attempt == 1) {
                        var retryMatch = MyRegex().Match(ex.Message);
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
                    if (!await InteractiveDelay.SmartDelayAsync(waitTime, delayMessage)) { exceptionCaught = true; break; }
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

        return (fullResponse, exceptionCaught, cts.IsCancellationRequested);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)s""")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
