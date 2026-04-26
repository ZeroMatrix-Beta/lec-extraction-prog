using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;

namespace AiInteraction.AutoExtraction;

// ==========================================
// 1. Google AI Studio (Free/Developer Tier)
// ==========================================

public class AiStudioAutoExtractionConfig
{
  public int ActiveApiProfile { get; set; } = 1;
  public string SourceFolder { get; set; } = @"D:\lecture-videos\analysis2";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\analysis2\destination";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string HistoryPreloadFolder { get; set; } = @"D:\gemini-chat-history";
  public string LogFolder { get; set; } = @"D:\gemini-logs";
  public string Model { get; set; } = "gemini-2.5-pro";
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}

public class AiStudioAutoExtractionSession
{
  private readonly Client _client;
  private readonly AiStudioAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private double _speed = 1.2;
  private string _systemInstructionText = "";
  private string _historyText = "";

  public AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
    _sessionLogger = sessionLogger;
  }

  public async Task StartAsync()
  {
    Console.WriteLine($"\n[AutoExtraction] Starte AI Studio Extraction Session...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    if (!Directory.Exists(_config.SourceFolder))
    {
      Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
      return;
    }

    if (!Directory.Exists(_config.TargetFolder))
    {
      Directory.CreateDirectory(_config.TargetFolder);
    }

    string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    foreach (var f in filesToProcess)
    {
      string fileName = Path.GetFileName(f).ToLowerInvariant();
      if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, @"^(\d{2,4}-)?\d{2}-\d{2}-(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)\.[a-z0-9]+$"))
      {
        Console.WriteLine($"\n[WARNUNG] Video entspricht nicht dem Datums-Namensschema: {Path.GetFileName(f)}");
        Console.WriteLine("Erwartetes Format z.B.: 04-12-monday.mp4 oder 06-04-12-montag.mp4 oder 2006-04-12-montag.mp4");
      }
    }

    Console.Write("\nSystem Instruction laden? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j" && System.IO.File.Exists(_config.SystemInstructionPath))
    {
      _systemInstructionText = await System.IO.File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"[INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }

    Console.Write("History (alte Chat-Verläufe) mitschicken? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j" && Directory.Exists(_config.HistoryPreloadFolder))
    {
      var histFiles = Directory.GetFiles(_config.HistoryPreloadFolder, "*.*", SearchOption.AllDirectories);
      foreach (var hf in histFiles)
      {
        if (!string.Equals(Path.GetFullPath(hf), Path.GetFullPath(_config.SystemInstructionPath), StringComparison.OrdinalIgnoreCase))
        {
          _historyText += $"\n--- HISTORY DATEI: {Path.GetFileName(hf)} ---\n" + await System.IO.File.ReadAllTextAsync(hf) + "\n";
        }
      }
      Console.WriteLine($"[INFO] History geladen.");
    }

    _sessionLogger.InitializeSession();
    _sessionLogger.SetSessionMetadata(!string.IsNullOrEmpty(_systemInstructionText), !string.IsNullOrEmpty(_historyText));
    await _sessionLogger.LogSessionSetupAsync();

    await ReplLoopAsync();
  }

  private void ShowCommands()
  {
    Console.WriteLine("\nBefehle:");
    Console.WriteLine("  show commands        -> Zeigt diese Befehle und Infos zur Konfiguration");
    Console.WriteLine("  set speed [wert]     -> Setzt die Video-Geschwindigkeit (z.B. set speed 1.2). Standard: 1.2");
    Console.WriteLine("  convert chosen video -> Wählt ein Video interaktiv aus und startet Konvertierung");
    Console.WriteLine("  convert all videos   -> Konvertiert alle Videos im Quellordner");
    Console.WriteLine("  exit / quit          -> Beendet");
    Console.WriteLine("\nHinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");
  }

  private async Task ReplLoopAsync()
  {
    ShowCommands();
    while (true)
    {
      Console.Write("\nAutoExt> ");
      string input = Console.ReadLine()?.Trim() ?? "";
      if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

      if (input.Equals("show commands", StringComparison.OrdinalIgnoreCase))
      {
        ShowCommands();
      }
      else if (input.StartsWith("set speed", StringComparison.OrdinalIgnoreCase))
      {
        string val = input.Substring(9).Trim();
        if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s))
        {
          _speed = s;
          Console.WriteLine($"Speed gesetzt auf {_speed}x");
        }
        else
        {
          Console.WriteLine("Ungültiger Wert für speed.");
        }
      }
      else if (input.Equals("convert chosen video", StringComparison.OrdinalIgnoreCase))
      {
        var files = FfmpegUtilities.ConsoleUiHelper.SelectSingleFile(_config.SourceFolder);
        if (files.Length > 0)
        {
          await ProcessFilesAsync(files);
        }
      }
      else if (input.Equals("convert all videos", StringComparison.OrdinalIgnoreCase))
      {
        var files = Directory.GetFiles(_config.SourceFolder, "*.mp4");
        await ProcessFilesAsync(files);
      }
      else
      {
        Console.WriteLine("Unbekannter Befehl.");
      }
    }
  }

  private async Task ProcessFilesAsync(string[] files)
  {
    var toolkit = new FfmpegUtilities.FfmpegToolkit();
    string tmpFolder = Path.Combine(_config.TargetFolder, "tmp");
    if (!Directory.Exists(tmpFolder)) Directory.CreateDirectory(tmpFolder);

    foreach (var file in files)
    {
      Console.WriteLine($"\n=== Starte Konvertierung für {Path.GetFileName(file)} ===");

      // 1. Mono & Speed
      Console.WriteLine("Schritt 1: Beschleunige Video und konvertiere nach Mono...");
      string? speedVideo = await toolkit.ProcessGeneralVideoAsync(file, tmpFolder, speedMultiplier: _speed, fps: 1, downmixToMono: true);
      if (speedVideo == null)
      {
        Console.WriteLine("Fehler bei ProcessGeneralVideoAsync");
        continue;
      }

      // 2. Split
      Console.WriteLine("Schritt 2: Schneide Video in Teile mit Overlap...");
      var parts = await toolkit.ProcessSplitVideoAsync(speedVideo, tmpFolder, parts: 3, overlapSeconds: 180, downmixToMono: false);
      if (parts.Count == 0)
      {
        Console.WriteLine("Fehler beim Splitten");
        continue;
      }

      List<string> generatedTexFiles = new List<string>();
      string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
      string baseName = Path.GetFileNameWithoutExtension(file);

      for (int i = 0; i < parts.Count; i++)
      {
        string partFile = parts[i];
        string safePartPath = Path.Combine(tmpFolder, $"{baseName}-{dateStr}-part{i + 1}.mp4");
        int copy = 1;
        while (System.IO.File.Exists(safePartPath))
        {
          safePartPath = Path.Combine(tmpFolder, $"{baseName}-{dateStr}-part{i + 1}-kopie{copy++}.mp4");
        }
        System.IO.File.Move(partFile, safePartPath);

        Console.WriteLine($"\nVerarbeite Teil {i + 1}/{parts.Count}: {Path.GetFileName(safePartPath)}");

        string texOutput = await ProcessPartWithGeminiAsync(safePartPath, i + 1, generatedTexFiles, file);

        if (!string.IsNullOrWhiteSpace(texOutput))
        {
          string cleanTex = texOutput;
          if (cleanTex.Contains("```latex"))
          {
            int start = cleanTex.IndexOf("```latex") + 8;
            int end = cleanTex.LastIndexOf("```");
            if (end > start)
            {
              cleanTex = cleanTex.Substring(start, end - start).Trim();
            }
          }

          string texPath = Path.ChangeExtension(safePartPath, ".tex");
          await System.IO.File.WriteAllTextAsync(texPath, cleanTex);
          generatedTexFiles.Add(texPath);
        }
      }

      Console.WriteLine($"\n[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Die Teile liegen im tmp Ordner: {tmpFolder}");
    }
  }

  private async Task<string> ProcessPartWithGeminiAsync(string partFile, int partNumber, List<string> previousTexFiles, string originalFileName)
  {
    string prompt = _config.Prompt;
    if (partNumber > 1)
    {
      prompt += $"\n\nDieses Video ist Teil {partNumber} einer Vorlesung. Im Kontext sind die bereits generierten LaTeX Dokumente der vorherigen Teile (siehe --- DOKUMENT START ---). " +
                "Bitte nutze diese für den Kontext. WICHTIG: Du musst für das 'spoken-clean' Environment KEINEN Zeit-Offset berechnen. Du darfst ganz normal bei 00:00:00 starten.";
    }

    var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
    if (!uploadSuccess || attachmentParts.Count == 0) return "";

    var parts = new List<Part>(attachmentParts);

    if (!string.IsNullOrEmpty(_historyText))
    {
      parts.Add(new Part { Text = "=== HISTORY START ===\n" + _historyText + "\n=== HISTORY ENDE ===" });
    }

    foreach (var texFile in previousTexFiles)
    {
      string content = await System.IO.File.ReadAllTextAsync(texFile);
      parts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(texFile)}) ---\n{content}\n--- DOKUMENT ENDE ---" });
    }

    parts.Add(new Part { Text = parsedPrompt });

    var history = new List<Content> { new Content { Role = "user", Parts = parts } };

    var requestConfig = new GenerateContentConfig
    {
      Temperature = 0.0f,
      MaxOutputTokens = 65535
    };

    if (!string.IsNullOrWhiteSpace(_systemInstructionText))
    {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }
    if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase))
    {
      requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };
    }

    string fullResponse = "";
    int backoff = 15;

    while (true)
    {
      try
      {
        Console.WriteLine($"  [API] Sende Anfrage für Part {partNumber} an {_config.Model}...");

        var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, history, requestConfig);
        string chunkResp = "";
        await foreach (var chunk in responseStream)
        {
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          chunkResp += txt;
        }

        fullResponse += chunkResp;
        await _sessionLogger.LogChatAsync($"[Part {partNumber}] {originalFileName}", prompt, _config.Model, chunkResp, "AutoExtraction");

        if (chunkResp.Contains("[SYSTEM] Segment complete", StringComparison.OrdinalIgnoreCase))
        {
          Console.WriteLine("\n\n[AutoExtraction] Sende 'Continue'...");
          history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = chunkResp } } });
          history.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = "Continue" } } });
          backoff = 15; // reset backoff on success
          continue;
        }

        break; // Finished
      }
      catch (Exception ex)
      {
        if (ex.Message.Contains("429") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
        {
          Console.WriteLine($"\n[Rate Limit] Warte {backoff} Sekunden... ({ex.Message})");
          await Task.Delay(backoff * 1000);
          backoff *= 2;
        }
        else
        {
          Console.WriteLine($"\n[Fehler] {ex.Message}");
          break;
        }
      }
    }

    return fullResponse;
  }
}

