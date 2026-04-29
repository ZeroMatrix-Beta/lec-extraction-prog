using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using GoogleGenAi;
using Config;
using DirectChatAiInteraction;
using Infrastructure;
using static System.Console;
using DirectChatAiInteraction.AiStudio;

namespace DirectChatAiInteraction.AiStudio;
/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for the conversational AI interface.
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots. Hier werden deine Eingaben gelesen, an Google gesendet und die Antworten in der Konsole ausgegeben.
/// </summary> 
public class DirectAiChatSessionAiStudio {
  // [AI Context] Global state for file resolution. 
  // UploadFolderPath is the base dir for relative paths. HistoryFolderPath is an absolute path.
  // Konfigurierbarer Basis-Pfad für deine Uploads. 
  // Z.B.: @"C:\Users\miche\programming\lec-extraction-prog\uploads"
  private readonly string UploadFolderPath;

  // Absoluter Pfad zum Ordner für die automatisch zu ladende History.
  // Z.B.: @"C:\Users\miche\programming\lec-extraction-prog\history"
  private readonly string[] HistoryPreloadPaths;

  // Standard-Nachricht, die gesendet wird, wenn die History geladen wird.
  private string InitialHistoryPrompt = "Hier ist das Material aus meiner History. Bitte lies es sorgfältig durch. Bestätige mir den Erhalt ausnahmslos mit exakt folgendem Text: '[AI-Model: {0}] Material [...] received and analyzed. I am standing by for your instructions.' Warte danach auf meine nächsten Anweisungen.";

  // [GCS] Der Name deines Google Cloud Storage Buckets
  // Z.B.: "en-linalg-biran-gemini-videos"
  private readonly string GcsBucketName;

  // [Log-Ordner] Status für den aktuellen Programmablauf
  private readonly string LogFolderPath;
  private readonly string SystemInstructionPath;
  private string? _systemInstructionText;
  private DirectAiChatSessionAiStudioGenerationConfig AIParams;
  private readonly bool IsAiStudio;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private Client _client;
  private int _activeApiProfile;
  private int _sessionTotalInputTokens = 0;
  private int _sessionTotalOutputTokens = 0;

  // [AI Context] Constructor injects config dependencies to isolate state.
  public DirectAiChatSessionAiStudio(Client client, DirectAiChatSessionAiStudioConfig config, SessionLogger logger, AttachmentHandler attachmentHandler, bool isAiStudio) {
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

    // [AI Context] Creates a localized deep copy of AI parameters.
    // [Human] Kopiert die Standard-Werte, damit wir sie später mit "/set temp" im Chat verändern können, ohne das Original zu überschreiben.
    // Wir legen eine lokale Kopie an, damit /set Befehle nur diese Sitzung modifizieren
    AIParams = new DirectAiChatSessionAiStudioGenerationConfig {
      Temperature = config.AI.Temperature,
      TopP = config.AI.TopP,
      TopK = config.AI.TopK,
      MaxOutputTokens = config.AI.MaxOutputTokens
    };
  }

