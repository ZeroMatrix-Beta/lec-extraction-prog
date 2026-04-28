using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using Config;
using AiInteraction;
using static System.Console;

namespace AiInteraction.Vertex;

/// <summary>
/// [AI Context] Localized generation parameters for the Vertex AI Enterprise session.
/// Ensures Vertex workloads can be tuned independently of AI Studio workloads.
/// </summary>
public class VertexAIConfig {
  // [AI Context] Temperature (0.0 - 2.0). 0.0 = purely deterministic.
  public float Temperature { get; set; } = 0.1f;
  // [AI Context] TopP (Nucleus Sampling). 0.0 - 1.0.
  public float TopP { get; set; } = 0.9f;
  // [AI Context] TopK. Limits the vocabulary. TopK=1 is greedy decoding.
  public int TopK { get; set; } = 10;
  // [AI Context] Hard cutoff limit for output generation.
  public int MaxOutputTokens { get; set; } = 65535;
  // [AI Context] Explicitly maps to Vertex Gemini 2.5 thinking budget.
  public int? ThinkingBudget { get; set; } = 4096;
  // [AI Context] Explicitly maps to Vertex Gemini 3.x reasoning effort.
  public string? ThinkingLevel { get; set; } = "MEDIUM";
}

/// <summary>
/// [AI Context] DTO for Vertex AI specific configurations.
/// Requires valid GCP ProjectId and Location for IAM authentication.
/// </summary>
public class VertexAiConfig {
  // [AI Context] The Google Cloud Platform (GCP) Project ID associated with the billing account.
  public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
  // [AI Context] Region for Vertex AI execution. Must support the requested Gemini models.
  public string Location { get; set; } = "global"; // Changed from "us-central1" to "global"
  public string UploadFolder { get; set; } = @"D:\gemini-upload-folder";
  public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
  public string LogFolder { get; set; } = @"D:\gemini-logs";
  // [AI Context] Crucial: The designated Google Cloud Storage bucket used exclusively for Vertex AI multimodal attachments.
  public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string[] IncludePaths { get; set; } = new[] {
    @"D:\lecture-videos\d-und-a/",
    @"D:\lecture-videos\d-und-a/new"
  };
  public VertexAIConfig AI { get; set; } = new VertexAIConfig();
}

/// <summary>
/// [AI Context] Core REPL manager specifically for Google Cloud Vertex AI interactions.
/// This completely isolates the enterprise execution context from the developer AI Studio context.
/// </summary>
public class VertexAiChatSession {
  private readonly string UploadFolderPath;
  private readonly string[] HistoryPreloadPaths;
  private string InitialHistoryPrompt = "Hier ist das Material aus meiner History. Bitte lies es sorgfältig durch. Bestätige mir den Erhalt ausnahmslos mit exakt folgendem Text: '[SYSTEM] Material [...] received and analyzed. I am standing by for your instructions.' Warte danach auf meine nächsten Anweisungen.";
  private readonly string GcsBucketName;
  private readonly string LogFolderPath;
  private readonly string SystemInstructionPath;
  private string? _systemInstructionText;
  private VertexAIConfig AIParams;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private readonly Client _client;

  // [AI Context] Constructor receives injected dependencies. The 'client' here is strictly a Vertex-configured client (GoogleAiClientBuilder.BuildVertexClient).
  public VertexAiChatSession(Client client, VertexAiConfig config, SessionLogger logger, AttachmentHandler attachmentHandler) {
    _client = client;
    _sessionLogger = logger;
    _attachmentHandler = attachmentHandler;
    UploadFolderPath = config.UploadFolder;
    HistoryPreloadPaths = config.HistoryPreloadPaths;
    LogFolderPath = config.LogFolder;
    GcsBucketName = config.GcsBucketName;
    SystemInstructionPath = config.SystemInstructionPath;

    AIParams = new VertexAIConfig {
      Temperature = config.AI.Temperature,
      TopP = config.AI.TopP,
      TopK = config.AI.TopK,
      MaxOutputTokens = config.AI.MaxOutputTokens
    };
  }

  public async Task StartAsync() {
    while (true) {
      string selectedModel = await SelectModelAsync();
      if (selectedModel == "__EXIT__") return;

      WriteLine("\n[System] Initiating Vertex AI Enterprise Session...");

      // ALWAYS clean up the bucket completely before starting a session (crash recovery)
      await ForcePurgeGcsBucketAsync();

      _sessionLogger.InitializeSession();

      bool loadedSysPrompt = false;
      string sysPromptChoice = await PromptWithCommandsAsync($"\n[Setup] System Instruction laden? Pfad: '{SystemInstructionPath}' (j/n): ");
      if (sysPromptChoice == "__EXIT__") return;

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

      string? initialInput = await GetInitialHistoryCommandAsync();
      if (initialInput == "__EXIT__") return;

      bool loadedHistory = initialInput != null;

      _sessionLogger.SetSessionMetadata(loadedSysPrompt, loadedHistory);
      await _sessionLogger.LogSessionSetupAsync();

      await RunChatSessionAsync(selectedModel, initialInput);
      break; // Session beendet, zurück zu Program.cs
    }
  }