// ==========================================
// 2. Google Cloud Vertex AI (Enterprise Tier)
// ==========================================

public class VertexAutoExtractionConfig
{
  public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
  public string Location { get; set; } = "global";
  public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
  public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string Model { get; set; } = "gemini-2.5-pro";
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}

public class VertexAutoExtractionSession
{
  private readonly Client _client;
  private readonly VertexAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;

  public VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentHandler attachmentHandler)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
  }

  public async Task StartAsync()
  {
    Console.WriteLine("\n[AutoExtraction] Starte Vertex AI Enterprise Extraction Session...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    if (!Directory.Exists(_config.SourceFolder))
    {
      Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
      return;
    }

    if (!Directory.Exists(_config.TargetFolder))
    {
      Directory.CreateDirectory(_config.TargetFolder);
    }

    await CleanupBucketAsync(); // Clean up before starting

    string systemInstruction = "";
    if (System.IO.File.Exists(_config.SystemInstructionPath))
    {
      systemInstruction = await System.IO.File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"[INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }

    string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    if (filesToProcess.Length == 0)
    {
      Console.WriteLine("[AutoExtraction] Keine Dateien zum Verarbeiten gefunden.");
      return;
    }

    Console.WriteLine($"[AutoExtraction] {filesToProcess.Length} Datei(en) gefunden. Starte Verarbeitung...");

    // [AI Context] TODO: Future Architecture Note
    // This section will become significantly more complex. We need to integrate the FfmpegToolkit here
    // to automatically cut/split long lecture videos into 10-12 minute chunks before uploading them.
    // The current loop is a simplified placeholder that assumes the files are already prepared.
    foreach (var file in filesToProcess)
    {
      string targetFilePath = Path.Combine(_config.TargetFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
      if (System.IO.File.Exists(targetFilePath))
      {
        Console.WriteLine($"\n[Übersprungen] {Path.GetFileName(file)} wurde bereits verarbeitet.");
        continue;
      }

      try
      {
        Console.WriteLine($"\n[Verarbeite] {Path.GetFileName(file)}...");

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{file}\" | {_config.Prompt}");
        if (!uploadSuccess || attachmentParts.Count == 0)
        {
          Console.WriteLine($"\n  [Fehler] Upload fehlgeschlagen. Überspringe.");
          continue;
        }

        var parts = new List<Part>(attachmentParts);
        parts.Add(new Part { Text = parsedPrompt });
        var contents = new List<Content> { new Content { Role = "user", Parts = parts } };

        var requestConfig = new GenerateContentConfig
        {
          Temperature = 0.0f,
          MaxOutputTokens = 65535
        };

        if (!string.IsNullOrWhiteSpace(systemInstruction)) requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
        if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };

        Console.WriteLine($"  [API] Sende Anfrage an {_config.Model}...");
        var response = await _client.Models.GenerateContentAsync(_config.Model, contents, requestConfig);

        string outputText = response.Text ?? "";
        string header = $"% ==========================================\n% AutoExtraction Source: {Path.GetFileName(file)}\n% Model: {_config.Model}\n% ==========================================\n\n";

        await System.IO.File.WriteAllTextAsync(targetFilePath, header + outputText);
        Console.WriteLine($"  [Erfolg] Gespeichert unter: {targetFilePath}");
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\n  [Fehler] Abbruch bei {Path.GetFileName(file)}: {ex.Message}");
      }
      finally
      {
        // ALWAYS clean up GCS after each file to minimize enterprise storage costs!
        await CleanupBucketAsync();
      }
    }
    Console.WriteLine("\n[AutoExtraction] Vertex Batch-Verarbeitung abgeschlossen!");
  }

  private async Task CleanupBucketAsync()
  {
    if (string.IsNullOrWhiteSpace(_config.GcsBucketName)) return;
    try
    {
      var storageClient = await StorageClient.CreateAsync();
      var objects = storageClient.ListObjectsAsync(_config.GcsBucketName);
      int count = 0;
      await foreach (var obj in objects)
      {
        await storageClient.DeleteObjectAsync(_config.GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0) Console.WriteLine($"  [GCS] {count} temporäre Datei(en) gelöscht, um Storage-Kosten zu sparen.");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"  [GCS Warnung] Konnte Bucket nicht bereinigen: {ex.Message}");
    }
  }
}