  /// <summary>
  /// [AI Context] Asynchronous entry point for the session. Initializes API clients and directory structures.
  /// [Human] Startet die Session, verbindet sich mit Google und erstellt die Log-Ordner für diesen Chat-Verlauf.
  /// </summary>
  public async Task StartAsync() {
    while (true) {
      string selectedModel = await SelectModelAsync();
      if (selectedModel == "__EXIT__") return;
      if (selectedModel == "__CHANGED_KEY__") continue;

      // 3b. Bucket beim Start aufräumen (falls von einem vorherigen Absturz noch Videos übrig sind)
      await CleanupGcsBucketAsync();

      // [AI Context] Implements session persistence by isolating text/LaTeX outputs in discrete timestamped directories.
      // [Human] Erstellt für jede neue Chat-Sitzung einen eigenen Ordner, damit nichts aus Versehen überschrieben wird.
      // 3c. Session Log-Ordner ermitteln und erstellen (folder-1, folder-2...)
      _sessionLogger.InitializeSession();

      // [AI Context] Load System Instructions (Persona & Rules) into memory.
      bool loadedSysPrompt = false;
      string sysPromptChoice = await PromptWithCommandsAsync($"\n[Setup] System Instruction laden? Pfad: '{SystemInstructionPath}' (j/n): ");
      if (sysPromptChoice == "__EXIT__") return;
      if (sysPromptChoice == "__CHANGED_KEY__") continue;

      if (sysPromptChoice.Trim().ToLower() == "j") {
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

      string? initialInput = await GetInitialHistoryCommandAsync(selectedModel);
      if (initialInput == "__EXIT__") return;
      if (initialInput == "__CHANGED_KEY__") continue;

      bool loadedHistory = initialInput != null;

      _sessionLogger.SetSessionMetadata(loadedSysPrompt, loadedHistory);
      await _sessionLogger.LogSessionSetupAsync();

      // 4. Starte die Haupt-Chat-Schleife
      await RunChatSessionAsync(selectedModel, initialInput);
      break; // Beendet den aktuellen Setup-Loop und geht komplett ins Hauptmenü zurück
    }
  }

  /// <summary>
  /// [AI Context] Interactive console menu for initial model selection. Returns the specific model ID string.
  /// RULE: If you add, modify, or remove a model in the switch expression below, you MUST synchronously update the WriteLine menu text here!
  /// The UI representation and the underlying switch logic must ALWAYS perfectly mirror each other.
  /// [Human] Das Startmenü in der Konsole. Wenn du neue Modelle hinzufügst, musst du sie exakt hier eintragen.
  /// </summary>
  private async Task<string> SelectModelAsync() {
    WriteLine($"\n=== Model Selection (AI Studio) ===");
    WriteLine("Wähle ein Modell:");
    WriteLine(" 1) gemini-3.1-flash-lite-preview");
    WriteLine(" 2) gemini-3-flash-preview");
    WriteLine(" 3) gemini-3.1-pro-preview");
    WriteLine(" 4) gemini-2.5-flash");
    WriteLine(" 5) gemini-2.5-flash-lite");
    WriteLine(" 6) gemini-2.5-pro");
    WriteLine(" 7) gemma-3-27b-it                || (Open Model, 27B Parameter)");
    WriteLine(" 8) gemini-1.5-flash              || (Schnelles Fallback für Video/Audio)");
    WriteLine(" 9) gemini-1.5-pro                || (Mächtiges Fallback für Video/Audio)");
    WriteLine("10) gemini-robotics-er-1.5-preview|| (Free Tier, Multimodal)");
    WriteLine("11) gemini-robotics-er-1.6-preview|| (Neues Robotics Modell)");

    string choice = await PromptWithCommandsAsync("Auswahl (1-11) [Standard: 4]: ");
    if (choice == "__EXIT__" || choice == "__CHANGED_KEY__") return choice;

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
      "10" => "gemini-robotics-er-1.5-preview",
      "11" => "gemini-robotics-er-1.6-preview",
      _ => "gemini-2.5-flash"
    };
  }

  // --- Ausgelagerte Methoden ---

