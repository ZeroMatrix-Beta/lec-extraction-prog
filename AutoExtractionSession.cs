using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using System.Threading.Channels;
using System.Threading;
using Google.GenAI;
using Config;
using Google.GenAI.Types;

namespace AiInteraction.AutoExtraction;

// ==========================================
// 1. Google AI Studio (Free/Developer Tier)
// ==========================================

/// <summary>
/// [AI Context] Parses specific date/weekday formats from video filenames. 
/// Crucial for ensuring that overlapping lecture chunks are fed to the AI strictly in chronological order.
/// [Human] Liest das Datum aus dem Dateinamen aus, damit die Videos in der exakt richtigen Reihenfolge verarbeitet werden.
/// </summary>
internal static class VideoDateParser
{
  public static (DateTime Date, string Weekday, string DateString) Parse(string filePath)
  {
    string fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
    var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(?:(\d{2,4})-)?(\d{2})-(\d{2})-([a-z]+)");

    int year = DateTime.Now.Year;
    int month = 1;
    int day = 1;
    string weekday = "Unknown";
    string dateString = "Unknown";

    if (match.Success)
    {
      if (match.Groups[1].Success && !string.IsNullOrEmpty(match.Groups[1].Value))
      {
        year = int.Parse(match.Groups[1].Value);
        if (year < 100) year += 2000;
      }

      month = int.Parse(match.Groups[2].Value);
      day = int.Parse(match.Groups[3].Value);
      weekday = match.Groups[4].Value;
      weekday = char.ToUpper(weekday[0]) + weekday.Substring(1);

      dateString = match.Groups[1].Success && !string.IsNullOrEmpty(match.Groups[1].Value)
          ? $"{day:D2}.{month:D2}.{year}"
          : $"{day:D2}.{month:D2}.";
    }

    DateTime sortDate = DateTime.MaxValue;
    try { if (match.Success) sortDate = new DateTime(year, month, day); } catch { }

    return (sortDate, weekday, dateString);
  }
}

/// <summary>
/// [AI Context] Configuration DTO for unattended batch processing using AI Studio endpoints.
/// Defines source/target directories and the critical extraction prompt.
/// [Human] Konfiguration für den automatisierten Extraktions-Modus mit dem kostenlosen AI Studio.
/// </summary>
public class AiStudioAutoExtractionConfig
{
  public string SourceFolder { get; set; } = @"D:\lecture-videos\analysis2";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\analysis2\destination2";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
  public string LogFolder { get; set; } = @"D:\gemini-logs";
  public string Model { get; set; } = "gemini-3-flash-preview";
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline. 
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos.
/// </summary>
public class AiStudioAutoExtractionSession
{
  private Client _client;
  private readonly AiStudioAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private double _speed = 1.2;
  private string _systemInstructionText = "";
  // [AI Context] Cached payloads to avoid redundant uploads and API calls across multiple video chunks.
  private List<Part> _historyParts = new List<Part>();
  private List<Content> _sessionPreamble = new List<Content>();
  private bool _historyWasLoaded = false;
  private List<Content> _debugChatHistory = new List<Content>();

  public AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
    _sessionLogger = sessionLogger;
  }

  public async Task StartAsync()
  {
    // [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
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

    Console.Write($"\nSystem Instruction aus '{_config.SystemInstructionPath}' laden? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j" && System.IO.File.Exists(_config.SystemInstructionPath))
    {
      _systemInstructionText = await System.IO.File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"  [INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }

    Console.Write($"\nHistory (alte Chat-Verläufe) aus den konfigurierten Pfaden mitschicken? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j")
    {
      var allHistoryFiles = new List<string>();
      var notFoundPaths = new List<string>();

      if (_config.HistoryPreloadPaths != null)
      {
        foreach (var path in _config.HistoryPreloadPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
          if (System.IO.File.Exists(path))
          {
            allHistoryFiles.Add(Path.GetFullPath(path));
          }
          else if (Directory.Exists(path))
          {
            allHistoryFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f)));
          }
          else
          {
            notFoundPaths.Add(path);
          }
        }
      }

      var distinctFiles = allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

      if (distinctFiles.Any())
      {
        Console.WriteLine("\n  [INFO] Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
        string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}");
        if (success && attachmentParts.Any())
        {
          _historyParts.AddRange(attachmentParts);
          _historyWasLoaded = true;
          Console.WriteLine("  [INFO] History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
          await AcknowledgeHistoryAsync();
        }
        else
        {
          Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
        }
      }
    }

    // Update metadata in case AcknowledgeHistoryAsync failed and reset the flag
    _sessionLogger.SetSessionMetadata(!string.IsNullOrEmpty(_systemInstructionText), _historyWasLoaded);
    _sessionLogger.InitializeSession();
    await _sessionLogger.LogSessionSetupAsync();

