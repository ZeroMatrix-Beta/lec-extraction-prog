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
using AiInteraction;
using static System.Console;

namespace AiInteraction.GoogleAIStudio;

/// <summary>
/// [AI Context] Localized generation parameters for the Google AI Studio session.
/// Dictates the deterministic vs. creative output distribution of the LLM.
/// </summary>
public class GoogleAIStudioAIConfig
{
  // [AI Context] Temperature (0.0 - 2.0). 0.0 = purely deterministic (best for strict code/math/transcripts). 1.0+ = highly creative (risk of hallucinations).
  public float Temperature { get; set; } = 0.1f;

  // [AI Context] TopP (Nucleus Sampling). 0.0 - 1.0. Lower values restrict vocabulary to the most probable tokens, cutting off the "long tail" of creative/random words.
  public float TopP { get; set; } = 0.9f;

  // [AI Context] TopK. Limits the vocabulary to the top K most likely next tokens. TopK=1 is greedy decoding (perfect for LaTeX generation).
  public int TopK { get; set; } = 10;

  // [AI Context] Hard cutoff limit for output generation. Does NOT affect verbosity, only truncates if exceeded. Set to maximum (65535) for large LaTeX scripts.
  public int MaxOutputTokens { get; set; } = 65535;

  // [AI Context] "Thinking" params introduced for the latest Gemini 2.5 and 3.x models. Strictly required for the 2.5 series.
  public int? ThinkingBudget { get; set; } = 4096;

  // [AI Context] Controls the internal reasoning time for the Gemini 3.x series (e.g., MINIMAL, LOW, MEDIUM, HIGH).
  public string? ThinkingLevel { get; set; } = "HIGH";
}

/// <summary>
/// [AI Context] DTO for Google AI Studio specific session configurations.
/// Separated from VertexAI to prevent accidental contamination of free-tier and enterprise logic.
/// </summary>
public class GoogleAIStudioConfig
{
  // [AI Context] Selects the environment variable API key profile to use (1-3).
  public int ActiveApiProfile { get; set; } = int.TryParse(System.Environment.GetEnvironmentVariable("ACTIVE_GEMINI_PROFILE", EnvironmentVariableTarget.User), out int val) ? val : 1;
  public string UploadFolder { get; set; } = @"D:\gemin-upload-folder";
  public string HistoryFolder { get; set; } = @"D:\gemini-chat-history";
  public string LogFolder { get; set; } = @"D:\gemini-logs";

  // [AI Context] Unused in AI Studio free tier, but retained for interface compatibility with the AttachmentHandler if needed.
  public string GcsBucketName { get; set; } = "biran-linalg-source-material";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";

  // [AI Context] Defines fallback directories for the AttachmentHandler's ResolveFilePath method.
  public string[] IncludePaths { get; set; } = new[] {
    @"D:\lecture-videos\d-und-a/",
    @"D:\lecture-videos\d-und-a/new"
  };

  // [AI Context] Nested generation config explicitly for AI Studio models.
  public GoogleAIStudioAIConfig AI { get; set; } = new GoogleAIStudioAIConfig();
}

/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for the conversational AI interface.
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots. Hier werden deine Eingaben gelesen, an Google gesendet und die Antworten in der Konsole ausgegeben.
/// </summary>
public class GoogleAIStudioChatSession
{
  // [AI Context] Global state for file resolution. 
  // UploadFolderPath is the base dir for relative paths. HistoryFolderPath is an absolute path.
  // Konfigurierbarer Basis-Pfad für deine Uploads. 
  // Z.B.: @"C:\Users\miche\programming\lec-extraction-prog\uploads"
  private readonly string UploadFolderPath;

  // Absoluter Pfad zum Ordner für die automatisch zu ladende History.
  // Z.B.: @"C:\Users\miche\programming\lec-extraction-prog\history"
  private readonly string HistoryFolderPath;