  /// <summary>
  /// [AI Context] Main REPL loop. 
  /// Mutates the 'history' list to maintain conversation state. Catches errors to prevent chat state corruption.
  /// Hauptschleife des Chats: Liest kontinuierlich Benutzereingaben, verarbeitet Befehle,
  /// sendet Nachrichten an die Gemini-API und gibt die gestreamten Antworten in der Konsole aus.
  /// </summary>
  private async Task RunChatSessionAsync(string selectedModel, string? initialInput) {
    var history = new List<Content>();

    // [AI Context] Cache initial state to allow memory resets without restarting the runtime.
    // [Human] Speichert den Zustand nach dem ersten Laden ab. So funktioniert der "clear" Befehl!
    var initialHistory = new List<Content>(history); // Den Startzustand merken
    string userName = "AI Studio User";

    WriteLine($"\n--- Chat gestartet ({selectedModel} | API Profil: {_activeApiProfile}) ---");
    ShowCommands();

    while (true) {
      string? input;
      if (initialInput != null) {
        // [AI Context] Automatically executes the history attachment command on the first loop iteration without requiring user interaction.
        input = initialInput;
        WriteLine($"\n{userName}: {input}");
        initialInput = null; // Nur beim allerersten Durchlauf verwenden
      }
      else {
        // [AI Context] Flush the input buffer before asking for new input.
        // Prevents confusing "ghost inputs" if the user typed something while the AI was generating or waiting in a Task.Delay backoff loop.
        if (!Console.IsInputRedirected) {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
        }
        Write($"\n{userName}: ");
        input = ReadLine();
      }

      if (string.IsNullOrWhiteSpace(input)) continue;
      if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

      var parts = new List<Part>();
      string promptText = input;

      // Extract command handling to keep the main loop focused purely on the chat flow
      // [AI Context] Uses a Command/Interceptor pattern. If TryHandleBuiltInCommandsAsync returns true, the input was a local REPL command, avoiding an API call.
      bool isCommandHandled = await TryHandleBuiltInCommandsAsync(input, history, initialHistory, parts, newPrompt => promptText = newPrompt);

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

      int backoff = 30;
      int maxRetries = 5;

      for (int attempt = 1; attempt <= maxRetries; attempt++) {
        try {
          // [AI Context] Hands off to streaming handler. Mutates 'history' internally.
          await StreamGeminiResponseAsync(selectedModel, history, input, promptText, userName);
          break; // Erfolg
        }
        catch (Exception ex) {
          WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
          WriteLine($"Originaler Fehlertext: {ex.Message}");

          if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) {
            var metricMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Quota exceeded for metric: ([^,]+)");
            if (metricMatch.Success) WriteLine($"  [API-Limit] Metrik überschritten: {metricMatch.Groups[1].Value.Trim()}");

            var retryTimeMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Please retry in ([^s]+s)");
            if (retryTimeMatch.Success) WriteLine($"  [API-Limit] Erwartete Wartezeit: {retryTimeMatch.Groups[1].Value}");
          }

          bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);
          if (attempt < maxRetries && isOverloaded) {
            var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
            if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
              int waitTime = serverSuggestedDelay + 10;
              WriteLine($"\n[Rate Limit] API schlägt Wartezeit vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
              if (!await SmartDelayAsync(waitTime)) {
                WriteLine("\n[INFO] Wartezeit durch Benutzer abgebrochen.");
                history.RemoveAt(history.Count - 1);
                break;
              }
            }
            else {
              WriteLine($"\n[Rate Limit / Überlastung] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
              if (!await SmartDelayAsync(backoff)) {
                WriteLine("\n[INFO] Wartezeit durch Benutzer abgebrochen.");
                history.RemoveAt(history.Count - 1);
                break;
              }
              backoff *= 2;
            }
          }
          else {
            WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
            // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
            history.RemoveAt(history.Count - 1);
            break;
          }
        }
      }
    }