    await ReplLoopAsync();
  }

  /// <summary>
  /// [AI Context] Interactive control loop for the AutoExtraction mode. 
  /// Allows developers to dynamically adjust FFmpeg speeds, trigger specific files, or chat directly with the configured model for prompt debugging before launching a massive batch job.
  /// [Human] Eine interaktive Konsole, um vor dem großen Batch-Start Parameter (wie Video-Speed) zu testen oder den Prompt zu debuggen.
  /// </summary>
  private async Task ReplLoopAsync()
  {
    Console.WriteLine("\nBefehle:");
    Console.WriteLine("  1) Befehle anzeigen");
    Console.WriteLine("  2) Video-Geschwindigkeit setzen (z.B. 'set speed 1.5' oder nur '2'). Standard: 1.2");
    Console.WriteLine("  3) Einzelnes Video interaktiv auswählen und konvertieren");
    Console.WriteLine("  4) Alle Videos im Quellordner konvertieren");
    Console.WriteLine("  5) Beenden (exit/quit)");
    Console.WriteLine("  6) API-Key Profil wechseln (z.B. 'change-key 2')");
    Console.WriteLine("  (Alles andere wird als normaler Chat-Prompt zum Debuggen an Gemini gesendet)");
    Console.WriteLine("\nHinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");

    while (true)
    {
      if (!Console.IsInputRedirected)
      {
        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
      }
      Console.Write("\nAutoExt> ");
      string input = Console.ReadLine()?.Trim() ?? "";
      if (string.IsNullOrWhiteSpace(input)) continue;

      string normalizedInput = input.TrimStart('/');
      if (normalizedInput == "5" || normalizedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

      if (normalizedInput == "1" || normalizedInput.Equals("show commands", StringComparison.OrdinalIgnoreCase))
      {
        Console.WriteLine("\nBefehle:");
        Console.WriteLine("  1) Befehle anzeigen");
        Console.WriteLine("  2) Video-Geschwindigkeit setzen (z.B. 'set speed 1.5' oder nur '2'). Standard: 1.2");
        Console.WriteLine("  3) Einzelnes Video interaktiv auswählen und konvertieren");
        Console.WriteLine("  4) Alle Videos im Quellordner konvertieren");
        Console.WriteLine("  5) Beenden (exit/quit)");
        Console.WriteLine("  6) API-Key Profil wechseln (z.B. 'change-key 2')");
        Console.WriteLine("  (Alles andere wird als normaler Chat-Prompt zum Debuggen an Gemini gesendet)");
        Console.WriteLine("\nHinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");
      }
      else if (normalizedInput == "2" || normalizedInput.StartsWith("2 ") || normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase))
      {
        string val = "";
        if (normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) val = normalizedInput.Substring(9).Trim();
        else if (normalizedInput.StartsWith("2 ")) val = normalizedInput.Substring(2).Trim();
        else if (normalizedInput == "2")
        {
          Console.Write("Neuer Speed-Wert (z.B. 1.5): ");
          val = Console.ReadLine()?.Trim() ?? "";
        }

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
      else if (normalizedInput == "3" || normalizedInput.Equals("convert chosen video", StringComparison.OrdinalIgnoreCase))
      {
        var files = FfmpegUtilities.ConsoleUiHelper.SelectSingleFile(_config.SourceFolder);
        if (files.Length > 0)
        {
          await ProcessFilesAsync(files);
        }
      }
      else if (normalizedInput == "4" || normalizedInput.Equals("convert all videos", StringComparison.OrdinalIgnoreCase))
      {
        var files = Directory.GetFiles(_config.SourceFolder, "*.mp4");
        await ProcessFilesAsync(files);
      }
      else if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
      {
        _debugChatHistory.Clear();
        Console.WriteLine("  [INFO] Debug-Chat Verlauf gelöscht.");
      }
      else if (System.Text.RegularExpressions.Regex.IsMatch(normalizedInput, @"^change[- ]?key\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
      {
        var match = System.Text.RegularExpressions.Regex.Match(normalizedInput, @"change[- ]?key\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int newProfile) && newProfile >= 1 && newProfile <= 3)
        {
          string? newApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKey(newProfile);
          if (!string.IsNullOrEmpty(newApiKey))
          {
            _client = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
            _attachmentHandler.UpdateClient(_client);
            Console.WriteLine($"  [INFO] API-Key erfolgreich auf Profil {newProfile} gewechselt!");
          }
        }
        else
        {
          Console.WriteLine("  [Fehler] Bitte eine gültige Profilnummer (1, 2 oder 3) angeben.");
        }
      }
      else
      {
        await DebugChatAsync(input); // Chat erhält den originalen Input
      }
    }
  }

  private async Task DebugChatAsync(string input)
  {
    _debugChatHistory.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = input } } });

    var requestConfig = new GenerateContentConfig
    {
      Temperature = 0.7f,
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

    Console.Write($"\n[Debug Chat] {_config.Model} (Strg+C zum Abbrechen): ");
    string fullResponse = "";

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
    Console.CancelKeyPress += cancelHandler;

    int maxRetries = 5;
    int backoff = 30;
    bool exceptionCaught = false;

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
      bool isGenerating = true;
      var inputInterceptorTask = Task.Run(async () =>
      {
        while (isGenerating)
        {
          if (!Console.IsInputRedirected && Console.KeyAvailable)
          {
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            Console.WriteLine("\n[System] Still waiting for the acknowledgment / response. Please wait...");
          }
          await Task.Delay(100);
        }
      });

      try
      {
        if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");
        var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, _debugChatHistory, requestConfig);
        await foreach (var chunk in responseStream.WithCancellation(cts.Token))
        {
          if (cts.IsCancellationRequested) break;
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          fullResponse += txt;
        }
        Console.WriteLine();
        isGenerating = false;
        await inputInterceptorTask;
        break; // Erfolg
      }
      catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase))
      {
        isGenerating = false;
        await inputInterceptorTask;
        exceptionCaught = true;
        break;
      }
      catch (Exception ex)
      {
        isGenerating = false;
        await inputInterceptorTask;

        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

        bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
        if (isOverloaded && attempt < maxRetries)
        {
          var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
          if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay))
          {
            int waitTime = serverSuggestedDelay + 2;
            Console.WriteLine($"\n[Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await SmartDelayAsync(waitTime)) { exceptionCaught = true; break; }
          }
          else
          {
            Console.WriteLine($"\n[Rate Limit / Überlastung] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await SmartDelayAsync(backoff)) { exceptionCaught = true; break; }
          }
          backoff *= 2; // Increment backoff for the next potential retry, regardless of whether server suggested a delay
        }
        else
        {
          Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
          // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
          _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
          break;
        }
      }
    }

    Console.CancelKeyPress -= cancelHandler;

    if (exceptionCaught || cts.IsCancellationRequested)
    {
      Console.WriteLine("\n\n[INFO] Debug-Chat durch Benutzer abgebrochen.");
    }

    if (!string.IsNullOrWhiteSpace(fullResponse))
    {
      _debugChatHistory.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
    }
    else if (_debugChatHistory.Any() && _debugChatHistory.Last().Role == "user")
    {
      // Falls abgebrochen wurde, bevor die KI etwas gesagt hat, die User-Nachricht entfernen.
      _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
    }
  }

  /// <summary>
  /// [AI Context] Forces a real API call to explicitly acknowledge the history payload. 
  /// This guarantees the model context is correctly primed before batch processing starts and provides immediate visual feedback.
  /// [Human] Sendet die geladenen History-Dateien an Gemini und wartet auf eine Bestätigung. So stellen wir sicher, dass die KI den Kontext gefressen hat, bevor es losgeht.
  /// </summary>
  private async Task AcknowledgeHistoryAsync()
  {
    var historyPromptParts = new List<Part>(_historyParts);
    historyPromptParts.Add(new Part { Text = "Hier ist das Material aus meiner History. Bitte lies es sorgfältig durch. Bestätige mir den Erhalt ausnahmslos mit exakt folgendem Text: '[SYSTEM] Material [...] received and analyzed. I am standing by for your instructions.' Warte danach auf meine nächsten Anweisungen." });
    var userContent = new Content { Role = "user", Parts = historyPromptParts };

    _sessionPreamble.Add(userContent);

    var requestConfig = new GenerateContentConfig { Temperature = 0.0f, MaxOutputTokens = 1024 };
    if (!string.IsNullOrWhiteSpace(_systemInstructionText))
    {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }
    if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase))
    {
      requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };
    }

    Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.Model}: ");
    string fullResponse = "";
    int backoff = 30;
    int maxRetries = 5;
    bool success = false;

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
      using var cts = new CancellationTokenSource();
      ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
      Console.CancelKeyPress += cancelHandler;

      try
      {
        if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");
        var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, _sessionPreamble, requestConfig);
        await foreach (var chunk in responseStream.WithCancellation(cts.Token))
        {
          if (cts.IsCancellationRequested) break;
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          fullResponse += txt;
        }
        Console.WriteLine();
        success = true;
        break; // Success
      }
      catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase))
      {
        Console.WriteLine("\n[INFO] Bestätigung durch Benutzer abgebrochen.");
        break;
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
        if (isOverloaded && attempt < maxRetries)
        {
          var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
          if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay))
          {
            int waitTime = serverSuggestedDelay + 2;
            Console.WriteLine($"\n[Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await SmartDelayAsync(waitTime)) { break; }
          }
          else
          {
            Console.WriteLine($"\n[Rate Limit / Überlastung] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await SmartDelayAsync(backoff)) { break; }
          }
          backoff *= 2;
        }
        else
        {
          Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
          break;
        }
      }
      finally
      {
        Console.CancelKeyPress -= cancelHandler;
      }
    }

    if (success && !string.IsNullOrWhiteSpace(fullResponse))
    {
      _sessionPreamble.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
      await _sessionLogger.LogChatAsync("[History Acknowledgment]", historyPromptParts.Last().Text, _config.Model, fullResponse, "AutoExtractionSetup");
    }
    else
    {
      Console.WriteLine("\n[FEHLER] Konnte Bestätigung für History nicht erhalten. Die History wird für diese Session ignoriert.");
      _sessionPreamble.Clear();
      _historyWasLoaded = false;
    }
  }

  /// <summary>
  /// [AI Context] Executes the batch processing workflow.
  /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
  /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
  /// </summary>
  private async Task ProcessFilesAsync(string[] files)
  {
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
    var producerTask = Task.Run(async () =>
    {
      foreach (var file in files)
      {
        string baseName = Path.GetFileNameWithoutExtension(file);
        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        var cachedParts = Directory.GetFiles(tmpFolder, $"{baseName}-{dateStr}-part*.mp4").ToList();
        bool useCache = false;

        if (cachedParts.Count > 0)
        {
          var fileInfo = new FileInfo(cachedParts[0]);
          if ((DateTime.Now - fileInfo.LastWriteTime).TotalHours <= 2)
          {
            useCache = true;
          }
        }

        if (useCache)
        {
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
        for (int i = 0; i < parts.Count; i++)
        {
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
    await foreach (var job in channel.Reader.ReadAllAsync())
    {
      string file = job.originalFile;
      var parts = job.parts;
      bool isCached = job.isCached;

      Console.WriteLine($"\n[Gemini Consumer] === Starte API-Extraktion für {Path.GetFileName(file)} ===");
      List<string> generatedTexFiles = new List<string>();
      string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
      string baseName = Path.GetFileNameWithoutExtension(file);

      for (int i = 0; i < parts.Count; i++)
      {
        string safePartPath = parts[i];

        Console.WriteLine($"\nVerarbeite Teil {i + 1}/{parts.Count}: {Path.GetFileName(safePartPath)}");

        string texOutput = await ProcessPartWithGeminiAsync(safePartPath, i + 1, parts.Count, generatedTexFiles, file);

        if (!string.IsNullOrWhiteSpace(texOutput))
        {
          string cleanTex = texOutput;

          // [AI Context] Regex-based cleanup ensures that even if the output is split across multiple continuation chunks,
          // all markdown blocks and system messages are fully stripped, preventing compilation errors.
          cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"```latex\r?\n?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
          cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"```\r?\n?", "");
          // [AI Context] Fuzzy regex to catch variations like "**[SYSTEM] Segment complete.**" with leading spaces or bold markers
          cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"(?im)^[ \t]*(?:\*|_)*\[SYSTEM\][^\r\n]*(?:Segment|Video)\s*complete[^\r\n]*\r?\n?", "");
          cleanTex = cleanTex.Trim();

          string texPath = Path.ChangeExtension(safePartPath, ".tex");
          await System.IO.File.WriteAllTextAsync(texPath, cleanTex);

          // Hier werden .tex dateien geschrieben:
          generatedTexFiles.Add(texPath);
        }
      }

      Console.WriteLine($"\n[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Die Teile liegen im tmp Ordner: {tmpFolder}");
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
  private async Task<string> ProcessPartWithGeminiAsync(string partFile, int partNumber, int totalParts, List<string> previousTexFiles, string originalFileName)
  {
    var dateInfo = VideoDateParser.Parse(originalFileName);
    string prompt = _config.Prompt;

    // [AI Context] Dynamic prompt engineering. Instructs the model to treat the chunk as part of a larger whole, 
    // providing absolute timestamps and previously generated LaTeX to maintain cross-chunk continuity.
    prompt += $"\n\n[Meta-Information]: These {totalParts} video parts (and corresponding .tex files) originate from the lecture on {dateInfo.Weekday}, {dateInfo.DateString}. Do not include this date in the compiled LaTeX code right now; it is just for your internal context.";
    prompt += $"\n\nThe uploaded video is part {partNumber} of {totalParts} from this lecture.";
    prompt += $"\n\nThe video is played back / scaled to {_speed}x speed.";

    if (partNumber > 1)
    {
      prompt += "\n\nThe previously generated LaTeX documents for the prior parts are included in the context (see --- DOKUMENT START ---). Please use them to maintain context continuity.";
      prompt += "\n\nNote: Consecutive video parts have an intentional 3-minute overlap to prevent context loss. If the video starts mid-sentence, use the provided LaTeX context from the previous part to reconstruct the full sentence.";
    }

    prompt += "\n\nIMPORTANT: Do NOT calculate any time offset for the 'spoken-clean' environment. You may start normally at 00:00:00. Furthermore, do NOT calculate any time scaling factor for the speed adjustments. Just transcribe the timestamps exactly as they appear in the video player.";
    prompt += "\n\nWhen in doubt, transcribe more content into the 'spoken-clean' environment rather than less. Do NOT attempt to merge the current part with the previous parts. A dedicated post-processing AI-routine will handle the final merging and duplicate removal later. Just focus on transcribing the currently uploaded video. Ensure that related mathematical derivations and explanations are grouped together within a single 'math-stroke' environment to keep the logical flow cohesive, self-contained and unbroken.";

    var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
    if (!uploadSuccess || attachmentParts.Count == 0) return "";

    var userPromptParts = new List<Part>(attachmentParts);

    foreach (var texFile in previousTexFiles)
    {
      string content = await System.IO.File.ReadAllTextAsync(texFile);
      userPromptParts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(texFile)}) ---\n{content}\n--- DOKUMENT ENDE ---" });
    }

    userPromptParts.Add(new Part { Text = parsedPrompt });

    var history = new List<Content>();

    history.AddRange(_sessionPreamble);
    history.Add(new Content { Role = "user", Parts = userPromptParts });

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
    int backoff = 30;
    int currentRequest = 1;
    int maxRequests = 5;
    int attempt = 1; // Zähler für API-Fehlschläge
    int maxRetries = 5;

    while (true)
    {
      bool isGenerating = true;
      var inputInterceptorTask = Task.Run(async () =>
      {
        while (isGenerating)
        {
          if (!Console.IsInputRedirected && Console.KeyAvailable)
          {
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            Console.WriteLine("\n[System] Still waiting for the acknowledgment / processing...");
          }
          await Task.Delay(100);
        }
      });

      try
      {
        Console.WriteLine($"  [API] Sende Anfrage für Part {partNumber} an {_config.Model} (Request {currentRequest}/{maxRequests})...");

        var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, history, requestConfig);
        string chunkResp = "";
        await foreach (var chunk in responseStream)
        {
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          chunkResp += txt;
        }

        isGenerating = false;
        await inputInterceptorTask;

        fullResponse += chunkResp;
        await _sessionLogger.LogChatAsync($"[Part {partNumber}] {originalFileName}", prompt, _config.Model, chunkResp, "AutoExtraction");

        bool segmentComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[SYSTEM\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool videoComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[SYSTEM\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!videoComplete)
        {
          if (currentRequest >= maxRequests)
          {
            Console.WriteLine($"\n\n[WARNUNG] Maximale Anzahl an Requests ({maxRequests}) für diesen Teil erreicht. Breche ab.");
            break;
          }

          if (segmentComplete)
          {
            Console.WriteLine("\n\n[AutoExtraction] Segment-Limit erreicht. Sende 'Continue'...");
          }
          else
          {
            Console.WriteLine("\n\n[AutoExtraction] Unerwartetes Ende der Antwort (Max Tokens?). Sende 'Continue'...");
          }

          string snippet = chunkResp.Length > 300 ? "...\n" + chunkResp.Substring(chunkResp.Length - 300) : chunkResp;
          string continuePrompt = "[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
                                  $"```latex\n{snippet}\n```\n\n" +
                                  "Please \"continue\" exactly where you left off...";

          history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = chunkResp } } });
          history.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = continuePrompt } } });

          backoff = 30;
          attempt = 1;
          currentRequest++;
          continue;
        }

        break; // Finished
      }
      catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled") || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase))
      {
        isGenerating = false;
        await inputInterceptorTask;
        Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen.");
        // Break out of the while loop for this part and return what we have so far.
        return fullResponse;
      }
      catch (Exception ex)
      {
        isGenerating = false;
        await inputInterceptorTask;

        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

        bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
        if (isOverloaded && attempt < maxRetries)
        {
          var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
          if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay))
          {
            int waitTime = serverSuggestedDelay + 2;
            Console.WriteLine($"\n  [Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await SmartDelayAsync(waitTime)) break;
          }
          else
          {
            Console.WriteLine($"\n  [Rate Limit / Überlastung] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await SmartDelayAsync(backoff)) break;
          }
          backoff *= 2; // Increment backoff for the next potential retry, regardless of whether server suggested a delay
          attempt++;
        } // Unrecoverable error
        else
        {
          Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Fahre mit nächstem Teil fort.");
          break;
        }
      }
    }

    return fullResponse;
  }

  /// <summary>
  /// [AI Context] Implements an interactive delay with user cancellation.
  /// Allows the user to interrupt long backoff periods by pressing any key.
  /// [Human] Eine intelligente Wartefunktion, die der Nutzer mit Tastendruck abbrechen kann.
  /// </summary>
  private async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...")
  {
    bool delayCanceled = false;
    ConsoleCancelEventHandler cancelHandler = (sender, e) =>
    {
      e.Cancel = true;
      delayCanceled = true;
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
      int delaySteps = seconds * 10;
      for (int i = 0; i < delaySteps; i++)
      {
        if (delayCanceled) return false;
        await Task.Delay(100);
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
          Console.WriteLine($"\n[System] {message}");
        }
      }
      return true;
    }
    finally
    {
      Console.CancelKeyPress -= cancelHandler;
    }
  }
}

