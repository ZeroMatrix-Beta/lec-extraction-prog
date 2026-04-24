using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;

/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for the conversational AI interface.
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots. Hier werden deine Eingaben gelesen, an Google gesendet und die Antworten in der Konsole ausgegeben.
/// </summary>
public class ChatSession
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
  private string CurrentSessionLogPath = "";
  private string CurrentSessionDateSuffix = "";
  private int ResponseCount = 1;
  private AIConfig AIParams;
  private readonly string[] IncludePaths;
  private readonly bool UseVertexAI;
  private bool IsAiStudio;
  private AttachmentHandler _attachmentHandler;

  // [AI Context] Constructor injects config dependencies to isolate state.
  public ChatSession(ChatConfig config)
  {
    UploadFolderPath = config.UploadFolder;
    HistoryFolderPath = config.HistoryFolder;
    LogFolderPath = config.LogFolder;
    GcsBucketName = config.GcsBucketName;
    IncludePaths = config.IncludePaths ?? Array.Empty<string>();
    UseVertexAI = config.UseVertexAI;

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
    // [AI Context] Setup phase: Load API keys, configure client (custom timeout for large media), and initialize history.
    // 1. API Key laden (wird nur noch als Fallback für den /modelle Befehl genutzt)
    string apiKey = ResolveApiKey() ?? "no-key";

    // 2. Den Client mit einem benutzerdefinierten Timeout erstellen
    // [Human] TIPP: Falls es genau HIER einen Kompilier-Fehler gibt ("HttpOptions nicht gefunden"), 
    // liegt das an einem Update des Google.GenAI SDKs. In ganz neuen Versionen ist der Timeout oft direkt im Konstruktor von 'Client'.
    // [AI Context] Increases HttpClient timeout to 20 minutes to prevent socket closures during large video file polling.
    // Der Standard-Timeout (100s) ist für große Datei-Uploads/Verarbeitung zu kurz.
    var options = new HttpOptions
    {
      // Timeout auf 20 Minuten erhöhen, um die Verarbeitung großer Dateien zu ermöglichen
      Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
    };

    // 3. Client initialisieren: Prüfen ob Free-Tier (AI Studio) oder Vertex AI genutzt werden soll
    Client client;
    IsAiStudio = !UseVertexAI || selectedModel.Contains("gemma", StringComparison.OrdinalIgnoreCase);
    if (IsAiStudio)
    {
      Console.WriteLine("  [INFO] Verbinde mit Google AI Studio (Developer API-Key aktiv)...");
      client = new Client(apiKey: apiKey, httpOptions: options);
    }
    else
    {
      Console.WriteLine("  [INFO] Verbinde mit Google Cloud Vertex AI (Projekt: en-linalg-biran-gemini)...");
      client = new Client(
          vertexAI: true,
          project: "en-linalg-biran-gemini",
          location: "us-central1", // Geändert, da neueste Modelle oft nicht sofort in europe-west6 verfügbar sind
          httpOptions: options
      );
    }

    _attachmentHandler = new AttachmentHandler(client, UploadFolderPath, IncludePaths, IsAiStudio, GcsBucketName);

    // 3b. Bucket beim Start aufräumen (falls von einem vorherigen Absturz noch Videos übrig sind)
    await CleanupGcsBucketAsync();

    // [AI Context] Implements session persistence by isolating text/LaTeX outputs in discrete timestamped directories.
    // [Human] Erstellt für jede neue Chat-Sitzung einen eigenen Ordner, damit nichts aus Versehen überschrieben wird.
    // 3c. Session Log-Ordner ermitteln und erstellen (folder-1, folder-2...)
    CurrentSessionDateSuffix = GetFormattedDateString(DateTime.Now);

    if (!string.IsNullOrWhiteSpace(LogFolderPath))
    {
      if (!Directory.Exists(LogFolderPath))
      {
        Directory.CreateDirectory(LogFolderPath);
      }

      int maxIndex = 0;
      foreach (var dir in Directory.GetDirectories(LogFolderPath))
      {
        string dirName = Path.GetFileName(dir);
        if (dirName.StartsWith("folder-", StringComparison.OrdinalIgnoreCase))
        {
          string[] dirParts = dirName.Split('-');
          if (dirParts.Length >= 2 && int.TryParse(dirParts[1], out int parsedIndex))
          {
            if (parsedIndex > maxIndex) maxIndex = parsedIndex;
          }
        }
      }

      int folderIndex = maxIndex + 1;
      CurrentSessionLogPath = Path.Combine(LogFolderPath, $"folder-{folderIndex}-{CurrentSessionDateSuffix}");
      Directory.CreateDirectory(CurrentSessionLogPath);
    }

    string? initialInput = GetInitialHistoryCommand();

    // 4. Starte die Haupt-Chat-Schleife
    await RunChatSessionAsync(client, selectedModel, apiKey, initialInput);
  }

  // --- Ausgelagerte Methoden ---

  /// <summary>
  /// [AI Context] Main REPL loop. 
  /// Mutates the 'history' list to maintain conversation state. Catches errors to prevent chat state corruption.
  /// Hauptschleife des Chats: Liest kontinuierlich Benutzereingaben, verarbeitet Befehle,
  /// sendet Nachrichten an die Gemini-API und gibt die gestreamten Antworten in der Konsole aus.
  /// </summary>
  private async Task RunChatSessionAsync(Client client, string selectedModel, string apiKey, string? initialInput)
  {
    var history = new List<Content>();

    // [AI Context] Cache initial state to allow memory resets without restarting the runtime.
    // [Human] Speichert den Zustand nach dem ersten Laden ab. So funktioniert der "clear" Befehl!
    var initialHistory = new List<Content>(history); // Den Startzustand merken

    Console.WriteLine($"\n--- Chat gestartet ({selectedModel}) ---");
    Console.WriteLine("Befehle:");
    Console.WriteLine("  exit / quit               -> Beendet den Chat");
    Console.WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
    Console.WriteLine("  attach datei1, datei2 | Frage  -> Hängt Dateien an und stellt eine Frage dazu.");
    Console.WriteLine("                             (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
    Console.WriteLine("  modelle                   -> Zeigt alle Modelle mit Audio-Support an");
    Console.WriteLine("  set temp [wert]           -> Ändert die Temperatur für die nächste Antwort (z.B. set temp 0.5)");
    Console.WriteLine("  set tokens [wert]         -> Ändert das MaxOutputTokens-Limit dynamisch (z.B. set tokens 8192)");

    while (true)
    {
      string? input;
      if (initialInput != null)
      {
        input = initialInput;
        Console.WriteLine($"\nDu: {input}");
        initialInput = null; // Nur beim allerersten Durchlauf verwenden
      }
      else
      {
        Console.Write("\nDu: ");
        input = Console.ReadLine();
      }

      if (string.IsNullOrWhiteSpace(input)) continue;
      if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

      // [AI Context] Purges active conversation state.
      // [Human] Setzt den Chatbot zurück, falls er "verwirrt" ist oder du das Thema komplett wechseln möchtest.
      if (input.ToLower() == "clear" || input.ToLower() == "reset")
      {
        history.Clear();
        history.AddRange(initialHistory); // Startzustand wiederherstellen
        Console.WriteLine("\n[INFO] Gedächtnis gelöscht! Gemini startet komplett frisch.");
        continue;
      }

      var parts = new List<Part>();
      string promptText = input;

      // 5a. Sonderbefehl: Ruft die Google API ab, um alle aktuell verfügbaren Modelle aufzulisten.
      if (input.ToLower() == "modelle")
      {
        await ShowAvailableModelsAsync(apiKey);
        continue;
      }

      // 5c. Temperatur dynamisch anpassen
      if (input.StartsWith("set temp ", StringComparison.OrdinalIgnoreCase))
      {
        string tempValueStr = input.Substring(9).Trim();
        // Use InvariantCulture to ensure '.' is used as the decimal separator
        if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f)
        {
          AIParams.Temperature = newTemp;
          Console.WriteLine($"[INFO] Temperatur für die nächste(n) Antwort(en) auf {AIParams.Temperature:F1} gesetzt.");
        }
        else
        {
          Console.WriteLine($"[Fehler] Ungültiger Temperaturwert '{tempValueStr}'. Bitte eine Zahl zwischen 0.0 und 2.0 angeben.");
        }
        continue;
      }

      // 5d. MaxOutputTokens dynamisch anpassen
      if (input.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase))
      {
        string tokenValueStr = input.Substring(11).Trim();
        if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1)
        {
          AIParams.MaxOutputTokens = newTokens;
          Console.WriteLine($"[INFO] MaxOutputTokens für die nächste(n) Antwort(en) auf {AIParams.MaxOutputTokens} gesetzt.");
        }
        else
        {
          Console.WriteLine($"[Fehler] Ungültiger Token-Wert '{tokenValueStr}'. Bitte eine positive ganze Zahl angeben.");
        }
        continue;
      }

      // 5b. Datei-Anhang mit kombiniertem Prompt (Z.B.: attach file1.txt, file2.jpg | Erkläre das Bild)
      if (input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase))
      {
        var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(input);
        if (!success) continue;
        promptText = parsedPrompt;
        parts.AddRange(attachmentParts);
      }

      // 6. Text-Prompt anhängen und an die Historie übergeben
      if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
      else if (parts.Count == 0) continue;

      history.Add(new Content { Role = "user", Parts = parts });

      try
      {
        // [AI Context] Hands off to streaming handler. Mutates 'history' internally.
        await StreamGeminiResponseAsync(client, selectedModel, history, input, promptText);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\nHoppla, da gab es einen Fehler: {ex.Message}");
        // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
        history.RemoveAt(history.Count - 1);
      }
    }

    Console.WriteLine("\n[INFO] Chat beendet. Räume temporäre Dateien im Cloud Storage auf...");
    await CleanupGcsBucketAsync();
  }

  /// <summary>
  /// [AI Context] Response streaming & state update.
  /// Side-effects: Mutates 'history' list by appending the assistant's full response. Appends raw text to 'chat_log.md'.
  /// Streamt die Antwort von Gemini asynchron in die Konsole und speichert das Ergebnis in der Historie und einem Logfile.
  /// </summary>
  private async Task StreamGeminiResponseAsync(Client client, string selectedModel, List<Content> history, string input, string promptText)
  {
    Console.Write($"\n{selectedModel}: ");
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

    // Streaming aktivieren
    var responseStream = client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
    await foreach (var chunk in responseStream)
    {
      string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
      Console.Write(chunkText);
      fullResponse += chunkText;
    }
    Console.WriteLine();

    // 7. KI-Antwort in die Historie aufnehmen
    history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });

    // Optional: Verlauf in einer Log-Datei mitprotokollieren
    string logInput = input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase) ? $"[Dateien] {promptText}" : input;
    await System.IO.File.AppendAllTextAsync("chat_log.md", $"\n**Du:** {logInput}\n\n**{selectedModel}:** {fullResponse}\n---\n");

    // [AI Context] File I/O: Dumps raw stream into a .tex file. Assumes model follows LaTeX protocol formatting.
    // [Human] Speichert JEDE Antwort zusätzlich als saubere LaTeX Datei im Ordner!
    // Antwort zusätzlich als saubere .tex-Datei im aktuellen Session-Ordner speichern
    if (!string.IsNullOrWhiteSpace(CurrentSessionLogPath))
    {
      string texFilePath = Path.Combine(CurrentSessionLogPath, $"response-{ResponseCount}-{CurrentSessionDateSuffix}.tex");
      await System.IO.File.WriteAllTextAsync(texFilePath, fullResponse);
      ResponseCount++;
    }
  }

  /// <summary>
  /// [AI Context] Environment variable parser for robust credential loading across OS environments.
  /// [Human] Sucht deinen API Key in den Windows Umgebungsvariablen. So musst du ihn nicht unsicher in den Code schreiben.
  /// Lädt und wählt den konfigurierten API-Key aus den Umgebungsvariablen aus.
  /// </summary>
  private string? ResolveApiKey()
  {
    string? apiKey1 = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                   ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User)
                   ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.Machine);
    string? apiKey2 = System.Environment.GetEnvironmentVariable("LECTURE_TRANSCRIPTION_API_KEY")
                   ?? System.Environment.GetEnvironmentVariable("LECTURE_TRANSCRIPTION_API_KEY", EnvironmentVariableTarget.User)
                   ?? System.Environment.GetEnvironmentVariable("LECTURE_TRANSCRIPTION_API_KEY", EnvironmentVariableTarget.Machine);
    string? apiKey3 = System.Environment.GetEnvironmentVariable("ULTIMATE_API_KEY")
                   ?? System.Environment.GetEnvironmentVariable("ULTIMATE_API_KEY", EnvironmentVariableTarget.User)
                   ?? System.Environment.GetEnvironmentVariable("ULTIMATE_API_KEY", EnvironmentVariableTarget.Machine);

    // Ändere diese Variable auf 1, 2 oder 3, um das Projekt/Konto im Code zu wechseln
    int activeKeyProfile = 1; // Zurück auf den ersten Key (z.B. Free Tier) wechseln
    string apiKey = "";

    switch (activeKeyProfile)
    {
      case 3:
        apiKey = apiKey3 ?? "";
        Console.WriteLine("  [INFO] Verwende ULTIMATE_API_KEY (Projekt 3)");
        break;
      case 2:
        apiKey = apiKey2 ?? "";
        Console.WriteLine("  [INFO] Verwende LECTURE_TRANSCRIPTION_API_KEY (Projekt 2)");
        break;
      case 1:
      default:
        apiKey = apiKey1 ?? "";
        Console.WriteLine("  [INFO] Verwende GEMINI_API_KEY (Projekt 1 - Pay-as-you-go/AI Studio)");
        break;
    }

    if (string.IsNullOrEmpty(apiKey))
    {
      Console.WriteLine("Fehler: Weder GEMINI_API_KEY, LECTURE_TRANSCRIPTION_API_KEY noch ULTIMATE_API_KEY wurden in den Umgebungsvariablen gefunden.");
      return null;
    }

    return apiKey;
  }

  /// <summary>
  /// Fragt den Nutzer, ob eine bestehende History geladen werden soll, 
  /// und baut den entsprechenden /attach Befehl zusammen.
  /// </summary>
  private string? GetInitialHistoryCommand()
  {
    Console.Write("\n[Setup] Möchtest du mit einer frischen History starten? (j/n, bei 'n' wird der 'history'-Ordner geladen): ");
    bool loadHistory = Console.ReadLine()?.Trim().ToLower() == "n";

    if (!loadHistory) return null;

    if (string.IsNullOrWhiteSpace(HistoryFolderPath) || !Directory.Exists(HistoryFolderPath))
    {
      Console.WriteLine($"  [WARNUNG] Der History-Ordner '{HistoryFolderPath}' wurde nicht gefunden oder ist nicht konfiguriert.");
      return null;
    }

    string[] historyFiles = Directory.GetFiles(HistoryFolderPath);
    if (historyFiles.Length == 0)
    {
      Console.WriteLine($"  [INFO] Der History-Ordner '{HistoryFolderPath}' ist leer. Nichts zu laden.");
      return null;
    }

    // Die `historyFiles` enthalten bereits die vollen, absoluten Pfade.
    // Wir können sie direkt verwenden und für den Befehl in Anführungszeichen setzen.
    string fileList = string.Join(", ", historyFiles.Select(p => $"\"{p}\""));
    return $"attach {fileList} | {InitialHistoryPrompt}";
  }

  /// <summary>
  /// [AI Context] Diagnostic tool. Uses raw HttpClient to fetch model details from REST API, 
  /// bypassing the GenAI SDK to access raw JSON properties like 'inputTokenLimit'.
  /// Verbindet sich direkt mit der Google REST-API, um dynamisch eine Liste aller unterstützten Modelle 
  /// abzurufen und formatiert in der Konsole darzustellen (inklusive Token-Limits).
  /// </summary>
  /// <param name="apiKey">Der API-Schlüssel für die Authentifizierung.</param>
  private async Task ShowAvailableModelsAsync(string apiKey)
  {
    Console.WriteLine("\n[API] Rufe alle aktuell verfügbaren Modelle von Google ab...");
    try
    {
      using var httpClient = new HttpClient();
      string json = await httpClient.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
      using var doc = JsonDocument.Parse(json);

      Console.WriteLine("\nVerfügbare Gemini-Modelle und ihre unterstützten Eingabe-Formate:");
      foreach (var element in doc.RootElement.GetProperty("models").EnumerateArray())
      {
        string name = element.GetProperty("name").GetString()?.Replace("models/", "") ?? "";

        // Nur relevante Modelle anzeigen, um die Liste übersichtlich zu halten
        if (!name.Contains("gemini") && !name.Contains("gemma")) continue;

        if (element.TryGetProperty("supportedGenerationMethods", out var methods))
        {
          var methodList = methods.EnumerateArray().Select(m => m.GetString()).ToList();
          if (methodList.Contains("generateContent"))
          {
            string displayName = element.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
            int inputTokens = element.TryGetProperty("inputTokenLimit", out var limit) ? limit.GetInt32() : 0;
            string tokenStr = inputTokens > 0 ? $"[{inputTokens:N0} Tokens]" : "";
            Console.WriteLine($"🔹 {name,-30} {tokenStr,-16} -> {displayName}");
          }
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"[Fehler] Konnte Modelle nicht abrufen: {ex.Message}");
    }
  }

  /// <summary>
  /// [GCS] Löscht alle Dateien im konfigurierten Google Cloud Storage Bucket.
  /// Wird beim Start (für Dateileichen) und beim Beenden (für aktuelle Uploads) aufgerufen.
  /// </summary>
  private async Task CleanupGcsBucketAsync()
  {
    if (string.IsNullOrWhiteSpace(GcsBucketName) || GcsBucketName == "DEIN_BUCKET_NAME_HIER_EINTRAGEN") return;

    if (IsAiStudio) return; // Verhindert, dass AI Studio versehentlich GCS-Ressourcen anpingt

    try
    {
      var storageClient = await StorageClient.CreateAsync();
      Console.WriteLine($"  [GCS] Prüfe Bucket '{GcsBucketName}' auf alte/temporäre Dateien...");
      var objects = storageClient.ListObjectsAsync(GcsBucketName);
      int count = 0;
      await foreach (var obj in objects)
      {
        await storageClient.DeleteObjectAsync(GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0)
      {
        Console.WriteLine($"  [GCS] {count} Datei(en) erfolgreich gelöscht.");
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"  [GCS Warnung] Fehler beim Bereinigen des Buckets: {ex.Message}");
    }
  }

  /// <summary>
  /// Generates a formatted date string like "april-23rd-26".
  /// </summary>
  private string GetFormattedDateString(DateTime date)
  {
    string month = date.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture).ToLower();
    int day = date.Day;
    string suffix = (day % 10 == 1 && day != 11) ? "st"
                  : (day % 10 == 2 && day != 12) ? "nd"
                  : (day % 10 == 3 && day != 13) ? "rd"
                  : "th";
    string year = date.ToString("yyyy");
    return $"{month}-{day}{suffix}-{year}";
  }
}