    WriteLine("\n[INFO] Chat beendet. Räume temporäre Dateien im Cloud Storage auf...");
    await CleanupGcsBucketAsync();
  }

  private async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...") {
    bool delayCanceled = false;
    ConsoleCancelEventHandler cancelHandler = (sender, e) => {
      e.Cancel = true;
      delayCanceled = true;
    };
    Console.CancelKeyPress += cancelHandler;

    try {
      int delaySteps = seconds * 10;
      for (int i = 0; i < delaySteps; i++) {
        if (delayCanceled) return false;
        await Task.Delay(100);
        if (!Console.IsInputRedirected && Console.KeyAvailable) {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
          WriteLine($"\n[AI-Model] {message}");
        }
      }
      return true;
    }
    finally {
      Console.CancelKeyPress -= cancelHandler;
    }
  }

  private async Task<string> PromptWithCommandsAsync(string promptMessage) {
    while (true) {
      Write(promptMessage);
      string? input = ReadLine()?.Trim();
      if (string.IsNullOrWhiteSpace(input)) continue;

      string normalizedInput = input.TrimStart('/');

      if (normalizedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) {
        return "__EXIT__";
      }

      if (System.Text.RegularExpressions.Regex.IsMatch(normalizedInput, @"^change[- ]?key\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
        HandleChangeKey(normalizedInput);
        return "__CHANGED_KEY__";
      }
      return input; // Return the non-command input
    }
  }

  private void ShowCommands() {
    WriteLine("\nBefehle:");
    WriteLine("  help / commands           -> Zeigt diese Befehlsübersicht erneut an");
    WriteLine("  exit / quit               -> Beendet den Chat");
    WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
    WriteLine("  attach datei1, datei2 | Frage  -> Hängt Dateien an und stellt eine Frage dazu.");
    WriteLine("                             (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
    WriteLine("  set temp [wert]           -> Ändert die Temperatur für die nächste Antwort (z.B. set temp 0.5)");
    WriteLine("  set tokens [wert]         -> Ändert das MaxOutputTokens-Limit dynamisch (z.B. set tokens 8192)");
    WriteLine("  change-key [1-3]          -> Wechselt das API-Key Profil dynamisch und speichert die Wahl (z.B. change-key 2)");
  }

  /// <summary>
  /// Verarbeitet alle eingebauten /- oder Kommando-Befehle, um die Hauptschleife sauber zu halten.
  /// Returns true, wenn der Input ein Befehl war und verarbeitet wurde.
  /// </summary>
  private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText) {
    string normalizedInput = input.TrimStart('/');

    if (normalizedInput.Equals("help", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("commands", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("show commands", StringComparison.OrdinalIgnoreCase)) {
      ShowCommands();
      return true;
    }

    if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("reset", StringComparison.OrdinalIgnoreCase)) {
      history.Clear();
      history.AddRange(initialHistory);
      WriteLine("\n[INFO] Gedächtnis gelöscht! Gemini startet komplett frisch.");
      return true;
    }

    if (normalizedInput.StartsWith("set temp ", StringComparison.OrdinalIgnoreCase)) {
      string tempValueStr = normalizedInput.Substring(9).Trim();
      if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f) {
        AIParams.Temperature = newTemp;
        WriteLine($"[INFO] Temperatur für die nächste(n) Antwort(en) auf {AIParams.Temperature:F1} gesetzt.");
      }
      else {
        WriteLine($"[Fehler] Ungültiger Temperaturwert '{tempValueStr}'. Bitte eine Zahl zwischen 0.0 und 2.0 angeben.");
      }
      return true;
    }

    if (normalizedInput.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase)) {
      string tokenValueStr = normalizedInput.Substring(11).Trim();
      if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1) {
        AIParams.MaxOutputTokens = newTokens;
        WriteLine($"[INFO] MaxOutputTokens für die nächste(n) Antwort(en) auf {AIParams.MaxOutputTokens} gesetzt.");
      }
      else {
        WriteLine($"[Fehler] Ungültiger Token-Wert '{tokenValueStr}'. Bitte eine positive ganze Zahl angeben.");
      }
      return true;
    }

    if (System.Text.RegularExpressions.Regex.IsMatch(normalizedInput, @"^change[- ]?key\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
      HandleChangeKey(normalizedInput);
      return true;
    }

    if (normalizedInput.StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) {
      var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(normalizedInput);

      if (!success) return true; // Handled, but failed. Returning true with empty 'parts' forces the main loop to cleanly skip the turn.

      parts.AddRange(attachmentParts);
      updatePromptText(parsedPrompt);
      return true;
    }

    return false; // Not a built-in command
  }

  /// <summary>
  /// [AI Context] Response streaming & state update.
  /// Side-effects: Mutates 'history' list by appending the assistant's full response. Appends raw text to 'chat_log.md'.
  /// Streamt die Antwort von Gemini asynchron in die Konsole und speichert das Ergebnis in der Historie und einem Logfile.
  /// </summary>
  private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText, string userName) {
    Write($"\n{selectedModel} (Drücke Strg+C zum Abbrechen): ");
    string fullResponse = "";

    int inputTokens = 0;
    int outputTokens = 0;

    // [AI Context] Maps current dynamic AI params to the Request payload.
    // Generierungs-Konfiguration anpassen (Temperatur auf 0 für maximale Präzision bei Transkripten)
    var config = new GenerateContentConfig {
      Temperature = AIParams.Temperature,
      TopP = AIParams.TopP,
      TopK = AIParams.TopK,
      MaxOutputTokens = AIParams.MaxOutputTokens
    };

    // [AI Context] Safely inject Thinking parameters ONLY for supported 2.5 and 3.x models
    // Older models (1.5, robotics) or non-Gemini models (Gemma) will crash if this is included.
    if (selectedModel.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
      if (!string.IsNullOrWhiteSpace(AIParams.ThinkingLevel)) {
        config.ThinkingConfig = new ThinkingConfig { ThinkingLevel = AIParams.ThinkingLevel };
      }
    }
    else if (selectedModel.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) {
      if (AIParams.ThinkingBudget.HasValue) {
        config.ThinkingConfig = new ThinkingConfig { ThinkingBudget = AIParams.ThinkingBudget };
      }
    }

    // Pass the Director's Cut Protocol as an absolute System Instruction
    if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
      config.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }

    bool exceptionCaught = false;
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => {
      e.Cancel = true; // Verhindert das Beenden des Programms
      try { cts.Cancel(); } catch { }
    };
    Console.CancelKeyPress += cancelHandler;

    bool isGenerating = true;
    var inputInterceptorTask = Task.Run(async () => {
      while (isGenerating) {
        if (!Console.IsInputRedirected && Console.KeyAvailable) {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
          WriteLine("\n[AI-Model] Still waiting for the acknowledgment / response. Please wait...");
        }
        await Task.Delay(100);
      }
    });

    try {
      // Streaming aktivieren
      var responseStream = _client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
      await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
        // Fallback-Break: Falls das CancellationToken vom Google SDK ignoriert wird, 
        // brechen wir die Schleife manuell beim nächsten empfangenen Wort ab.
        if (cts.IsCancellationRequested) break;

        string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
        Write(chunkText);
        fullResponse += chunkText;

        if (chunk.UsageMetadata != null) {
          if (chunk.UsageMetadata.PromptTokenCount.HasValue) inputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
          if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) outputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
        }
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled") || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
      exceptionCaught = true;
    }
    finally {
      isGenerating = false;
      await inputInterceptorTask; // Warte kurz, bis der Input-Blocker sauber beendet ist
      Console.CancelKeyPress -= cancelHandler;

      if (outputTokens > 0) {
        _sessionTotalInputTokens += inputTokens;
        _sessionTotalOutputTokens += outputTokens;
        WriteLine($"\n[Request Tokens] Input: {inputTokens} | Output: {outputTokens} (inkl. Thinking Tokens)");
        WriteLine($"[Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");
      }

      if (exceptionCaught || cts.IsCancellationRequested) {
        WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen.");
      }
      else {
        WriteLine();
      }
    }

    // 7. KI-Antwort in die Historie aufnehmen
    if (!string.IsNullOrWhiteSpace(fullResponse)) {
      history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
      await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse, userName, inputTokens, outputTokens);
    }
    else {
      // [AI Context] Falls abgebrochen wurde, bevor die KI etwas gesagt hat, 
      // müssen wir die User-Nachricht entfernen, um "Consecutive User Message"-Errors zu vermeiden.
      history.RemoveAt(history.Count - 1);
    }
  }

  /// <summary>
  /// Fragt den Nutzer, ob eine bestehende History geladen werden soll, 
  /// und baut den entsprechenden /attach Befehl zusammen.
  /// </summary>
  private async Task<string?> GetInitialHistoryCommandAsync(string selectedModel) {
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

    if (notFoundPaths.Any()) {
      WriteLine($"\n[Setup-Warnung] Folgende History-Pfade wurden nicht gefunden:");
      foreach (var path in notFoundPaths) {
        WriteLine($"  - {path}");
      }
    }

    var distinctFiles = allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Verhindert, dass die System Instruction versehentlich als History geladen wird, 
    // falls der Nutzer sie physisch im History-Ordner abgelegt hat.
    if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
      distinctFiles = distinctFiles.Where(f => !string.Equals(f, Path.GetFullPath(SystemInstructionPath), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    if (distinctFiles.Count == 0) {
      return null;
    }

    WriteLine($"\n[Setup] Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
    foreach (var file in distinctFiles) {
      WriteLine($"  - {file}");
    }

    string historyChoice = await PromptWithCommandsAsync("Sollen diese Dateien als History geladen werden? (j/n): ");
    if (historyChoice == "__EXIT__" || historyChoice == "__CHANGED_KEY__") return historyChoice;

    bool loadHistory = historyChoice.Trim().ToLower() == "j";

    if (!loadHistory) return null;

    // Die `historyFiles` enthalten bereits die vollen, absoluten Pfade.
    // Wir können sie direkt verwenden und für den Befehl in Anführungszeichen setzen.
    string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
    return $"attach {fileList} | {string.Format(InitialHistoryPrompt, selectedModel)}";
  }

  private void HandleChangeKey(string input) {
    var match = System.Text.RegularExpressions.Regex.Match(input, @"change[- ]?key\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (match.Success && int.TryParse(match.Groups[1].Value, out int newProfile) && newProfile >= 1 && newProfile <= 3) {
      System.Environment.SetEnvironmentVariable("ACTIVE_GEMINI_PROFILE", newProfile.ToString(), EnvironmentVariableTarget.User);

      string? newApiKey = GoogleAiClientBuilder.ResolveApiKey(newProfile);
      if (!string.IsNullOrEmpty(newApiKey)) {
        _client = GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
        _attachmentHandler.UpdateClient(_client);
        _activeApiProfile = newProfile;
        WriteLine($"  [INFO] API-Key Profil erfolgreich auf {newProfile} gewechselt und dauerhaft in den Windows-Umgebungsvariablen gespeichert!");
      }
      else {
        WriteLine($"[Fehler] Konnte API-Key für Profil {newProfile} nicht finden. Der Wechsel wurde abgebrochen.");
      }
    }
    else {
      WriteLine("[Fehler] Bitte eine gültige Profilnummer (1, 2 oder 3) angeben.");
    }
  }

  /// <summary>
  /// [GCS] Löscht alle Dateien im konfigurierten Google Cloud Storage Bucket.
  /// Wird beim Start (für Dateileichen) und beim Beenden (für aktuelle Uploads) aufgerufen.
  /// </summary>
  private async Task CleanupGcsBucketAsync() {
    if (string.IsNullOrWhiteSpace(GcsBucketName) || GcsBucketName == "DEIN_BUCKET_NAME_HIER_EINTRAGEN") return;

    if (IsAiStudio) return; // Prevent free-tier from pinging GCS

    try {
      var storageClient = await StorageClient.CreateAsync();
      WriteLine($"  [GCS] Prüfe Bucket '{GcsBucketName}' auf alte/temporäre Dateien...");
      var objects = storageClient.ListObjectsAsync(GcsBucketName);
      int count = 0;
      await foreach (var obj in objects) {
        await storageClient.DeleteObjectAsync(GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0) {
        WriteLine($"  [GCS] {count} Datei(en) erfolgreich gelöscht.");
      }
    }
    catch (Exception ex) {
      WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
      WriteLine($"Originaler Fehlertext: {ex.Message}");

      if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.Sockets.SocketException ||
          ex.Message.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains("host is known", StringComparison.OrdinalIgnoreCase)) {
        WriteLine($"  [GCS Warnung] Netzwerkfehler beim Bereinigen des Buckets '{GcsBucketName}'. Möglicherweise sind Sie nicht mit dem Internet verbunden! Originalfehler: {ex.Message}");
      }
      else {
        WriteLine($"  [GCS Warnung] Fehler beim Bereinigen des Buckets: {ex.Message}");
      }
    }
  }
}