  // Standard-Nachricht, die gesendet wird, wenn die History geladen wird.
  private string InitialHistoryPrompt = "Hier ist das Material aus meiner History. Bitte lies es sorgfältig und warte dann auf meine nächsten Anweisungen.";

  // [GCS] Der Name deines Google Cloud Storage Buckets
  // Z.B.: "en-linalg-biran-gemini-videos"
  private readonly string GcsBucketName;

  // [Log-Ordner] Status für den aktuellen Programmablauf
  private readonly string LogFolderPath;
  private readonly string SystemInstructionPath;
  private string? _systemInstructionText;
  private GoogleAIStudioAIConfig AIParams;
  private readonly bool IsAiStudio;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private Client _client;
  private int _activeApiProfile;

  // [AI Context] Constructor injects config dependencies to isolate state.
  public GoogleAIStudioChatSession(Client client, GoogleAIStudioConfig config, SessionLogger logger, AttachmentHandler attachmentHandler, bool isAiStudio)
  {
    _client = client;
    _sessionLogger = logger;
    _attachmentHandler = attachmentHandler;
    IsAiStudio = isAiStudio;
    UploadFolderPath = config.UploadFolder;
    HistoryFolderPath = config.HistoryFolder;
    LogFolderPath = config.LogFolder;
    GcsBucketName = config.GcsBucketName;
    SystemInstructionPath = config.SystemInstructionPath;
    _activeApiProfile = config.ActiveApiProfile;

    // [AI Context] Creates a localized deep copy of AI parameters.
    // [Human] Kopiert die Standard-Werte, damit wir sie später mit "/set temp" im Chat verändern können, ohne das Original zu überschreiben.
    // Wir legen eine lokale Kopie an, damit /set Befehle nur diese Sitzung modifizieren
    AIParams = new GoogleAIStudioAIConfig
    {
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
  public async Task StartAsync()
  {
    string selectedModel = SelectModel();

    // 3b. Bucket beim Start aufräumen (falls von einem vorherigen Absturz noch Videos übrig sind)
    await CleanupGcsBucketAsync();

    // [AI Context] Implements session persistence by isolating text/LaTeX outputs in discrete timestamped directories.
    // [Human] Erstellt für jede neue Chat-Sitzung einen eigenen Ordner, damit nichts aus Versehen überschrieben wird.
    // 3c. Session Log-Ordner ermitteln und erstellen (folder-1, folder-2...)
    _sessionLogger.InitializeSession();

    // [AI Context] Load System Instructions (Persona & Rules) into memory.
    bool loadedSysPrompt = false;
    Write($"\n[Setup] System Instruction laden? Pfad: '{SystemInstructionPath}' (j/n): ");
    if (ReadLine()?.Trim().ToLower() == "j")
    {
      if (!string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath))
      {
        _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
        WriteLine($"  [INFO] System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
        loadedSysPrompt = true;
      }
      else
      {
        WriteLine($"  [WARNUNG] System-Prompt-Datei nicht gefunden: {SystemInstructionPath}");
      }
    }
    else
    {
      WriteLine("  [INFO] System Instruction wird ignoriert.");
    }

    string? initialInput = GetInitialHistoryCommand();
    bool loadedHistory = initialInput != null;

    _sessionLogger.SetSessionMetadata(loadedSysPrompt, loadedHistory);
    await _sessionLogger.LogSessionSetupAsync();

    // 4. Starte die Haupt-Chat-Schleife
    await RunChatSessionAsync(selectedModel, initialInput);
  }

  /// <summary>
  /// [AI Context] Interactive console menu for initial model selection. Returns the specific model ID string.
  /// RULE: If you add, modify, or remove a model in the switch expression below, you MUST synchronously update the WriteLine menu text here!
  /// The UI representation and the underlying switch logic must ALWAYS perfectly mirror each other.
  /// [Human] Das Startmenü in der Konsole. Wenn du neue Modelle hinzufügst, musst du sie exakt hier eintragen.
  /// </summary>
  private string SelectModel()
  {
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
    Write("Auswahl (1-11) [Standard: 4]: ");

    string? choice = ReadLine()?.Trim();
    return choice switch
    {
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
  private async Task RunChatSessionAsync(string selectedModel, string? initialInput)
  {
    var history = new List<Content>();

    // [AI Context] Cache initial state to allow memory resets without restarting the runtime.
    // [Human] Speichert den Zustand nach dem ersten Laden ab. So funktioniert der "clear" Befehl!
    var initialHistory = new List<Content>(history); // Den Startzustand merken
    string userName = "AI Studio User";

    WriteLine($"\n--- Chat gestartet ({selectedModel} | API Profil: {_activeApiProfile}) ---");
    ShowCommands();

    while (true)
    {
      string? input;
      if (initialInput != null)
      {
        // [AI Context] Automatically executes the history attachment command on the first loop iteration without requiring user interaction.
        input = initialInput;
        WriteLine($"\n{userName}: {input}");
        initialInput = null; // Nur beim allerersten Durchlauf verwenden
      }
      else
      {
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
      if (isCommandHandled)
      {
        // The only exception is the 'attach' command, which modifies our parts/prompt and STILL wants to talk to Gemini
        if (!input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        // If 'attach' failed (e.g., file not found), 'parts' will be empty and we skip the turn
        if (parts.Count == 0) continue;
      }

      // 6. Text-Prompt anhängen und an die Historie übergeben
      if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
      else if (parts.Count == 0) continue;

      history.Add(new Content { Role = "user", Parts = parts });

      try
      {
        // [AI Context] Hands off to streaming handler. Mutates 'history' internally.
        await StreamGeminiResponseAsync(selectedModel, history, input, promptText, userName);
      }
      catch (Exception ex)
      {
        // [AI Context] RULE: Always include the original exception message (ex.Message or ex.ToString()) in error outputs to aid debugging.
        WriteLine($"\nHoppla, da gab es einen Fehler: {ex.Message}");
        // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
        history.RemoveAt(history.Count - 1);
      }
    }

    WriteLine("\n[INFO] Chat beendet. Räume temporäre Dateien im Cloud Storage auf...");
    await CleanupGcsBucketAsync();
  }

  private void ShowCommands()
  {
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
  private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText)
  {
    if (input.Equals("help", StringComparison.OrdinalIgnoreCase) || input.Equals("commands", StringComparison.OrdinalIgnoreCase) || input.Equals("show commands", StringComparison.OrdinalIgnoreCase))
    {
      ShowCommands();
      return true;
    }

    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase) || input.Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
      history.Clear();
      history.AddRange(initialHistory);
      WriteLine("\n[INFO] Gedächtnis gelöscht! Gemini startet komplett frisch.");
      return true;
    }

    if (input.StartsWith("set temp ", StringComparison.OrdinalIgnoreCase))
    {
      string tempValueStr = input.Substring(9).Trim();
      if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f)
      {
        AIParams.Temperature = newTemp;
        WriteLine($"[INFO] Temperatur für die nächste(n) Antwort(en) auf {AIParams.Temperature:F1} gesetzt.");
      }
      else
      {
        WriteLine($"[Fehler] Ungültiger Temperaturwert '{tempValueStr}'. Bitte eine Zahl zwischen 0.0 und 2.0 angeben.");
      }
      return true;
    }

    if (input.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase))
    {
      string tokenValueStr = input.Substring(11).Trim();
      if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1)
      {
        AIParams.MaxOutputTokens = newTokens;
        WriteLine($"[INFO] MaxOutputTokens für die nächste(n) Antwort(en) auf {AIParams.MaxOutputTokens} gesetzt.");
      }
      else
      {
        WriteLine($"[Fehler] Ungültiger Token-Wert '{tokenValueStr}'. Bitte eine positive ganze Zahl angeben.");
      }
      return true;
    }

    if (input.StartsWith("change-key ", StringComparison.OrdinalIgnoreCase))
    {
      string keyStr = input.Substring(11).Trim();
      if (int.TryParse(keyStr, out int newProfile) && newProfile >= 1 && newProfile <= 3)
      {
        System.Environment.SetEnvironmentVariable("ACTIVE_GEMINI_PROFILE", newProfile.ToString(), EnvironmentVariableTarget.User);

        string? newApiKey = GoogleAiClientBuilder.ResolveApiKey(newProfile);
        if (!string.IsNullOrEmpty(newApiKey))
        {
          _client = GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
          _attachmentHandler.UpdateClient(_client);
          _activeApiProfile = newProfile;
          WriteLine($"[INFO] API-Key Profil erfolgreich auf {newProfile} gewechselt und dauerhaft in den Windows-Umgebungsvariablen gespeichert!");
        }
        else
        {
          WriteLine($"[Fehler] Konnte API-Key für Profil {newProfile} nicht finden. Der Wechsel wurde abgebrochen.");
        }
      }
      else
      {
        WriteLine("[Fehler] Bitte eine gültige Profilnummer (1, 2 oder 3) angeben.");
      }
      return true;
    }

    if (input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase))
    {
      var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(input);

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
  private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText, string userName)
  {
    Write($"\n{selectedModel} (Drücke Strg+C zum Abbrechen): ");
    string fullResponse = "";

    // [AI Context] Maps current dynamic AI params to the Request payload.
    // Generierungs-Konfiguration anpassen (Temperatur auf 0 für maximale Präzision bei Transkripten)
    var config = new GenerateContentConfig
    {
      Temperature = AIParams.Temperature,
      TopP = AIParams.TopP,
      TopK = AIParams.TopK,
      MaxOutputTokens = AIParams.MaxOutputTokens
    };

    // [AI Context] Safely inject Thinking parameters ONLY for supported 2.5 and 3.x models
    // Older models (1.5, robotics) or non-Gemini models (Gemma) will crash if this is included.
    if (selectedModel.Contains("gemini-3", StringComparison.OrdinalIgnoreCase))
    {
      if (!string.IsNullOrWhiteSpace(AIParams.ThinkingLevel))
      {
        config.ThinkingConfig = new ThinkingConfig { ThinkingLevel = AIParams.ThinkingLevel };
      }
    }
    else if (selectedModel.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase))
    {
      if (AIParams.ThinkingBudget.HasValue)
      {
        config.ThinkingConfig = new ThinkingConfig { ThinkingBudget = AIParams.ThinkingBudget };
      }
    }

    // Pass the Director's Cut Protocol as an absolute System Instruction
    if (!string.IsNullOrWhiteSpace(_systemInstructionText))
    {
      config.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }

    bool exceptionCaught = false;
    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) =>
    {
      e.Cancel = true; // Verhindert das Beenden des Programms
      try { cts.Cancel(); } catch { }
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
      // Streaming aktivieren
      var responseStream = _client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
      await foreach (var chunk in responseStream.WithCancellation(cts.Token))
      {
        // Fallback-Break: Falls das CancellationToken vom Google SDK ignoriert wird, 
        // brechen wir die Schleife manuell beim nächsten empfangenen Wort ab.
        if (cts.IsCancellationRequested) break;

        string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
        Write(chunkText);
        fullResponse += chunkText;
      }
    }
    catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled") || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase))
    {
      exceptionCaught = true;
    }
    finally
    {
      Console.CancelKeyPress -= cancelHandler;
      if (exceptionCaught || cts.IsCancellationRequested)
      {
        WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen.");
      }
      else
      {
        WriteLine();
      }
    }

