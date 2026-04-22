using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;

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

  public ChatSession(string uploadFolder, string historyFolder)
  {
    UploadFolderPath = uploadFolder;
    HistoryFolderPath = historyFolder;
  }

  public async Task StartAsync(string selectedModel)
  {
    // [AI Context] Setup phase: Load API keys, configure client (custom timeout for large media), and initialize history.
    // 1. API Keys sicher aus den Umgebungsvariablen laden
    string? apiKey = ResolveApiKey();
    if (string.IsNullOrEmpty(apiKey)) return;

    // 2. Den Client mit einem benutzerdefinierten Timeout erstellen
    // Der Standard-Timeout (100s) ist für große Datei-Uploads/Verarbeitung zu kurz.
    var options = new HttpOptions
    {
      // Timeout auf 20 Minuten erhöhen, um die Verarbeitung großer Dateien zu ermöglichen
      Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
    };
    var client = new Client(apiKey: apiKey, httpOptions: options);

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

    var initialHistory = new List<Content>(history); // Den Startzustand merken

    Console.WriteLine($"\n--- Chat gestartet ({selectedModel}) ---");
    Console.WriteLine("Befehle:");
    Console.WriteLine("  exit / quit               -> Beendet den Chat");
    Console.WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
    Console.WriteLine("  /attach datei1, datei2 | Frage -> Hängt Dateien an und stellt eine Frage dazu.");
    Console.WriteLine("                             (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
    Console.WriteLine("  /modelle                  -> Zeigt alle Modelle mit Audio-Support an");

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
      if (input.ToLower() == "/modelle" || input.ToLower() == "modelle")
      {
        await ShowAvailableModelsAsync(apiKey);
        continue;
      }

      // 5b. Datei-Anhang mit kombiniertem Prompt (Z.B.: /attach file1.txt, file2.jpg | Erkläre das Bild)
      if (input.StartsWith("/attach ", StringComparison.OrdinalIgnoreCase))
      {
        var (success, parsedPrompt) = await HandleAttachmentsAsync(input, client, parts);
        if (!success) continue;
        promptText = parsedPrompt;
      }

      // 6. Text-Prompt anhängen und an die Historie übergeben
      if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
      else if (parts.Count == 0) continue;

      history.Add(new Content { Role = "user", Parts = parts });

      try
      {
        await StreamGeminiResponseAsync(client, selectedModel, history, input, promptText);
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\nHoppla, da gab es einen Fehler: {ex.Message}");
        // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
        history.RemoveAt(history.Count - 1);
      }
    }
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

    // Streaming aktivieren
    var responseStream = client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history);
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
    string logInput = input.StartsWith("/attach") ? $"[Dateien] {promptText}" : input;
    await System.IO.File.AppendAllTextAsync("chat_log.md", $"\n**Du:** {logInput}\n\n**{selectedModel}:** {fullResponse}\n---\n");
  }

  /// <summary>
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
    int activeKeyProfile = 3;
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
        Console.WriteLine("  [INFO] Verwende GEMINI_API_KEY (Projekt 1)");
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
    return $"/attach {fileList} | {InitialHistoryPrompt}";
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
  /// [AI Context] Media & Document handler.
  /// Side-effects: Mutates 'parts' list by appending local file contents or API FileData URIs. 
  /// Handles file path resolution and long-polling for Gemini file processing state.
  /// 
  /// Parst den \attach-Befehl, lädt lokale Textdateien in den Speicher und 
  /// streamt Medien-Dateien (Bilder, Videos, Audio) asynchron an die Google API.
  /// Wartet bei großen Medien-Dateien automatisch auf die serverseitige Verarbeitung.
  /// </summary>
  /// <param name="input">Die komplette Benutzereingabe inklusive /attach.</param>
  /// <param name="client">Der verbundene Gemini API-Client.</param>
  /// <param name="parts">Die Liste der Nachrichten-Teile, an die die Dateien angehängt werden.</param>
  /// <returns>Ein Tupel mit Erfolg-Status und dem extrahierten Text-Prompt.</returns>
  private async Task<(bool success, string promptText)> HandleAttachmentsAsync(string input, Client client, List<Part> parts)
  {
    string payload = input.Substring(8).Trim();
    string[] payloadParts = payload.Split('|', 2);
    string filesPart = payloadParts[0];
    string promptText = payloadParts.Length > 1 ? payloadParts[1].Trim() : "";

    string[] filePaths = filesPart.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
    bool filesLoaded = false;
    var loadedNames = new List<string>();

    foreach (var path in filePaths)
    {
      string originalPath = path.Trim().Trim('"', '\'');
      string filePath = originalPath;

      // Falls ein Upload-Ordner gesetzt ist und der Pfad nicht absolut ist, kombiniere sie
      if (!string.IsNullOrWhiteSpace(UploadFolderPath) && !Path.IsPathRooted(filePath))
      {
        string combinedPath = Path.Combine(UploadFolderPath, filePath);
        if (System.IO.File.Exists(combinedPath))
        {
          filePath = combinedPath;
        }
      }

      if (System.IO.File.Exists(filePath))
      {
        string ext = Path.GetExtension(filePath).ToLower();

        if (new[] { ".md", ".txt", ".cs", ".json", ".xml", ".html", ".py", ".js", ".ts", ".css", ".tex" }.Contains(ext))
        {
          string fileContent = await System.IO.File.ReadAllTextAsync(filePath);
          parts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(filePath)}) ---\n{fileContent}\n--- DOKUMENT ENDE ---" });
        }
        else
        {
          string? mimeType = ext switch
          {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            _ => null
          };

          if (mimeType == null)
          {
            Console.WriteLine($"[Fehler] Der Dateityp '{ext}' von '{Path.GetFileName(filePath)}' wird nicht unterstützt.");
            continue;
          }

          Console.WriteLine($"  [API] Lade '{Path.GetFileName(filePath)}' hoch (dies kann je nach Dateigröße einen Moment dauern)...");
          var uploadedFile = await client.Files.UploadAsync(filePath);

          if (uploadedFile.State?.ToString().Equals("Processing", StringComparison.OrdinalIgnoreCase) == true)
          {
            Console.Write("  [API] Warte auf Verarbeitung durch Google");
            while (uploadedFile.State?.ToString().Equals("Processing", StringComparison.OrdinalIgnoreCase) == true)
            {
              Console.Write(".");
              await Task.Delay(3000);
              uploadedFile = await client.Files.GetAsync(uploadedFile.Name!);
            }
            Console.WriteLine();
          }

          if (uploadedFile.State?.ToString().Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
          {
            Console.WriteLine($"  [Fehler] Die Datei '{Path.GetFileName(filePath)}' konnte von Google nicht verarbeitet werden.");
            continue;
          }

          parts.Add(new Part { FileData = new FileData { FileUri = uploadedFile.Uri, MimeType = mimeType } });
        }

        loadedNames.Add(Path.GetFileName(filePath));
        filesLoaded = true;
      }
      else
      {
        Console.WriteLine($"[Fehler] Die Datei '{filePath}' wurde nicht gefunden und übersprungen.");
      }
    }

    if (filesLoaded && string.IsNullOrWhiteSpace(promptText))
    {
      Console.WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
      Console.WriteLine("  [INFO] Kein Prompt angegeben. Sende automatischen Start-Befehl an Gemini...");
      promptText = "Hier ist das Material. Bitte starte mit der Transkription exakt nach den Regeln des System-Protocols.";
    }
    else if (!filesLoaded)
    {
      return (false, promptText);
    }
    else
    {
      Console.WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
    }

    return (true, promptText);
  }
}