  /// <summary>
  /// [AI Context] Interactive console menu for initial model selection. Returns the specific model ID string.
  /// RULE: If you add, modify, or remove a model in the switch expression below, you MUST synchronously update the WriteLine menu text here!
  /// The UI representation and the underlying switch logic must ALWAYS perfectly mirror each other.
  /// [Human] Das Startmenü in der Konsole. Wenn du neue Modelle hinzufügst, musst du sie exakt hier eintragen.
  /// </summary>
  private async Task<string> SelectModelAsync() {
    WriteLine("\n=== Model Selection (Vertex AI) ===");
    WriteLine("Wähle ein Modell:");
    WriteLine(" 1) gemini-3.1-flash-lite-preview || Input:  $0.25 (text / image / video), $0.50 (audio)");
    WriteLine("                                  || Output: $1.50 (<== Claimed to be the most cost-efficient, optimized)");
    WriteLine(" 2) gemini-3-flash-preview        || Input:  $0.50 (text / image / video), $1.00 (audio)");
    WriteLine("                                  || Output: $3.0");
    WriteLine(" 3) gemini-3.1-pro-preview        || Input:  $2.00, prompts <= 200k tokens, $4.00, prompts > 200k tokens");
    WriteLine("                                  || Output: $12.00, prompts <= 200k tokens, $18.00, prompts > 200k");
    WriteLine(" 4) gemini-2.5-flash              || Input:  $0.30  (text / image / video) $1.00 (audio). ");
    WriteLine("                                  || Output: $2.50");
    WriteLine(" 5) gemini-2.5-flash-lite         || Input:  $0.10  (text / image / video). ");
    WriteLine("                                  || Output: $0.40");
    WriteLine(" 6) gemini-2.5-pro                || Input:  $1.25, prompts <= 200k tokens, $2.50, prompts > 200k tokens.");
    WriteLine("                                  || Output: $10.00, prompts <= 200k tokens $15.00, prompts > 200k");
    WriteLine(" 7) gemma-3-27b-it                || (Open Model, 27B Parameter)");
    WriteLine(" 8) gemini-1.5-flash              || (Schnelles Fallback für Video/Audio)");
    WriteLine(" 9) gemini-1.5-pro                || (Mächtiges Fallback für Video/Audio)");
    WriteLine("10) gemini-robotics-er-1.5-preview|| (Free Tier, Multimodal)");
    WriteLine("11) gemini-robotics-er-1.6-preview|| (Neues Robotics Modell)");

    string choice = await PromptWithCommandsAsync("Auswahl (1-11) [Standard: 4]: ");
    if (choice == "__EXIT__") return choice;

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

  private async Task RunChatSessionAsync(string selectedModel, string? initialInput) {
    var history = new List<Content>();
    var initialHistory = new List<Content>(history);
    string userName = "Vertex AI User";

    WriteLine($"\n--- Vertex Chat gestartet ({selectedModel}) ---");
    ShowCommands();

    while (true) {
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
      if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

      var parts = new List<Part>();
      string promptText = input;

      bool isCommandHandled = await TryHandleBuiltInCommandsAsync(input, history, initialHistory, parts, newPrompt => promptText = newPrompt);

      if (isCommandHandled) {
        if (!input.TrimStart('/').StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) continue;
        if (parts.Count == 0) continue;
      }

      if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
      else if (parts.Count == 0) continue;

      history.Add(new Content { Role = "user", Parts = parts });

      int backoff = 30;
      int maxRetries = 5;

      for (int attempt = 1; attempt <= maxRetries; attempt++) {
        try {
          await StreamGeminiResponseAsync(selectedModel, history, input, promptText, userName);
          break;
        }
        catch (Exception ex) {
          WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
          WriteLine($"Originaler Fehlertext: {ex.Message}");

          bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);

          if (ex.Message.Contains("Service agents are being provisioned", StringComparison.OrdinalIgnoreCase)) {
            WriteLine($"\n[Vertex Info]: Google Cloud richtet gerade im Hintergrund die Zugriffsrechte (Service Agents) für deinen Bucket ein. Das passiert meistens nur beim allerersten Mal im Projekt. Bitte warte einfach 2-3 Minuten und versuche die Anfrage dann erneut! Originalfehler: {ex.Message}");
            history.RemoveAt(history.Count - 1);
            break;
          }
          else if (attempt < maxRetries && isOverloaded) {
            var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
            if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
              int waitTime = serverSuggestedDelay + 2;
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
            WriteLine($"\n[Vertex Error]: {ex.Message}");
            history.RemoveAt(history.Count - 1);
            break;
          }
        }
      }
    }

    WriteLine("\n[INFO] Chat beendet. Räume GCS Bucket komplett auf...");

    // ALWAYS clean up the bucket at the end of the session to save costs.
    await ForcePurgeGcsBucketAsync();
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

      // Placeholder for future setup-time commands in Vertex mode
      // if (input.StartsWith("some-vertex-command")) { ...; continue; }

      return input; // Return the non-command input
    }
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
          WriteLine($"\n[System] {message}");
        }
      }
      return true;
    }
    finally {
      Console.CancelKeyPress -= cancelHandler;
    }
  }

  private void ShowCommands() {
    WriteLine("\nBefehle:");
    WriteLine("  help / commands           -> Zeigt diese Befehlsübersicht erneut an");
    WriteLine("  exit / quit               -> Beendet den Chat");
    WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
    WriteLine("  attach datei1, datei2 | Frage  -> Hängt Dateien an und stellt eine Frage dazu.");
    WriteLine("  set temp [wert]           -> Ändert die Temperatur dynamisch");
    WriteLine("  set tokens [wert]         -> Ändert das MaxOutputTokens-Limit dynamisch");
  }

  private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText) {
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
      string tempValueStr = normalizedInput.Substring(9).Trim();
      if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f) {
        AIParams.Temperature = newTemp;
        WriteLine($"[INFO] Temperatur auf {AIParams.Temperature:F1} gesetzt.");
      }
      return true;
    }

    if (normalizedInput.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase)) {
      string tokenValueStr = normalizedInput.Substring(11).Trim();
      if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1) {
        AIParams.MaxOutputTokens = newTokens;
        WriteLine($"[INFO] MaxOutputTokens auf {AIParams.MaxOutputTokens} gesetzt.");
      }
      return true;
    }

    if (normalizedInput.StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) {
      var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(normalizedInput);

      if (!success) return true;

      parts.AddRange(attachmentParts);
      updatePromptText(parsedPrompt);
      return true;
    }

    return false;
  }

  private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText, string userName) {
    Write($"\n[Vertex] {selectedModel} (Drücke Strg+C zum Abbrechen): ");
    string fullResponse = "";

    var config = new GenerateContentConfig {
      Temperature = AIParams.Temperature,
      TopP = AIParams.TopP,
      TopK = AIParams.TopK,
      MaxOutputTokens = AIParams.MaxOutputTokens
    };

    // [AI Context] Safely inject Thinking parameters ONLY for supported 2.5 and 3.x models
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

    if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
      config.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }

    bool exceptionCaught = false;
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => {
      e.Cancel = true; // Verhindert das harte Beenden
      try { cts.Cancel(); } catch { }
    };
    Console.CancelKeyPress += cancelHandler;

    bool isGenerating = true;
    var inputInterceptorTask = Task.Run(async () => {
      while (isGenerating) {
        if (!Console.IsInputRedirected && Console.KeyAvailable) {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
          WriteLine("\n[System] Still waiting for the acknowledgment / response. Please wait...");
        }
        await Task.Delay(100);
      }
    });

    try {
      var responseStream = _client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
      await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
        // Fallback-Break: Falls das CancellationToken vom Google SDK ignoriert wird, 
        // brechen wir die Schleife manuell beim nächsten empfangenen Wort ab.
        if (cts.IsCancellationRequested) break;

        string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
        Write(chunkText);
        fullResponse += chunkText;
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled") || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
      exceptionCaught = true;
    }
    finally {
      isGenerating = false;
      await inputInterceptorTask;
      Console.CancelKeyPress -= cancelHandler;
      if (exceptionCaught || cts.IsCancellationRequested) {
        WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen.");
      }
      else {
        WriteLine();
      }
    }

    if (!string.IsNullOrWhiteSpace(fullResponse)) {
      history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
      await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse, userName);
    }
    else {
      history.RemoveAt(history.Count - 1);
    }
  }

  private async Task<string?> GetInitialHistoryCommandAsync() {
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

    // Verhindert, dass die System Instruction versehentlich als History geladen wird
    if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
      distinctFiles = distinctFiles.Where(f => !string.Equals(f, Path.GetFullPath(SystemInstructionPath), StringComparison.OrdinalIgnoreCase)).ToList();
    }

    if (distinctFiles.Count == 0) return null;

    WriteLine($"\n[Setup] Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
    foreach (var file in distinctFiles) {
      WriteLine($"  - {file}");
    }

    string historyChoice = await PromptWithCommandsAsync("Sollen diese Dateien als History geladen werden? (j/n): ");
    if (historyChoice == "__EXIT__") return historyChoice;

    bool loadHistory = historyChoice.Trim().ToLower() == "j";

    if (!loadHistory) return null;

    string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
    return $"attach {fileList} | {InitialHistoryPrompt}";
  }

  /// <summary>
  /// Deep cleans the assigned Vertex AI Bucket. Crucial for managing storage costs and cleaning up crashed sessions.
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