/// <summary>
/// [AI Context] Configuration for the enterprise Vertex AI tier.
/// Binds to a specific GCP Project and Region, requiring an active billing account and a dedicated GCS bucket for multimodal payloads.
/// </summary>
public class VertexAutoExtractionConfig
{
  public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
  public string Location { get; set; } = "global";
  public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
  public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
  public string LogFolder { get; set; } = AppConfig.LogFolder;
  public string Model { get; set; } = "gemini-3-flash-preview";
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
  public double SpeedMultiplier { get; set; } = 1.2;
}

/// <summary>
/// [AI Context] Orchestrates the enterprise-grade automated transcription pipeline using Vertex AI.
/// Handles stringent GCS bucket cleanups after each chunk to prevent runaway cloud storage billing.
/// [Human] Enterprise-Version der Batch-Verarbeitung. Löscht zwingend die Cloud-Speicher-Uploads nach jedem Video, um GCP-Kosten zu minimieren.
/// </summary>
public class VertexAutoExtractionSession
{
  private readonly Client _client;
  private readonly VertexAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  // [AI Context] Enterprise session state. Note the absence of the REPL loop, as this is intended for unattended bulk operations.

  public VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
    _sessionLogger = sessionLogger;
  }

  public async Task StartAsync()
  {
    // [Human] Die Hauptschleife für die Vertex-Verarbeitung. Arbeitet die Videos strikt chronologisch ab und bereitet die Umgebung vor.
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

    _sessionLogger.InitializeSession();

    string systemInstruction = "";
    Console.Write($"\nSystem Instruction aus '{_config.SystemInstructionPath}' laden? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j" && System.IO.File.Exists(_config.SystemInstructionPath))
    {
      systemInstruction = await System.IO.File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"  [INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }

    var historyParts = new List<Part>();
    bool historyWasLoaded = false;
    Console.Write($"\nHistory (alte Chat-Verläufe) aus den konfigurierten Pfaden mitschicken? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j")
    {
      var allHistoryFiles = new List<string>();
      var notFoundPaths = new List<string>();

      if (_config.HistoryPreloadPaths != null)
      {
        foreach (var path in _config.HistoryPreloadPaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
          if (System.IO.File.Exists(path))
          {
            allHistoryFiles.Add(Path.GetFullPath(path));
          }
          else if (Directory.Exists(path))
          {
            allHistoryFiles.AddRange(Directory.GetFiles(path, "*.*", SearchOption.AllDirectories).Select(f => Path.GetFullPath(f)));
          }
          else
          {
            notFoundPaths.Add(path);
          }
        }
      }

      var distinctFiles = allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

      if (distinctFiles.Any())
      {
        Console.WriteLine("\n  [INFO] Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
        string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}");
        if (success && attachmentParts.Any())
        {
          historyParts.AddRange(attachmentParts);
          historyWasLoaded = true;
          Console.WriteLine("  [INFO] History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
        }
        else
        {
          Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
        }
      }
    }

    var sessionPreamble = new List<Content>();

    bool loadedSysPrompt = !string.IsNullOrEmpty(systemInstruction);
    _sessionLogger.SetSessionMetadata(loadedSysPrompt, historyWasLoaded);
    await _sessionLogger.LogSessionSetupAsync();

    string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    if (filesToProcess.Length == 0)
    {
      Console.WriteLine("[AutoExtraction] Keine Dateien zum Verarbeiten gefunden.");
      return;
    }

    filesToProcess = filesToProcess.OrderBy(f => VideoDateParser.Parse(f).Date).ToArray();

    Console.WriteLine($"[AutoExtraction] {filesToProcess.Length} Datei(en) gefunden. Starte Verarbeitung...");

    var toolkit = new FfmpegUtilities.FfmpegToolkit();
    string tmpFolder = Path.Combine(_config.TargetFolder, "tmp");
    if (!Directory.Exists(tmpFolder)) Directory.CreateDirectory(tmpFolder);

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

        string baseName = Path.GetFileNameWithoutExtension(file);
        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        var cachedParts = Directory.GetFiles(tmpFolder, $"{baseName}-{dateStr}-part*.mp4").ToList();
        bool useCache = false;

        if (cachedParts.Count > 0)
        {
          var fileInfo = new FileInfo(cachedParts[0]);
          if ((DateTime.Now - fileInfo.LastWriteTime).TotalHours <= 2)
          {
            useCache = true;
          }
        }

        List<string> videoParts = new List<string>();

        if (useCache)
        {
          Console.WriteLine($"  [Cache] FFmpeg übersprungen für {Path.GetFileName(file)}. Verwende gecachte Dateien (jünger als 2h).");
          cachedParts.Sort();
          videoParts = cachedParts;
        }
        else
        {
          Console.WriteLine($"  Schritt 1: Konvertiere Video für Vertex (1 FPS, 720p, Mono, {_config.SpeedMultiplier}x Speed)...");
          string? processedVideo = await toolkit.ProcessGeneralVideoAsync(file, tmpFolder, speedMultiplier: _config.SpeedMultiplier, fps: 1, downmixToMono: true, scaleTo720p: true);

          if (processedVideo == null)
          {
            Console.WriteLine($"  [Fehler] Konvertierung fehlgeschlagen. Überspringe.");
            continue;
          }

          Console.WriteLine("  Schritt 2: Schneide Video in Teile mit Overlap...");
          var rawParts = await toolkit.ProcessSplitVideoAsync(processedVideo, tmpFolder, parts: 3, overlapSeconds: 180, downmixToMono: false, streamCopy: true);

          if (rawParts.Count == 0)
          {
            Console.WriteLine($"  [Fehler] Splitten fehlgeschlagen. Überspringe.");
            continue;
          }

          for (int i = 0; i < rawParts.Count; i++)
          {
            string safePartPath = Path.Combine(tmpFolder, $"{baseName}-{dateStr}-part{i + 1}.mp4");
            if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
            System.IO.File.Move(rawParts[i], safePartPath);
            videoParts.Add(safePartPath);
          }
        }

        List<string> generatedTexFiles = new List<string>();
        string fullOutputText = "";

        for (int i = 0; i < videoParts.Count; i++)
        {
          string partFile = videoParts[i];
          Console.WriteLine($"\n  [Verarbeite] Teil {i + 1}/{videoParts.Count}...");

          string prompt = _config.Prompt;
          var dateInfo = VideoDateParser.Parse(file);

          prompt += $"\n\n[Meta-Information]: These {videoParts.Count} video parts (and corresponding .tex files) originate from the lecture on {dateInfo.Weekday}, {dateInfo.DateString}. Do not include this date in the compiled LaTeX code right now; it is just for your internal context.";
          prompt += $"\n\nThe uploaded video is part {i + 1} of {videoParts.Count} from this lecture.";
          prompt += $"\n\nThe video is played back / scaled to {_config.SpeedMultiplier}x speed.";

          if (i > 0)
          {
            prompt += "\n\nThe previously generated LaTeX documents for the prior parts are included in the context (see --- DOKUMENT START ---). Please use them to maintain context continuity.";
            prompt += "\n\nNote: Consecutive video parts have an intentional 3-minute overlap to prevent context loss. If the video starts mid-sentence, use the provided LaTeX context from the previous part to reconstruct the full sentence.";
          }

          prompt += "\n\nIMPORTANT: Do NOT calculate any time offset for the 'spoken-clean' environment. You may start normally at 00:00:00. Furthermore, do NOT calculate any time scaling factor for the speed adjustments. Just transcribe the timestamps exactly as they appear in the video player.";
          prompt += "\n\nTranscribe more content into the 'spoken-clean' environment rather than less. Do NOT attempt to merge the current part with the previous parts. A dedicated post-processing script will handle the final merging and duplicate removal later. Just focus on transcribing the currently uploaded video. Ensure that related mathematical derivations and explanations are grouped together within a single 'math-stroke' environment to keep the logical flow cohesive, self-contained and unbroken.";

          var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
          if (!uploadSuccess || attachmentParts.Count == 0)
          {
            Console.WriteLine($"\n  [Fehler] Upload fehlgeschlagen für Teil {i + 1}. Überspringe.");
            continue;
          }

          var userPromptParts = new List<Part>(attachmentParts);

          // [AI Context] Context stitching for the Enterprise model. Maintains rigid notation consistency across segment boundaries.
          foreach (var texFile in generatedTexFiles)
          {
            string content = await System.IO.File.ReadAllTextAsync(texFile);
            userPromptParts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(texFile)}) ---\n{content}\n--- DOKUMENT ENDE ---" });
          }

          userPromptParts.Add(new Part { Text = parsedPrompt });

          var contents = new List<Content>();

          // [AI Context] Simulated Multi-Turn Initialization for Vertex.
          contents.AddRange(sessionPreamble);

          contents.Add(new Content { Role = "user", Parts = userPromptParts });

          var requestConfig = new GenerateContentConfig
          {
            Temperature = 0.0f,
            MaxOutputTokens = 65535
          };

          if (!string.IsNullOrWhiteSpace(systemInstruction)) requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
          if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };

          int backoff = 30;
          int maxRetries = 5;
          string outputTextForPart = "";
          int currentRequest = 1;
          int maxRequests = 5;

          using var cts = new CancellationTokenSource();
          ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
          Console.CancelKeyPress += cancelHandler;

          while (true)
          {
            string chunkOutput = "";
            bool streamDropped = false;
            bool userCancelled = false;

            int attempt = 1;
            for (; attempt <= maxRetries; attempt++)
            {
              bool isGenerating = true;
              var inputInterceptorTask = Task.Run(async () =>
              {
                while (isGenerating)
                {
                  if (!Console.IsInputRedirected && Console.KeyAvailable)
                  {
                    while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                    Console.WriteLine("\n[System] Still waiting for the acknowledgment / processing...");
                  }
                  await Task.Delay(100);
                }
              });

              try
              {
                Console.WriteLine($"  [API] Sende Anfrage an {_config.Model} (Request {currentRequest}/{maxRequests}, Versuch {attempt}/{maxRetries})...");

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, contents, requestConfig);
                await foreach (var chunk in responseStream.WithCancellation(cts.Token))
                {
                  if (cts.IsCancellationRequested) break;
                  string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                  Console.Write(txt);
                  chunkOutput += txt;
                }

                isGenerating = false;
                await inputInterceptorTask;

                if (cts.IsCancellationRequested) userCancelled = true;
                break;
              }
              catch (Exception ex)
              {
                isGenerating = false;
                await inputInterceptorTask;

                if (ex is OperationCanceledException && cts.IsCancellationRequested)
                {
                  userCancelled = true;
                  break;
                }

                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

                // [AI Context] Vertex AI specific Rescue Strategy to salvage interrupted generation streams.
                // [Human] Identisch zur AI Studio Version: Verhindert den Verlust von bereits generiertem LaTeX-Code bei unerwarteten Verbindungsabbrüchen.
                if (chunkOutput.Length > 100)
                {
                  Console.WriteLine("\n[INFO] Verbindung während der Generierung abgebrochen. Versuche, die unvollständige Antwort zu retten und fortzusetzen...");
                  streamDropped = true;
                  break;
                }

                bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase);
                if (isOverloaded && attempt < maxRetries)
                {
                  var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
                  if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay))
                  {
                    int waitTime = serverSuggestedDelay + 2;
                    Console.WriteLine($"\n  [Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
                    if (!await SmartDelayAsync(waitTime)) { userCancelled = true; break; }
                  }
                  else
                  {
                    Console.WriteLine($"\n  [Rate Limit / Überlastung / Verbindungsabbruch] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
                    if (!await SmartDelayAsync(backoff)) { userCancelled = true; break; }
                  }
                  backoff *= 2; // Increment backoff for the next potential retry, regardless of whether server suggested a delay
                }
                else
                {
                  Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Breche Verarbeitung für diesen Teil ab.");
                  userCancelled = true;
                  break;
                }
              }
            }

            if (userCancelled) break;

            outputTextForPart += chunkOutput;
            await _sessionLogger.LogChatAsync($"[Part {i + 1}] {Path.GetFileName(file)}", prompt, _config.Model, chunkOutput, "VertexAutoExtraction");

            bool segmentComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkOutput, @"\[SYSTEM\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            bool videoComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkOutput, @"\[SYSTEM\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (!videoComplete)
            {
              if (currentRequest >= maxRequests)
              {
                Console.WriteLine($"\n  [WARNUNG] Max Requests ({maxRequests}) erreicht. Breche ab.");
                break;
              }

              if (segmentComplete) Console.WriteLine("\n  [Vertex] Segment Limit erreicht. Sende 'Continue'...");
              else if (streamDropped) Console.WriteLine("\n  [Vertex] Stream abgebrochen. Sende automatisiert 'Continue' zur Wiederaufnahme...");
              else Console.WriteLine("\n  [Vertex] KI hat abgebrochen (Max Tokens). Sende automatisiert 'Continue'...");

              // Hole nur die letzten 300 Zeichen als Anker, um extrem viele Tokens zu sparen!
              string snippet = chunkOutput.Length > 300 ? "...\n" + chunkOutput.Substring(chunkOutput.Length - 300) : chunkOutput;
              string continuePrompt = "[IMPORTANT] Your response has been cut by the system's automatic length-detection. Your last latex block ended with:\n\n" +
                                      $"```latex\n{snippet}\n```\n\n" +
                                      "Please \"continue\" exactly where you left off...";

              contents.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = chunkOutput } } });
              contents.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = continuePrompt } } });
              backoff = 30; // Reset für den nächsten Request
              currentRequest++;
              continue;
            }

            break; // Finished
          }

          Console.CancelKeyPress -= cancelHandler;

          string cleanTex = outputTextForPart;
          cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"```latex\r?\n?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
          cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"```\r?\n?", "");
          // [AI Context] Fuzzy regex to catch variations like "**[SYSTEM] Segment complete.**" with leading spaces or bold markers
          cleanTex = System.Text.RegularExpressions.Regex.Replace(cleanTex, @"(?im)^[ \t]*(?:\*|_)*\[SYSTEM\][^\r\n]*(?:Segment|Video)\s*complete[^\r\n]*\r?\n?", "");
          cleanTex = cleanTex.Trim();

          fullOutputText += $"\n\n% --- TEIL {i + 1} ---\n" + cleanTex;

          string partTexFile = Path.ChangeExtension(partFile, ".tex");
          await System.IO.File.WriteAllTextAsync(partTexFile, cleanTex);
          generatedTexFiles.Add(partTexFile);

          // [AI Context] Cost Mitigation Strategy:
          // Vertex requires actual files residing in a GCS Bucket. Frequent cleanups prevent runaway cloud storage billing.
          await CleanupBucketAsync();
        }

        string header = $"% ==========================================\n% AutoExtraction Source: {Path.GetFileName(file)}\n% Model: {_config.Model}\n% ==========================================\n\n";
        await System.IO.File.WriteAllTextAsync(targetFilePath, header + fullOutputText);
        Console.WriteLine($"  [Erfolg] Komplettes Dokument gespeichert unter: {targetFilePath}");
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        Console.WriteLine($"  [Fehler] Abbruch bei {Path.GetFileName(file)}.");
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
    // [AI Context] Financial Guardrail:
    // Ensures the cloud storage bucket is purged immediately after processing to prevent accumulating storage costs for massive temporary video files.
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
      Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
      Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
      Console.WriteLine($"  [GCS Warnung] Konnte Bucket nicht bereinigen.");
    }
  }

  private async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...")
  {
    bool delayCanceled = false;
    ConsoleCancelEventHandler cancelHandler = (sender, e) =>
    {
      e.Cancel = true;
      delayCanceled = true;
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
      int delaySteps = seconds * 10;
      for (int i = 0; i < delaySteps; i++)
      {
        if (delayCanceled) return false;
        await Task.Delay(100);
        if (!Console.IsInputRedirected && Console.KeyAvailable)
        {
          while (Console.KeyAvailable) Console.ReadKey(intercept: true);
          Console.WriteLine($"\n[System] {message}");
        }
      }
      return true;
    }
    finally
    {
      Console.CancelKeyPress -= cancelHandler;
    }
  }
}