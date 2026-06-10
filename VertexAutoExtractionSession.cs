using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Infrastructure;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using Config; // Added for LatexRefinementConfig
using DirectChatAiInteraction; // For LatexRefinementSession

namespace AutoExtraction;

/// <summary>
/// [AI Context] Orchestrates the enterprise-grade automated transcription pipeline using Vertex AI.
/// Handles stringent GCS bucket cleanups after each chunk to prevent runaway cloud storage billing.
/// [Human] Enterprise-Version der Batch-Verarbeitung. Löscht zwingend die Cloud-Speicher-Uploads nach jedem Video, um GCP-Kosten zu minimieren.
/// </summary>
public class VertexAutoExtractionSession
{
  private readonly Client _client;
  private readonly VertexAutoExtractionConfig _config;
  private readonly Client _aiStudioClientForRefinement; // New: Client for AI Studio for RefinementSession
  private readonly LatexRefinementConfig _latexRefinementConfig; // New: Config for RefinementSession
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private int _sessionTotalInputTokens = 0;
  private int _sessionTotalOutputTokens = 0;
  // [AI Context] Enterprise session state. Note the absence of the REPL loop, as this is intended for unattended bulk operations.

  public VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger, Client aiStudioClientForRefinement, LatexRefinementConfig latexRefinementConfig)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
    _sessionLogger = sessionLogger;
    _aiStudioClientForRefinement = aiStudioClientForRefinement; // Initialize
    _latexRefinementConfig = latexRefinementConfig; // Initialize
  }

  /// <summary>
  /// [AI Context] Main execution loop for the Vertex AI batch processing. Enforces chronological order and strict caching.
  /// [Human] Die Hauptschleife für die Vertex-Verarbeitung. Arbeitet die Videos strikt chronologisch ab und bereitet die Umgebung vor.
  /// </summary>
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

    // If no specific target folder is provided in config, create one inside the source folder.
    if (string.IsNullOrWhiteSpace(_config.TargetFolder))
    {
      _config.TargetFolder = Path.Combine(_config.SourceFolder, "extracted_output");
    }
    if (!Directory.Exists(_config.TargetFolder))
    {
      Directory.CreateDirectory(_config.TargetFolder);
    }

    await CleanupBucketAsync(); // Clean up before starting

    _config.Model = await SelectModelAsync();

    Console.WriteLine("\nVerarbeitungsmodus wählen:");
    Console.WriteLine(" 1) Ein einzelnes Video interaktiv auswählen");
    Console.WriteLine(" 2) Alle Videos im Quellordner verarbeiten");
    Console.Write("Auswahl (1-2) [Standard: 2]: ");
    string modeChoice = Console.ReadLine()?.Trim() ?? "2";

    string[] filesToProcess;
    if (modeChoice == "1")
    {
      filesToProcess = FfmpegUtilities.ConsoleUiHelper.SelectSingleFile(_config.SourceFolder);
    }
    else
    {
      filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    }

    if (filesToProcess == null || filesToProcess.Length == 0)
    {
      Console.WriteLine("[AutoExtraction] Keine Dateien zum Verarbeiten gefunden oder Auswahl abgebrochen.");
      return;
    }

    filesToProcess = filesToProcess.OrderBy(f => VideoDateParser.Parse(f).Date).ToArray();

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
    string historyFileList = "";
    Console.Write($"\nHistory (alte Chat-Verläufe) aus den konfigurierten Pfaden mitschicken? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j")
    {
      var distinctFiles = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);

      if (distinctFiles.Any())
      {
        Console.WriteLine("\n  [INFO] Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
        historyFileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {historyFileList}");
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

    if (historyWasLoaded && historyParts.Any())
    {
      var historyPromptParts = new List<Part>(historyParts);
      historyPromptParts.Add(new Part { Text = $"Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {_config.Model}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards." });
      sessionPreamble.Add(new Content { Role = "user", Parts = historyPromptParts });

      var requestConfig = new GenerateContentConfig { Temperature = 0.0f, MaxOutputTokens = 1024 };
      if (!string.IsNullOrWhiteSpace(systemInstruction))
      {
        requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
      }
      // ThinkingLevel is not supported by the current SDK's ThinkingConfig.
      // If this functionality is intended, please check for SDK updates or alternative configuration methods.
      if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase))
      {
        if (_config.ThinkingBudget.HasValue)
        {
          requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
        }
      }

      Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.Model}: ");
      string fullResponse = "";
      int backoff = 45;
      int maxRetries = 5;
      bool success = false;
      int finalInputTokens = 0;
      int finalOutputTokens = 0;

      for (int attempt = 1; attempt <= maxRetries; attempt++)
      {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
        Console.CancelKeyPress += cancelHandler;

        try
        {
          if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");
          int requestInputTokens = 0;
          int requestOutputTokens = 0;

          var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, sessionPreamble, requestConfig);
          await foreach (var chunk in responseStream.WithCancellation(cts.Token))
          {
            if (cts.IsCancellationRequested) break;
            string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
            Console.Write(txt);
            fullResponse += txt;
            if (chunk.UsageMetadata != null)
            {
              if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
              if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
            }
          }

          _sessionTotalInputTokens += requestInputTokens;
          _sessionTotalOutputTokens += requestOutputTokens;
          finalInputTokens = requestInputTokens;
          finalOutputTokens = requestOutputTokens;
          Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens} (inkl. Thinking Tokens)");
          Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

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

          if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase))
          {
            var metricMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Quota exceeded for metric: ([^,]+)");
            if (metricMatch.Success) Console.WriteLine($"  [Quota-Info] Limit erreicht für: {metricMatch.Groups[1].Value.Trim()}");

            var retryTimeMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Please retry in ([^s]+s)");
            if (retryTimeMatch.Success) Console.WriteLine($"  [Quota-Info] API-Sperre aktiv für: {retryTimeMatch.Groups[1].Value}");
          }

          bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
          if (isOverloaded && attempt < maxRetries)
          {
            // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
            // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
            // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
            // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
            int waitTime;
            string contextMsg = " [History Bestätigung]";
            // [Human] Sonderbehandlung für "high demand"-Fehler: Feste Wartezeit von 3 Minuten.
            if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase))
            {
              waitTime = 180; // 3 Minuten
              Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
              backoff = waitTime;
            }
            else if (attempt == 1)
            {
              var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
              if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay))
              {
                waitTime = serverSuggestedDelay + 20;
                Console.WriteLine($"\n[Rate Limit]{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
              }
              else
              {
                waitTime = backoff;
                Console.WriteLine($"\n[Rate Limit / Überlastung]{contextMsg} Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
              }
              backoff = waitTime;
            }
            else
            {
              backoff += 30;
              waitTime = backoff;
              Console.WriteLine($"\n[Rate Limit]{contextMsg} Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
            }
            if (!await ExtractionHelpers.SmartDelayAsync(waitTime)) { break; }
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
        sessionPreamble.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
        string logMsg = $"[History Acknowledgment] Angehängte Dateien: {historyFileList}\n\nPrompt:\n{historyPromptParts.Last().Text}";
        await _sessionLogger.LogChatAsync(logMsg, logMsg, _config.Model, fullResponse, "VertexAutoExtractionSetup", finalInputTokens, finalOutputTokens);
      }
      else
      {
        Console.WriteLine("\n[FEHLER] Konnte Bestätigung für History nicht erhalten. Breche Extraktion ab.");
        return;
      }
    }

    Console.WriteLine($"[AutoExtraction] {filesToProcess.Length} Datei(en) gefunden. Starte Verarbeitung...");

    var toolkit = new FfmpegUtilities.FfmpegToolkit();

    bool hasErrors = false;

    foreach (var file in filesToProcess) // Fix: Removed tmpFolder parameter from this loop as it's created inside ProcessSingleFileAsync
    {
      bool success = await ProcessSingleFileAsync(file, toolkit, sessionPreamble, systemInstruction);
      if (!success) hasErrors = true;
    }

    if (hasErrors)
    {
      Console.WriteLine("\n[AutoExtraction] Vertex Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).");
    }
    else
    {
      Console.WriteLine("\n[AutoExtraction] Vertex Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!");
    }
  }

  private async Task<string> SelectModelAsync()
  {
    Console.WriteLine("\n=== Model Selection (Vertex AI Enterprise) ===");
    Console.WriteLine("Wähle ein Modell für die Batch-Extraktion:");
    Console.WriteLine(" 1) gemini-3.1-flash-lite-preview || (Most cost-efficient)");
    Console.WriteLine(" 2) gemini-3-flash-preview");
    Console.WriteLine(" 3) gemini-3.1-pro-preview        || (High logic, expensive)");
    Console.WriteLine(" 4) gemini-2.5-flash              || (Recommended default)");
    Console.WriteLine(" 5) gemini-2.5-flash-lite");
    Console.WriteLine(" 6) gemini-2.5-pro");
    Console.WriteLine(" 7) gemini-1.5-flash");
    Console.WriteLine(" 8) gemini-1.5-pro");
    Console.WriteLine(" 9) gemini-robotics-er-1.6-preview");

    Console.Write($"Auswahl (1-9) [Aktuell: {_config.Model}]: ");
    string choice = Console.ReadLine()?.Trim() ?? "";

    if (string.IsNullOrEmpty(choice)) return _config.Model;

    string selected = choice switch
    {
      "1" => "gemini-3.1-flash-lite-preview",
      "2" => "gemini-3-flash-preview",
      "3" => "gemini-3.1-pro-preview",
      "4" => "gemini-2.5-flash",
      "5" => "gemini-2.5-flash-lite",
      "6" => "gemini-2.5-pro",
      "7" => "gemini-1.5-flash",
      "8" => "gemini-1.5-pro",
      "9" => "gemini-robotics-er-1.6-preview",
      _ => choice.Contains("-") ? choice : _config.Model
    };

    Console.WriteLine($"  [INFO] Modell gesetzt auf: {selected}");
    return selected;
  }

  private async Task<bool> ProcessSingleFileAsync(string file, FfmpegUtilities.FfmpegToolkit toolkit, List<Content> sessionPreamble, string systemInstruction) // Fix: Removed tmpFolder parameter
  {
    string targetFilePath = Path.Combine(_config.TargetFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
    bool fileProcessingSuccess = true;

    string baseName = Path.GetFileNameWithoutExtension(file);
    double fullOriginalVideoDuration = await toolkit.GetVideoDurationAsync(file); // Get original video duration for offset calculation

    string fullOutputTextRaw = ""; // Stores text as is, no timestamp adjustment
    string fullOutputTextOffsetted = ""; // Stores text with timestamps adjusted by partStartTimeSeconds from its segment

    // Create a file-specific output folder within the main target folder for this video
    string fileSpecificOutputFolder = Path.Combine(_config.TargetFolder, baseName);
    if (!Directory.Exists(fileSpecificOutputFolder))
    {
      Directory.CreateDirectory(fileSpecificOutputFolder);
    }
    // Create a file-specific temporary folder inside the file-specific output folder
    string tmpFolderForFile = Path.Combine(fileSpecificOutputFolder, "tmp");
    if (!Directory.Exists(tmpFolderForFile))
    {
      Directory.CreateDirectory(tmpFolderForFile);
    }

    try
    {
      Console.WriteLine($"\n[Verarbeite] {Path.GetFileName(file)}...");
      List<string> generatedTexFiles = new List<string>();
      int fileTotalInputTokens = 0; // To track total tokens for this video
      int fileTotalOutputTokens = 0; // To track total tokens for this video
      List<(string FilePath, double StartTime)> videoParts = await PrepareVideoPartsAsync(file, toolkit, tmpFolderForFile, fullOriginalVideoDuration);


      for (int i = 0; i < videoParts.Count; i++)
      {
        string partFile = videoParts[i].FilePath;
        double partStartTimeSeconds = videoParts[i].StartTime;
        string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}.tex"); // Fix: Use fileSpecificOutputFolder
        Console.WriteLine($"\n  [Verarbeite] Teil {i + 1}/{videoParts.Count}...");

        if (System.IO.File.Exists(targetPartPath))
        {
          Console.WriteLine($"  [Resume] Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(targetPartPath)}. Überspringe API-Extraktion für diesen Teil.");
          string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
          generatedTexFiles.Add(targetPartPath);
          fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + DocumentUtilities.LatexTimestampHelper.ExtractContentWithoutTimestampHeader(existingTex); // For raw output
          fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + DocumentUtilities.LatexTimestampHelper.AdjustTimestamps(DocumentUtilities.LatexTimestampHelper.ExtractContentWithoutTimestampHeader(existingTex), partStartTimeSeconds); // For offsetted output
        }

        if (i > 0)
        {
          Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
          await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...");
        }

        var (cleanTex, partInputTokens, partOutputTokens) = await ProcessVideoPartAsync(partFile, i, videoParts.Count, file, sessionPreamble, generatedTexFiles, systemInstruction, partStartTimeSeconds); // Fix: Pass partStartTimeSeconds
        if (string.IsNullOrEmpty(cleanTex))
        {
          Console.WriteLine($"\n[FEHLER] Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
          fileProcessingSuccess = false;
          break;
        }

        fileTotalInputTokens += partInputTokens;
        fileTotalOutputTokens += partOutputTokens;

        fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Tokens: Input {partInputTokens}, Output {partOutputTokens}) ---\n" + cleanTex; // For raw output
        fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Tokens: Input {partInputTokens}, Output {partOutputTokens}) ---\n" + DocumentUtilities.LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds); // For offsetted output

        string partHeader = $"% PART_START_SECONDS: {partStartTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}\n";
        string uniqueTargetPartPath = GetUniqueTexPath(targetPartPath);
        await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + cleanTex);
        generatedTexFiles.Add(uniqueTargetPartPath);

        // [AI Context] Cost Mitigation Strategy:
        // Vertex requires actual files residing in a GCS Bucket. Frequent cleanups prevent runaway cloud storage billing.
        await CleanupBucketAsync();
      }

      if (fileProcessingSuccess)
      {
        // Write the raw (unoffsetted) combined .tex file
        string uniqueTargetFilePath = GetUniqueTexPath(Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + ".tex")); // Fix: Use fileSpecificOutputFolder
        string header = $"% ==========================================\n% AutoExtraction Source: {Path.GetFileName(file)}\n% Model: {_config.Model}\n% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n% Total Tokens (Input: {fileTotalInputTokens}, Output: {fileTotalOutputTokens})\n% ==========================================\n\n";
        await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, header + fullOutputTextRaw);
        Console.WriteLine($"  [Erfolg] Komplettes Dokument gespeichert unter: {uniqueTargetFilePath}");

        // Write the offsetted combined .tex fileameWithoutExtension(file)}-offset.tex");
        string uniqueTargetFilePathOffset = GetUniqueTexPath(Path.Combine(fileSpecificOutputFolder, $"{Path.GetFileNameWithoutExtension(file)}-offset.tex"));
        await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, header + fullOutputTextOffsetted);
        Console.WriteLine($"  [Erfolg] Offset-korrigiertes Dokument gespeichert unter: {uniqueTargetFilePathOffset}"); // Corrected to use the unique path

        // New: Trigger LatexRefinementSession immediately for the generated offset file
        Console.WriteLine("\n[AutoExtraction] Starte automatischen Refinement-Prozess für die offset-korrigierte Datei...");
        var refinementSession = new DirectChatAiInteraction.LatexRefinementSession(_aiStudioClientForRefinement, _latexRefinementConfig, uniqueTargetFilePathOffset);
        await refinementSession.StartAsync();
      }

      return fileProcessingSuccess;
    }
    catch (Exception ex) // General catch for this file's processing
    {
      Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
      Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
      Console.WriteLine($"  [Fehler] Abbruch bei {Path.GetFileName(file)}.");
      return false;
    }
    finally
    {
      // ALWAYS clean up GCS after each file to minimize enterprise storage costs!
      await CleanupBucketAsync();
    }
  }

  private string GetUniqueTexPath(string originalPath)
  {
    if (!System.IO.File.Exists(originalPath))
    {
      return originalPath;
    }

    Console.WriteLine($"  [Hinweis] Zieldatei '{Path.GetFileName(originalPath)}' existiert bereits.");
    string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
    string baseName = Path.GetFileNameWithoutExtension(originalPath);
    string ext = Path.GetExtension(originalPath);
    int copyIndex = 1;
    string newPath;
    do
    {
      newPath = Path.Combine(dir, $"{baseName}-copy-{copyIndex}{ext}");
      copyIndex++;
    } while (System.IO.File.Exists(newPath));

    Console.WriteLine($"  [Info] Neue Datei wird erstellt: '{Path.GetFileName(newPath)}'");
    return newPath;
  }

  private async Task<List<(string FilePath, double StartTime)>> PrepareVideoPartsAsync(string file, FfmpegUtilities.FfmpegToolkit toolkit, string tmpFolder, double fullOriginalVideoDuration)
  {
    string baseName = Path.GetFileNameWithoutExtension(file);
    string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
    var cachedParts = Directory.GetFiles(tmpFolder, $"{baseName}-{dateStr}-part*.mp4").ToList();
    bool useCache = false;

    bool isCacheRecent = cachedParts.Count > 0 && (DateTime.Now - new FileInfo(cachedParts[0]).LastWriteTime).TotalHours <= 2;

    if (isCacheRecent && cachedParts.Count >= 3)
    {
      useCache = true;
    }
    else if (isCacheRecent)
    {
      Console.WriteLine($"\n  [Cache] Ignoriere unvollständigen Cache für {baseName} ({cachedParts.Count} Teil(e) gefunden, erwartet: 3). FFmpeg wird neu gestartet...");
      foreach (var f in cachedParts)
      {
        try { System.IO.File.Delete(f); }
        catch (Exception ex)
        {
          Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
          Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
          Console.WriteLine($"  [Cache] Fehler beim Löschen der gecachten Datei {f}");
        }
      }
    }

    List<(string FilePath, double StartTime)> videoParts = new List<(string, double)>();

    // Need to get the duration of the processed video to calculate start times for cached parts
    string processedVideoPath = Path.Combine(tmpFolder, $"{baseName}-speed-{_config.SpeedMultiplier.ToString(CultureInfo.InvariantCulture)}-compressed.mp4");
    double processedVideoDuration = await toolkit.GetVideoDurationAsync(processedVideoPath);
    double segmentLengthForCached = (processedVideoDuration > 0) ? (processedVideoDuration + (3 - 1) * 180) / 3 : 0; // Assuming parts=3, overlap=180

    if (useCache)
    {
      Console.WriteLine($"  [Cache] FFmpeg übersprungen für '{file}'. Verwende folgende gecachte Dateien (jünger als 2h):");
      cachedParts.Sort();
      for (int i = 0; i < cachedParts.Count; i++)
      {
        double startTime = (segmentLengthForCached > 0) ? i * (segmentLengthForCached - 180) : 0;
        Console.WriteLine($"    - {cachedParts[i]} (Est. Start: {startTime.ToString("F2", CultureInfo.InvariantCulture)}s)");
        videoParts.Add((cachedParts[i], startTime));
      }
    }
    else
    {
      Console.WriteLine($"  Schritt 1: Konvertiere Video für Vertex (1 FPS, 720p, Mono, {_config.SpeedMultiplier}x Speed)...");
      string? processedVideo = await toolkit.ProcessGeneralVideoAsync(file, tmpFolder, speedMultiplier: _config.SpeedMultiplier, fps: 1, downmixToMono: true, scaleTo720p: true);
      if (processedVideo == null)
      {
        Console.WriteLine($"  [Fehler] Konvertierung fehlgeschlagen. Überspringe.");
        return videoParts;
      }

      Console.WriteLine("  Schritt 2: Schneide Video in Teile mit Overlap...");
      var rawPartsWithTimes = await toolkit.ProcessSplitVideoAsync(processedVideo, tmpFolder, parts: 3, overlapSeconds: 180, downmixToMono: false, streamCopy: true, overwrite: true);
      if (rawPartsWithTimes.Count == 0)
      {
        Console.WriteLine($"  [Fehler] Splitten fehlgeschlagen. Überspringe.");
        return videoParts;
      }

      for (int i = 0; i < rawPartsWithTimes.Count; i++)
      {
        string safePartPath = Path.Combine(tmpFolder, $"{baseName}-{dateStr}-part{i + 1}.mp4");
        if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
        System.IO.File.Move(rawPartsWithTimes[i].FilePath, safePartPath);
        videoParts.Add((safePartPath, rawPartsWithTimes[i].StartTime));
      }

      /*      // [Human] Temporäres Basis-Video (das ungeteilte 1FPS Video) aufräumen, um Festplattenspeicher zu sparen!
      try {
        if (File.Exists(processedVideo)) File.Delete(processedVideo);
      }
      catch { }
      */
    }

    return videoParts;
  }

  private async Task<(string texOutput, int inputTokens, int outputTokens)> ProcessVideoPartAsync(string partFile, int partIndex, int totalParts, string originalFile, List<Content> sessionPreamble, List<string> generatedTexFiles, string systemInstruction, double partStartTimeSeconds) // Fix: Added partStartTimeSeconds
  {
    string prompt = _config.Prompt;
    var dateInfo = VideoDateParser.Parse(originalFile);

    prompt += $"\n\nAs a reminder: You are currently transcribing Part {partIndex + 1} of {totalParts} from this lecture.";

    if (partIndex > 0)
    {
      prompt += "\n\nNote: Start the transcription EXACTLY where the professor starts in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction whatsoever.";
    }

    prompt += "\n\nIMPORTANT: Do NOT calculate any time offset for the 'spoken-clean' environment. You may start normally at 00:00:00. Furthermore, do NOT calculate any time scaling factor for the speed adjustments. Just transcribe the timestamps exactly as they appear in the video player.";
    prompt += "\n\nTranscribe more content into the 'spoken-clean' environment rather than less. Do NOT attempt to merge the current part with the previous parts. A dedicated post-processing script will handle the final merging and duplicate removal later. Just focus on transcribing the currently uploaded video. Ensure that related mathematical derivations and explanations are grouped together within a single 'math-stroke' environment to keep the logical flow cohesive, self-contained and unbroken.";

    var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
    if (!uploadSuccess || !attachmentParts.Any())
    {
      Console.WriteLine($"\n  [Fehler] Upload fehlgeschlagen für Teil {partIndex + 1}. Überspringe.");
      return (string.Empty, 0, 0);
    }

    var userPromptParts = new List<Part>();

    if (generatedTexFiles.Any())
    {
      Console.WriteLine("  [Kontext] Sende folgende bereits generierte .tex-Dateien als Kontext mit:");
      string contextText = "Here are the context files from the previous parts of the lecture:\n\n";
      foreach (var texFile in generatedTexFiles)
      {
        Console.WriteLine($"    - {Path.GetFileName(texFile)}");
        string content = await System.IO.File.ReadAllTextAsync(texFile);
        contextText += $"=== REFERENCE CONTEXT: {Path.GetFileName(texFile)} ===\n{content}\n=== END OF REFERENCE CONTEXT ===\n\n";
      }
      userPromptParts.Add(new Part { Text = contextText.TrimEnd() });
    }

    userPromptParts.AddRange(attachmentParts);
    userPromptParts.Add(new Part { Text = parsedPrompt });

    var contents = new List<Content>();
    contents.AddRange(sessionPreamble);
    contents.Add(new Content { Role = "user", Parts = userPromptParts });

    var requestConfig = new GenerateContentConfig
    {
      Temperature = 0.0f,
      MaxOutputTokens = 65535
    };

    if (!string.IsNullOrWhiteSpace(systemInstruction)) requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
    // ThinkingLevel is not supported by the current SDK's ThinkingConfig.
    // If this functionality is intended, please check for SDK updates or alternative configuration methods.
    if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase))
    {
      if (_config.ThinkingBudget.HasValue)
      {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
      }
    }

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
    Console.CancelKeyPress += cancelHandler;

    string fullResponse = "";
    int currentRequest = 1;
    int maxRequestsPerPart = 6;

    int interactionInputTokens = 0;
    int interactionOutputTokens = 0;

    string logContext = $"[Part {partIndex + 1}] {Path.GetFileName(originalFile)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
    if (generatedTexFiles.Any())
    {
      logContext += $"\n[Kontext-Dateien]: {string.Join(", ", generatedTexFiles.Select(Path.GetFileName))}";
    }
    logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";
    string currentLogPrompt = logContext;

    while (true)
    {
      Console.WriteLine($"  [API] Sende Anfrage für Part {partIndex + 1} an {_config.Model} (Request {currentRequest}/{maxRequestsPerPart})...");
      string chunkResp = "";
      int requestInputTokens = 0;
      int requestOutputTokens = 0;
      bool callSuccess = false;

      try
      {
        callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
            streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.Model, contents, requestConfig),
            onChunkReceived: async (chunk) =>
            {
              string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
              Console.Write(txt); // The variable txt is already updated from `chunk.Text ?? ...`, no change needed here.
              chunkResp += txt;
              if (chunk.UsageMetadata != null)
              {
                if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
              }
              await Task.CompletedTask;
            },
              cancellationToken: cts.Token,
              retryContext: $"Teil {partIndex + 1} von {Path.GetFileName(originalFile)}"
        );
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Fahre mit nächstem Teil fort.");
        Console.WriteLine($"Finaler Fehler: {ex.Message}");
        break;
      }

      if (!callSuccess)
      {
        Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
        break;
      }

      interactionInputTokens += requestInputTokens;
      interactionOutputTokens += requestOutputTokens;
      _sessionTotalInputTokens += requestInputTokens;
      _sessionTotalOutputTokens += requestOutputTokens;

      Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens} (inkl. Thinking Tokens)");
      Console.WriteLine($"  [Part Total Tokens] Input: {interactionInputTokens} | Output: {interactionOutputTokens} (inkl. Thinking Tokens)");
      Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

      fullResponse += chunkResp;
      await _sessionLogger.LogChatAsync(currentLogPrompt, currentLogPrompt, _config.Model, chunkResp, "VertexAutoExtraction", requestInputTokens, requestOutputTokens);

      bool segmentComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      bool videoComplete = System.Text.RegularExpressions.Regex.IsMatch(chunkResp, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      if (videoComplete) break;

      if (currentRequest >= maxRequestsPerPart)
      {
        Console.WriteLine($"\n\n[WARNUNG] Maximale Anzahl an Requests ({maxRequestsPerPart}) für diesen Teil erreicht. Breche ab.\n  Teil: {partFile}");
        break;
      }

      string continuePrompt = segmentComplete ? "Continue" :
          $"[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
          $"```latex\n{(chunkResp.Length > 300 ? "...\n" + chunkResp.Substring(chunkResp.Length - 300) : chunkResp)}\n```\n\n" +
          "Please \"continue\" exactly where you left off...";

      if (segmentComplete) Console.WriteLine("\n  [Vertex] Segment-Limit erreicht. Sende 'Continue'...");
      else Console.WriteLine("\n  [Vertex] Unerwartetes Ende der Antwort (Max Tokens?). Bereite automatisierten 'Continue'-Prompt vor...");

      contents.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = chunkResp } } });
      contents.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = continuePrompt } } });
      currentLogPrompt = $"[Continue Prompt für Part {partIndex + 1}]:\n{continuePrompt}";

      if (!await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...")) break;
      currentRequest++;
    }

    Console.CancelKeyPress -= cancelHandler;

    return (ExtractionHelpers.CleanLatexResponse(fullResponse), interactionInputTokens, interactionOutputTokens);
  }

  /// <summary>
  /// [AI Context] Financial Guardrail: Ensures the cloud storage bucket is purged immediately after processing to prevent accumulating storage costs for massive temporary video files.
  /// [Human] Löscht sofort nach der Verarbeitung alle temporären Videodateien aus dem Cloud-Speicher, um unnötige GCP-Kosten zu vermeiden.
  /// </summary>
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
      Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
      Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
      Console.WriteLine($"  [GCS Warnung] Konnte Bucket nicht bereinigen.");
    }
  }
}