    // 7. KI-Antwort in die Historie aufnehmen
    if (!string.IsNullOrWhiteSpace(fullResponse))
    {
      history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
      await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse, userName);
    }
    else
    {
      // [AI Context] Falls abgebrochen wurde, bevor die KI etwas gesagt hat, 
      // müssen wir die User-Nachricht entfernen, um "Consecutive User Message"-Errors zu vermeiden.
      history.RemoveAt(history.Count - 1);
    }
  }

  /// <summary>
  /// Fragt den Nutzer, ob eine bestehende History geladen werden soll, 
  /// und baut den entsprechenden /attach Befehl zusammen.
  /// </summary>
  private string? GetInitialHistoryCommand()
  {
    if (string.IsNullOrWhiteSpace(HistoryFolderPath) || !Directory.Exists(HistoryFolderPath))
    {
      return null;
    }

    string[] historyFiles = Directory.GetFiles(HistoryFolderPath, "*.*", SearchOption.AllDirectories);

    // Verhindert, dass die System Instruction versehentlich als History geladen wird, 
    // falls der Nutzer sie physisch im History-Ordner abgelegt hat.
    if (!string.IsNullOrWhiteSpace(SystemInstructionPath))
    {
      historyFiles = historyFiles.Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(SystemInstructionPath), StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    if (historyFiles.Length == 0)
    {
      return null;
    }

    WriteLine($"\n[Setup] Folgende History-Dateien wurden in '{HistoryFolderPath}' gefunden:");
    foreach (var file in historyFiles)
    {
      string relativePath = Path.GetRelativePath(HistoryFolderPath, file);
      WriteLine($"  - {relativePath}");
    }

    Write("Sollen diese Dateien als History geladen werden? (j/n): ");
    bool loadHistory = ReadLine()?.Trim().ToLower() == "j";

    if (!loadHistory) return null;

    // Die `historyFiles` enthalten bereits die vollen, absoluten Pfade.
    // Wir können sie direkt verwenden und für den Befehl in Anführungszeichen setzen.
    string fileList = string.Join(", ", historyFiles.Select(p => $"\"{p}\""));
    return $"attach {fileList} | {InitialHistoryPrompt}";
  }

  /// <summary>
  /// [GCS] Löscht alle Dateien im konfigurierten Google Cloud Storage Bucket.
  /// Wird beim Start (für Dateileichen) und beim Beenden (für aktuelle Uploads) aufgerufen.
  /// </summary>
  private async Task CleanupGcsBucketAsync()
  {
    if (string.IsNullOrWhiteSpace(GcsBucketName) || GcsBucketName == "DEIN_BUCKET_NAME_HIER_EINTRAGEN") return;

    if (IsAiStudio) return; // Prevent free-tier from pinging GCS

    try
    {
      var storageClient = await StorageClient.CreateAsync();
      WriteLine($"  [GCS] Prüfe Bucket '{GcsBucketName}' auf alte/temporäre Dateien...");
      var objects = storageClient.ListObjectsAsync(GcsBucketName);
      int count = 0;
      await foreach (var obj in objects)
      {
        await storageClient.DeleteObjectAsync(GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0)
      {
        WriteLine($"  [GCS] {count} Datei(en) erfolgreich gelöscht.");
      }
    }
    catch (Exception ex)
    {
      // [AI Context] RULE: Always include the original exception message (ex.Message or ex.ToString()) in error outputs to aid debugging.
      if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.Sockets.SocketException ||
          ex.Message.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
          ex.Message.Contains("host is known", StringComparison.OrdinalIgnoreCase))
      {
        WriteLine($"  [GCS Warnung] Netzwerkfehler beim Bereinigen des Buckets '{GcsBucketName}'. Möglicherweise sind Sie nicht mit dem Internet verbunden! Originalfehler: {ex.Message}");
      }
      else
      {
        WriteLine($"  [GCS Warnung] Fehler beim Bereinigen des Buckets: {ex.Message}");
      }
    }
  }
}