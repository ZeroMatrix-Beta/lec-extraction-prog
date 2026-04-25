using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using Config;
using GoogleGenAi;

namespace AiInteraction;

/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for the conversational AI interface.
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots. Hier werden deine Eingaben gelesen, an Google gesendet und die Antworten in der Konsole ausgegeben.
/// </summary>
public class AiChatSession
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
  private AIConfig AIParams;
  private readonly bool IsAiStudio;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private readonly IUserInterface _ui;
  private readonly Client _client;

  // [AI Context] Constructor injects config dependencies to isolate state.
  public AiChatSession(Client client, ChatConfig config, IUserInterface ui, SessionLogger logger, AttachmentHandler attachmentHandler, bool isAiStudio)
  {
    _client = client;
    _ui = ui;
    _sessionLogger = logger;
    _attachmentHandler = attachmentHandler;
    IsAiStudio = isAiStudio;
    UploadFolderPath = config.UploadFolder;
    HistoryFolderPath = config.HistoryFolder;
    LogFolderPath = config.LogFolder;
    GcsBucketName = config.GcsBucketName;
    SystemInstructionPath = config.SystemInstructionPath;

    // [AI Context] Creates a localized deep copy of AI parameters.
    // [Human] Kopiert die Standard-Werte, damit wir sie später mit "/set temp" im Chat verändern können, ohne das Original zu überschreiben.
    // Wir legen eine lokale Kopie an, damit /set Befehle nur diese Sitzung modifizieren
    AIParams = new AIConfig
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
  public async Task StartAsync(string selectedModel)
  {
    // 3b. Bucket beim Start aufräumen (falls von einem vorherigen Absturz noch Videos übrig sind)
    await CleanupGcsBucketAsync();

    // [AI Context] Implements session persistence by isolating text/LaTeX outputs in discrete timestamped directories.
    // [Human] Erstellt für jede neue Chat-Sitzung einen eigenen Ordner, damit nichts aus Versehen überschrieben wird.
    // 3c. Session Log-Ordner ermitteln und erstellen (folder-1, folder-2...)
    _sessionLogger.InitializeSession();

    // [AI Context] Load System Instructions (Persona & Rules) into memory.
    _ui.Write($"\n[Setup] System Instruction laden? Pfad: '{SystemInstructionPath}' (j/n): ");
    if (_ui.ReadLine()?.Trim().ToLower() == "j")
    {
      if (!string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath))
      {
        _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
        _ui.WriteLine($"  [INFO] System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
      }
      else
      {
        _ui.WriteLine($"  [WARNUNG] System-Prompt-Datei nicht gefunden: {SystemInstructionPath}");
      }
    }
    else
    {
      _ui.WriteLine("  [INFO] System Instruction wird ignoriert.");
    }

    string? initialInput = GetInitialHistoryCommand();

    // 4. Starte die Haupt-Chat-Schleife
    await RunChatSessionAsync(selectedModel, initialInput);
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

    _ui.WriteLine($"\n--- Chat gestartet ({selectedModel}) ---");
    _ui.WriteLine("Befehle:");
    _ui.WriteLine("  exit / quit               -> Beendet den Chat");
    _ui.WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
    _ui.WriteLine("  attach datei1, datei2 | Frage  -> Hängt Dateien an und stellt eine Frage dazu.");
    _ui.WriteLine("                             (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
    _ui.WriteLine("  set temp [wert]           -> Ändert die Temperatur für die nächste Antwort (z.B. set temp 0.5)");
    _ui.WriteLine("  set tokens [wert]         -> Ändert das MaxOutputTokens-Limit dynamisch (z.B. set tokens 8192)");

    while (true)
    {
      string? input;
      if (initialInput != null)
      {
        input = initialInput;
        _ui.WriteLine($"\nDu: {input}");
        initialInput = null; // Nur beim allerersten Durchlauf verwenden
      }
      else
      {
        _ui.Write("\nDu: ");
        input = _ui.ReadLine();
      }

      if (string.IsNullOrWhiteSpace(input)) continue;
      if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

      var parts = new List<Part>();
      string promptText = input;

      // Extract command handling to keep the main loop focused purely on the chat flow
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
        await StreamGeminiResponseAsync(selectedModel, history, input, promptText);
      }
      catch (Exception ex)
      {
        _ui.WriteLine($"\nHoppla, da gab es einen Fehler: {ex.Message}");
        // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
        history.RemoveAt(history.Count - 1);
      }
    }

    _ui.WriteLine("\n[INFO] Chat beendet. Räume temporäre Dateien im Cloud Storage auf...");
    await CleanupGcsBucketAsync();
  }

  /// <summary>
  /// Verarbeitet alle eingebauten /- oder Kommando-Befehle, um die Hauptschleife sauber zu halten.
  /// Returns true, wenn der Input ein Befehl war und verarbeitet wurde.
  /// </summary>
  private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText)
  {
    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase) || input.Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
      history.Clear();
      history.AddRange(initialHistory);
      _ui.WriteLine("\n[INFO] Gedächtnis gelöscht! Gemini startet komplett frisch.");
      return true;
    }

    if (input.StartsWith("set temp ", StringComparison.OrdinalIgnoreCase))
    {
      string tempValueStr = input.Substring(9).Trim();
      if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f)
      {
        AIParams.Temperature = newTemp;
        _ui.WriteLine($"[INFO] Temperatur für die nächste(n) Antwort(en) auf {AIParams.Temperature:F1} gesetzt.");
      }
      else
      {
        _ui.WriteLine($"[Fehler] Ungültiger Temperaturwert '{tempValueStr}'. Bitte eine Zahl zwischen 0.0 und 2.0 angeben.");
      }
      return true;
    }

    if (input.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase))
    {
      string tokenValueStr = input.Substring(11).Trim();
      if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1)
      {
        AIParams.MaxOutputTokens = newTokens;
        _ui.WriteLine($"[INFO] MaxOutputTokens für die nächste(n) Antwort(en) auf {AIParams.MaxOutputTokens} gesetzt.");
      }
      else
      {
        _ui.WriteLine($"[Fehler] Ungültiger Token-Wert '{tokenValueStr}'. Bitte eine positive ganze Zahl angeben.");
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
  private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText)
  {
    _ui.Write($"\n{selectedModel}: ");
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

    // Pass the Director's Cut Protocol as an absolute System Instruction
    if (!string.IsNullOrWhiteSpace(_systemInstructionText))
    {
      config.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }

    // Streaming aktivieren
    var responseStream = _client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
    await foreach (var chunk in responseStream)
    {
      string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
      _ui.Write(chunkText);
      fullResponse += chunkText;
    }
    _ui.WriteLine();

    // 7. KI-Antwort in die Historie aufnehmen
    history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });

    // Optional: Verlauf in einer Log-Datei mitprotokollieren
    // Und: Antwort zusätzlich als saubere .tex-Datei im aktuellen Session-Ordner speichern
    await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse);
  }

  /// <summary>
  /// Fragt den Nutzer, ob eine bestehende History geladen werden soll, 
  /// und baut den entsprechenden /attach Befehl zusammen.
  /// </summary>
  private string? GetInitialHistoryCommand()
  {
    _ui.Write($"\n[Setup] History laden? Ordner: '{HistoryFolderPath}' (j/n): ");
    bool loadHistory = _ui.ReadLine()?.Trim().ToLower() == "j";

    if (!loadHistory) return null;

    if (string.IsNullOrWhiteSpace(HistoryFolderPath) || !Directory.Exists(HistoryFolderPath))
    {
      _ui.WriteLine($"  [WARNUNG] Der History-Ordner '{HistoryFolderPath}' wurde nicht gefunden oder ist nicht konfiguriert.");
      return null;
    }

    string[] historyFiles = Directory.GetFiles(HistoryFolderPath);
    if (historyFiles.Length == 0)
    {
      _ui.WriteLine($"  [INFO] Der History-Ordner '{HistoryFolderPath}' ist leer. Nichts zu laden.");
      return null;
    }

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
      _ui.WriteLine($"  [GCS] Prüfe Bucket '{GcsBucketName}' auf alte/temporäre Dateien...");
      var objects = storageClient.ListObjectsAsync(GcsBucketName);
      int count = 0;
      await foreach (var obj in objects)
      {
        await storageClient.DeleteObjectAsync(GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0)
      {
        _ui.WriteLine($"  [GCS] {count} Datei(en) erfolgreich gelöscht.");
      }
    }
    catch (Exception ex)
    {
      _ui.WriteLine($"  [GCS Warnung] Fehler beim Bereinigen des Buckets: {ex.Message}");
    }
  }
}