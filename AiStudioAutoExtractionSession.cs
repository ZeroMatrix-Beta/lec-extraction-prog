using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks; // Removed DirectChatAiInteraction as SessionLogger is now in Infrastructure
using Infrastructure;
using Google.GenAI;
using Google.GenAI.Types;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline. 
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos.
/// </summary>
public class AiStudioAutoExtractionSession {
  private Client _client;
  private readonly AiStudioAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
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

  public AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger) {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
    _sessionLogger = sessionLogger;
  }

  public async Task StartAsync() {
    // [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
    Console.WriteLine($"\n[AutoExtraction] Starte AI Studio Extraction Session...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    if (!Directory.Exists(_config.SourceFolder)) {
      Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
      return;
    }

    if (!Directory.Exists(_config.TargetFolder)) {
      Directory.CreateDirectory(_config.TargetFolder);
    }

    string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    foreach (var f in filesToProcess) {
      string fileName = Path.GetFileName(f).ToLowerInvariant();
      if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^(\d{2,4}-)?\d{2}-\d{2}-(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)\.[a-z0-9]+$")) {
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
        var validPaths = new List<string>();
        foreach (var path in _config.SystemInstructionPaths) {
          if (System.IO.File.Exists(path)) {
            Console.WriteLine($"  - {path}");
            validPaths.Add(path);
          }
          else {
            Console.WriteLine($"  - [NICHT GEFUNDEN] {path}");
          }
        }

        if (validPaths.Any()) {
          Console.Write("System Instructions laden? (j/n): ");
          if (Console.ReadLine()?.Trim().ToLower() == "j") {
            var instructionBuilder = new System.Text.StringBuilder();
            foreach (var path in validPaths) {
              instructionBuilder.AppendLine(await System.IO.File.ReadAllTextAsync(path));
              Console.WriteLine($"  [INFO] System Instruction geladen: {Path.GetFileName(path)}");
            }
            _systemInstructionText = instructionBuilder.ToString();
          }
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
            if (!await AcknowledgeHistoryAsync()) return;
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
    Console.WriteLine("  6) API-Key Profil wechseln (z.B. 'change-key 2')");
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
        Console.WriteLine("  6) API-Key Profil wechseln (z.B. 'change-key 2')");
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
      else if (System.Text.RegularExpressions.Regex.IsMatch(normalizedInput, @"^change[- ]?key\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
        var match = System.Text.RegularExpressions.Regex.Match(normalizedInput, @"change[- ]?key\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int newProfile) && newProfile >= 1 && newProfile <= 3) {
          string? newApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKey(newProfile);
          if (!string.IsNullOrEmpty(newApiKey)) {
            _client = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
            _attachmentHandler.UpdateClient(_client);
            Console.WriteLine($"  [INFO] API-Key erfolgreich auf Profil {newProfile} gewechselt!");
          }
        }
        else {
          Console.WriteLine("  [Fehler] Bitte eine gültige Profilnummer (1, 2 oder 3) angeben.");
        }
      }
      else if (normalizedInput == "7" || normalizedInput.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) {
        _config.Model = await SelectModelAsync();
        Console.WriteLine($"  [INFO] Modell für diese Session auf '{_config.Model}' gesetzt.");
      }
      else {
        await DebugChatAsync(input); // Chat erhält den originalen Input
      }
    }
  }

  private async Task<string> SelectModelAsync() {
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
    Console.Write($"Auswahl (1-10) [Aktuell: {_config.Model}]: ");

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
      Temperature = 0.7f,
      MaxOutputTokens = 65535
    };

    if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }

    if (_config.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
      if (!string.IsNullOrWhiteSpace(_config.ThinkingLevel)) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
      }
    }
    else if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) {
      if (_config.ThinkingBudget.HasValue) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
      }
    }

    Console.Write($"\n[Debug Chat] {_config.Model} (Strg+C zum Abbrechen): ");

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
    Console.CancelKeyPress += cancelHandler;

    int maxRetries = 8;
    int backoff = 30;
    string fullResponse = "";
    bool exceptionCaught = false;

    for (int attempt = 1; attempt <= maxRetries; attempt++) {
      fullResponse = "";
      bool isGenerating = true;
      var inputInterceptorTask = Task.Run(async () => {
        while (isGenerating) {
          if (!Console.IsInputRedirected && Console.KeyAvailable) {
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
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          fullResponse += txt;
          if (chunk.UsageMetadata != null) {
            if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
            if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
          }
        }

        _sessionTotalInputTokens += requestInputTokens;
        _sessionTotalOutputTokens += requestOutputTokens;
        Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens}");
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
          var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
          if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
            int waitTime = serverSuggestedDelay + 10;
            Console.WriteLine($"\n[Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await ExtractionHelpers.SmartDelayAsync(waitTime)) { exceptionCaught = true; break; }
          }
          else {
            Console.WriteLine($"\n[Rate Limit / Überlastung] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await ExtractionHelpers.SmartDelayAsync(backoff)) { exceptionCaught = true; break; }
          }
          backoff *= 2; // Increment backoff for the next potential retry, regardless of whether server suggested a delay
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
  private async Task<bool> AcknowledgeHistoryAsync() {
    var historyPromptParts = new List<Part>(_historyParts);
    historyPromptParts.Add(new Part { Text = $"Hier ist das Material aus meiner History. Bitte lies es sorgfältig durch. Bestätige mir den Erhalt ausnahmslos mit exakt folgendem Text: '[AI-Model: {_config.Model}] Material [...] received and analyzed. I am standing by for your instructions.' Warte danach auf meine nächsten Anweisungen." });
    var userContent = new Content { Role = "user", Parts = historyPromptParts };

    _sessionPreamble.Add(userContent);

    var requestConfig = new GenerateContentConfig { Temperature = 0.0f, MaxOutputTokens = 1024 };
    if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }
    if (_config.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
      if (!string.IsNullOrWhiteSpace(_config.ThinkingLevel)) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
      }
    }
    else if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) {
      if (_config.ThinkingBudget.HasValue) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
      }
    }

    Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.Model}: ");
    int backoff = 30;
    int maxRetries = 10;
    bool success = false;
    string fullResponse = "";

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
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          fullResponse += txt;
          if (chunk.UsageMetadata != null) {
            if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
            if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
          }
        }

        _sessionTotalInputTokens += requestInputTokens;
        _sessionTotalOutputTokens += requestOutputTokens;
        Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens}");
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
          var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
          if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
            int waitTime = serverSuggestedDelay + 15;
            Console.WriteLine($"\n[Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await ExtractionHelpers.SmartDelayAsync(waitTime)) { break; }
          }
          else {
            Console.WriteLine($"\n[Rate Limit / Überlastung] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await ExtractionHelpers.SmartDelayAsync(backoff)) { break; }
          }
          backoff *= 2;
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
      await _sessionLogger.LogChatAsync("[History Acknowledgment]", historyPromptParts.Last().Text ?? "", _config.Model, fullResponse, "AutoExtractionSetup");
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
    string tmpFolder = Path.Combine(_config.TargetFolder, "tmp");
    if (!Directory.Exists(tmpFolder)) Directory.CreateDirectory(tmpFolder);

    // [AI Context] Producer-Consumer Pipeline
    // Wir limitieren das Fließband auf genau 1 Video. Das verhindert, dass FFmpeg 100 Videos 
    // am Stück konvertiert und uns den Festplattenspeicher (tmp-Ordner) füllt, während Gemini noch bei Video 1 hängt.
    // [AI Context] Bounded channel with capacity 1 acts as a strict backpressure mechanism.
    var channel = Channel.CreateBounded<(string originalFile, List<string> parts, bool isCached)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

    // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
    var producerTask = Task.Run(async () => {
      foreach (var file in files) {
        string baseName = Path.GetFileNameWithoutExtension(file);
        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        var cachedParts = Directory.GetFiles(tmpFolder, $"{baseName}-{dateStr}-part*.mp4").ToList();
        bool useCache = false;

        if (cachedParts.Count > 0) {
          var fileInfo = new FileInfo(cachedParts[0]);
          if ((DateTime.Now - fileInfo.LastWriteTime).TotalHours <= 2) {
            // [AI Context] Defend against incomplete caches from interrupted FFmpeg runs.
            // We expect exactly 3 parts. If fewer are found, the cache is corrupted or incomplete.
            // [Human] Wenn ein alter Lauf abgebrochen ist, liegen vielleicht nur 1-2 Teile im Cache. Das wird hier verhindert!
            if (cachedParts.Count >= 3) {
              useCache = true;
            }
            else {
              Console.WriteLine($"\n  [Cache] Ignoriere unvollständigen Cache für {baseName} ({cachedParts.Count} Teil(e) gefunden, erwartet: 3). FFmpeg wird neu gestartet...");
              foreach (var f in cachedParts) { try { System.IO.File.Delete(f); } catch { } }
            }
          }
        }

        if (useCache) {
          Console.WriteLine($"\n[Cache] FFmpeg übersprungen für {Path.GetFileName(file)}. Verwende gecachte Dateien (jünger als 2h).");
          cachedParts.Sort();
          await channel.Writer.WriteAsync((file, cachedParts, true));
          continue;
        }

        Console.WriteLine($"\n[FFmpeg Producer] Starte Konvertierung für {Path.GetFileName(file)}...");
        string? speedVideo = await toolkit.ProcessGeneralVideoAsync(file, tmpFolder, speedMultiplier: _speed, fps: 1, downmixToMono: true);
        if (speedVideo == null) continue;

        var parts = await toolkit.ProcessSplitVideoAsync(speedVideo, tmpFolder, parts: 3, overlapSeconds: 180, downmixToMono: false, streamCopy: true);
        if (parts.Count == 0) continue;

        List<string> safeParts = new List<string>();
        for (int i = 0; i < parts.Count; i++) {
          string safePartPath = Path.Combine(tmpFolder, $"{baseName}-{dateStr}-part{i + 1}.mp4");
          if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
          System.IO.File.Move(parts[i], safePartPath);
          safeParts.Add(safePartPath);
        }

        Console.WriteLine($"[FFmpeg Producer] {Path.GetFileName(file)} erfolgreich konvertiert! Lege es aufs Fließband für Gemini...");
        await channel.Writer.WriteAsync((file, safeParts, false));
      }
      channel.Writer.Complete(); // Signalisiert dem Fließband: "Feierabend, es kommen keine Videos mehr."
    });

    // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
    // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
    await foreach (var job in channel.Reader.ReadAllAsync()) {
      string file = job.originalFile;
      var parts = job.parts;
      bool isCached = job.isCached;

      Console.WriteLine($"\n[Gemini Consumer] === Starte API-Extraktion für {Path.GetFileName(file)} ===");
      List<string> generatedTexFiles = new List<string>();
      string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
      string baseName = Path.GetFileNameWithoutExtension(file);
      string fullOutputText = "";

      for (int i = 0; i < parts.Count; i++) {
        string safePartPath = parts[i];
        string texPath = Path.ChangeExtension(safePartPath, ".tex");

        Console.WriteLine($"\nVerarbeite Teil {i + 1}/{parts.Count}: {Path.GetFileName(safePartPath)}");

        if (System.IO.File.Exists(texPath) && new FileInfo(texPath).Length > 0) {
          Console.WriteLine($"  [Resume] Überspringe API-Aufruf. Verwende bereits existierende Datei: {Path.GetFileName(texPath)}");
          string existingTex = await System.IO.File.ReadAllTextAsync(texPath);
          fullOutputText += $"\n\n% --- TEIL {i + 1} ---\n" + existingTex;
          generatedTexFiles.Add(texPath);
          continue;
        }

        if (i > 0) {
          Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen...");
          await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...");
        }

        string texOutput = await ProcessPartWithGeminiAsync(safePartPath, i + 1, parts.Count, generatedTexFiles, file);

        if (!string.IsNullOrWhiteSpace(texOutput)) {
          string cleanTex = ExtractionHelpers.CleanLatexResponse(texOutput);

          fullOutputText += $"\n\n% --- TEIL {i + 1} ---\n" + cleanTex;

          await System.IO.File.WriteAllTextAsync(texPath, cleanTex);

          // Hier werden .tex dateien geschrieben:
          generatedTexFiles.Add(texPath);
        }
      }

      string targetFilePath = Path.Combine(_config.TargetFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
      string header = $"% ==========================================\n% AutoExtraction Source: {Path.GetFileName(file)}\n% Model: {_config.Model}\n% ==========================================\n\n";
      await System.IO.File.WriteAllTextAsync(targetFilePath, header + fullOutputText);
      Console.WriteLine($"\n[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {targetFilePath}");
    }

    // Warten, bis der Producer-Task sauber beendet wurde (fängt Fehler ab)
    await producerTask;
    Console.WriteLine("\n[AutoExtraction] Batch-Verarbeitung vollständig abgeschlossen!");
  }

  /// <summary>
  /// [AI Context] Core prompt engineering logic for processing isolated video chunks.
  /// Injects historical LaTeX context from previously processed segments to maintain narrative and mathematical continuity across the 3-minute overlap boundaries.
  /// [Human] Baut den exakten KI-Prompt für jeden einzelnen Videoteil zusammen und fügt alte LaTeX-Dateien als Kontext hinzu, damit Sätze nicht in der Mitte abbrechen.
  /// </summary>
  private async Task<string> ProcessPartWithGeminiAsync(string partFile, int partNumber, int totalParts, List<string> previousTexFiles, string originalFileName) {
    var dateInfo = VideoDateParser.Parse(originalFileName);
    string prompt = _config.Prompt;

    // [AI Context] Dynamic prompt engineering. Instructs the model to treat the chunk as part of a larger whole, 
    // providing absolute timestamps and previously generated LaTeX to maintain cross-chunk continuity.
    prompt += $"\n\n[Meta-Information]: These {totalParts} video parts (and corresponding .tex files) originate from the lecture on {dateInfo.Weekday}, {dateInfo.DateString}. Do not include this date in the compiled LaTeX code right now; it is just for your internal context.";
    prompt += $"\n\nThe uploaded video is part {partNumber} of {totalParts} from this lecture.";
    prompt += $"\n\nThe video is played back / scaled to {_speed}x speed.";

    if (partNumber > 1) {
      // [AI Context] Continuous State Injection: Appends the exact LaTeX output of the previous chunks so the model can seamlessly finish mid-sentence derivations.
      prompt += "\n\nThe previously generated LaTeX documents for the prior parts are included in the context (see --- DOKUMENT START ---). Please use them to maintain context continuity.";
      prompt += "\n\nNote: Consecutive video parts have an intentional 3-minute overlap to prevent context loss. If the video starts mid-sentence, use the provided LaTeX context from the previous part to reconstruct the full sentence.";
    }

    // [AI Context] Explicitly disables temporal scaling logic within the model, forcing it to transcribe the burned-in UI timestamps directly.
    prompt += "\n\nIMPORTANT: Do NOT calculate any time offset for the 'spoken-clean' environment. You may start normally at 00:00:00. Furthermore, do NOT calculate any time scaling factor for the speed adjustments. Just transcribe the timestamps exactly as they appear in the video player.";
    prompt += "\n\nWhen in doubt, transcribe more content into the 'spoken-clean' environment rather than less. Do NOT attempt to merge the current part with the previous parts. A dedicated post-processing AI-routine will handle the final merging and duplicate removal later. Just focus on transcribing the currently uploaded video. Ensure that related mathematical derivations and explanations are grouped together within a single 'math-stroke' environment to keep the logical flow cohesive, self-contained and unbroken.";

    var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
    if (!uploadSuccess || attachmentParts.Count == 0) return "";

    var userPromptParts = new List<Part>(attachmentParts);

    if (previousTexFiles.Any()) {
      Console.WriteLine("  [Kontext] Sende folgende bereits generierte .tex-Dateien als Kontext mit:");
      foreach (var texFile in previousTexFiles) {
        Console.WriteLine($"    - {Path.GetFileName(texFile)}");
        string content = await System.IO.File.ReadAllTextAsync(texFile);
        userPromptParts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(texFile)}) ---\n{content}\n--- DOKUMENT ENDE ---" });
      }
    }

    userPromptParts.Add(new Part { Text = parsedPrompt });

    var history = new List<Content>();

    history.AddRange(_sessionPreamble);
    history.Add(new Content { Role = "user", Parts = userPromptParts });

    var requestConfig = new GenerateContentConfig {
      Temperature = 0.0f,
      MaxOutputTokens = 65535
    };

    if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }
    if (_config.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
      if (!string.IsNullOrWhiteSpace(_config.ThinkingLevel)) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
      }
    }
    else if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) {
      if (_config.ThinkingBudget.HasValue) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
      }
    }

    string fullResponse = "";
    int currentRequest = 1;
    int maxRequestsPerPart = 5;

    int interactionInputTokens = 0;
    int interactionOutputTokens = 0;

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
              Console.Write(txt);
              chunkResp += txt;
              if (chunk.UsageMetadata != null) {
                if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
              }
              await Task.CompletedTask;
            },
            cancellationToken: cts.Token
        );
      }
      catch (Exception ex) {
        // ApiResilience re-throws unrecoverable errors
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

      Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens}");
      Console.WriteLine($"  [Part Total Tokens] Input: {interactionInputTokens} | Output: {interactionOutputTokens}");
      Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

      fullResponse += chunkResp;
      await _sessionLogger.LogChatAsync($"[Part {partNumber}] {originalFileName}", prompt ?? "", _config.Model, chunkResp, "AutoExtraction");

      bool segmentComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      bool videoComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      if (videoComplete) {
        break; // Finished
      }

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

      Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor der Fortsetzung, um API-Limits zu schonen...");
      if (!await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...")) {
        Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
        break;
      }

      currentRequest++;
    }

    Console.CancelKeyPress -= cancelHandler;
    return fullResponse;
  }
}