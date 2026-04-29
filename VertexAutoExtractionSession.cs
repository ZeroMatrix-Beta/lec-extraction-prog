using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DirectChatAiInteraction;
using Infrastructure;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Orchestrates the enterprise-grade automated transcription pipeline using Vertex AI.
/// Handles stringent GCS bucket cleanups after each chunk to prevent runaway cloud storage billing.
/// [Human] Enterprise-Version der Batch-Verarbeitung. Löscht zwingend die Cloud-Speicher-Uploads nach jedem Video, um GCP-Kosten zu minimieren.
/// </summary>
public class VertexAutoExtractionSession {
  private readonly Client _client;
  private readonly VertexAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private int _sessionTotalInputTokens = 0;
  private int _sessionTotalOutputTokens = 0;
  // [AI Context] Enterprise session state. Note the absence of the REPL loop, as this is intended for unattended bulk operations.

  public VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger) {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
    _sessionLogger = sessionLogger;
  }

  /// <summary>
  /// [AI Context] Main execution loop for the Vertex AI batch processing. Enforces chronological order and strict caching.
  /// [Human] Die Hauptschleife für die Vertex-Verarbeitung. Arbeitet die Videos strikt chronologisch ab und bereitet die Umgebung vor.
  /// </summary>
  public async Task StartAsync() {
    Console.WriteLine("\n[AutoExtraction] Starte Vertex AI Enterprise Extraction Session...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    if (!Directory.Exists(_config.SourceFolder)) {
      Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
      return;
    }

    if (!Directory.Exists(_config.TargetFolder)) {
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
    if (modeChoice == "1") {
      filesToProcess = FfmpegUtilities.ConsoleUiHelper.SelectSingleFile(_config.SourceFolder);
    }
    else {
      filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    }

    if (filesToProcess == null || filesToProcess.Length == 0) {
      Console.WriteLine("[AutoExtraction] Keine Dateien zum Verarbeiten gefunden oder Auswahl abgebrochen.");
      return;
    }

    filesToProcess = filesToProcess.OrderBy(f => VideoDateParser.Parse(f).Date).ToArray();

    _sessionLogger.InitializeSession();

    string systemInstruction = "";
    Console.Write($"\nSystem Instruction aus '{_config.SystemInstructionPath}' laden? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j" && System.IO.File.Exists(_config.SystemInstructionPath)) {
      systemInstruction = await System.IO.File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"  [INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }

    var historyParts = new List<Part>();
    bool historyWasLoaded = false;
    Console.Write($"\nHistory (alte Chat-Verläufe) aus den konfigurierten Pfaden mitschicken? (j/n): ");
    if (Console.ReadLine()?.Trim().ToLower() == "j") {
      var distinctFiles = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);

      if (distinctFiles.Any()) {
        Console.WriteLine("\n  [INFO] Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
        string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}");
        if (success && attachmentParts.Any()) {
          historyParts.AddRange(attachmentParts);
          historyWasLoaded = true;
          Console.WriteLine("  [INFO] History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
        }
        else {
          Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
        }
      }
    }

    var sessionPreamble = new List<Content>();

    bool loadedSysPrompt = !string.IsNullOrEmpty(systemInstruction);
    _sessionLogger.SetSessionMetadata(loadedSysPrompt, historyWasLoaded);
    await _sessionLogger.LogSessionSetupAsync();

    if (historyWasLoaded && historyParts.Any()) {
      var historyPromptParts = new List<Part>(historyParts);
      historyPromptParts.Add(new Part { Text = $"Hier ist das Material aus meiner History. Bitte lies es sorgfältig durch. Bestätige mir den Erhalt ausnahmslos mit exakt folgendem Text: '[AI-Model: {_config.Model}] Material [...] received and analyzed. I am standing by for your instructions.' Warte danach auf meine nächsten Anweisungen." });
      sessionPreamble.Add(new Content { Role = "user", Parts = historyPromptParts });

      var requestConfig = new GenerateContentConfig { Temperature = 0.0f, MaxOutputTokens = 1024 };
      if (!string.IsNullOrWhiteSpace(systemInstruction)) {
        requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
      }
      if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) {
        requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };
      }

      Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.Model}: ");
      string fullResponse = "";
      int backoff = 30;
      int maxRetries = 5;
      bool success = false;

      for (int attempt = 1; attempt <= maxRetries; attempt++) {
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
        Console.CancelKeyPress += cancelHandler;

        try {
          if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");
          int requestInputTokens = 0;
          int requestOutputTokens = 0;

          var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, sessionPreamble, requestConfig);
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
          break; // Success
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
          Console.WriteLine("\n[INFO] Bestätigung durch Benutzer abgebrochen.");
          break;
        }
        catch (Exception ex) {
          Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
          Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

          if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) {
            var metricMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Quota exceeded for metric: ([^,]+)");
            if (metricMatch.Success) Console.WriteLine($"  [Quota-Info] Limit erreicht für: {metricMatch.Groups[1].Value.Trim()}");

            var retryTimeMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Please retry in ([^s]+s)");
            if (retryTimeMatch.Success) Console.WriteLine($"  [Quota-Info] API-Sperre aktiv für: {retryTimeMatch.Groups[1].Value}");
          }

          bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase);
          if (isOverloaded && attempt < maxRetries) {
            var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
            if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
              int waitTime = serverSuggestedDelay + 10;
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
        sessionPreamble.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
        await _sessionLogger.LogChatAsync("[History Acknowledgment]", historyPromptParts.Last().Text ?? "", _config.Model, fullResponse, "AutoExtractionSetup");
      }
      else {
        Console.WriteLine("\n[FEHLER] Konnte Bestätigung für History nicht erhalten. Breche Extraktion ab.");
        return;
      }
    }

    Console.WriteLine($"[AutoExtraction] {filesToProcess.Length} Datei(en) gefunden. Starte Verarbeitung...");

    var toolkit = new FfmpegUtilities.FfmpegToolkit();
    string tmpFolder = Path.Combine(_config.TargetFolder, "tmp");
    if (!Directory.Exists(tmpFolder)) Directory.CreateDirectory(tmpFolder);

    foreach (var file in filesToProcess) {
      await ProcessSingleFileAsync(file, toolkit, tmpFolder, sessionPreamble, systemInstruction);
    }
    Console.WriteLine("\n[AutoExtraction] Vertex Batch-Verarbeitung abgeschlossen!");
  }

  private async Task<string> SelectModelAsync() {
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

    string selected = choice switch {
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

  private async Task ProcessSingleFileAsync(string file, FfmpegUtilities.FfmpegToolkit toolkit, string tmpFolder, List<Content> sessionPreamble, string systemInstruction) {
    string targetFilePath = Path.Combine(_config.TargetFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
    if (System.IO.File.Exists(targetFilePath)) {
      Console.WriteLine($"\n[Übersprungen] {Path.GetFileName(file)} wurde bereits verarbeitet.");
      return;
    }

    try {
      Console.WriteLine($"\n[Verarbeite] {Path.GetFileName(file)}...");

      List<string> videoParts = await PrepareVideoPartsAsync(file, toolkit, tmpFolder);
      if (videoParts == null || videoParts.Count == 0) return;

      List<string> generatedTexFiles = new List<string>();
      string fullOutputText = "";
      string baseName = Path.GetFileNameWithoutExtension(file);

      for (int i = 0; i < videoParts.Count; i++) {
        string partFile = videoParts[i];
        string targetPartPath = Path.Combine(_config.TargetFolder, $"{baseName}-part{i + 1}.tex");
        Console.WriteLine($"\n  [Verarbeite] Teil {i + 1}/{videoParts.Count}...");

        if (System.IO.File.Exists(targetPartPath) && new FileInfo(targetPartPath).Length > 0 && (DateTime.Now - new FileInfo(targetPartPath).LastWriteTime).TotalHours <= 2) {
          Console.WriteLine($"  [Resume] Überspringe API-Aufruf. Verwende bereits existierende Datei (jünger als 2h): {Path.GetFileName(targetPartPath)}");
          string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
          fullOutputText += $"\n\n% --- TEIL {i + 1} ---\n" + existingTex;
          generatedTexFiles.Add(targetPartPath);
          continue;
        }

        if (i > 0) {
          Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen...");
          await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...");
        }

        string cleanTex = await ProcessVideoPartAsync(partFile, i, videoParts.Count, file, sessionPreamble, generatedTexFiles, systemInstruction);
        if (string.IsNullOrEmpty(cleanTex)) continue;

        fullOutputText += $"\n\n% --- TEIL {i + 1} ---\n" + cleanTex;

        string partTexFile = Path.ChangeExtension(partFile, ".tex");
        await System.IO.File.WriteAllTextAsync(partTexFile, cleanTex);

        await System.IO.File.WriteAllTextAsync(targetPartPath, cleanTex);
        generatedTexFiles.Add(targetPartPath);

        // [AI Context] Cost Mitigation Strategy:
        // Vertex requires actual files residing in a GCS Bucket. Frequent cleanups prevent runaway cloud storage billing.
        await CleanupBucketAsync();
      }

      string header = $"% ==========================================\n% AutoExtraction Source: {Path.GetFileName(file)}\n% Model: {_config.Model}\n% ==========================================\n\n";
      await System.IO.File.WriteAllTextAsync(targetFilePath, header + fullOutputText);
      Console.WriteLine($"  [Erfolg] Komplettes Dokument gespeichert unter: {targetFilePath}");
    }
    catch (Exception ex) {
      Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
      Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
      Console.WriteLine($"  [Fehler] Abbruch bei {Path.GetFileName(file)}.");
    }
    finally {
      // ALWAYS clean up GCS after each file to minimize enterprise storage costs!
      await CleanupBucketAsync();
    }
  }

  private async Task<List<string>> PrepareVideoPartsAsync(string file, FfmpegUtilities.FfmpegToolkit toolkit, string tmpFolder) {
    string baseName = Path.GetFileNameWithoutExtension(file);
    string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
    var cachedParts = Directory.GetFiles(tmpFolder, $"{baseName}-{dateStr}-part*.mp4").ToList();
    bool useCache = false;

    bool isCacheRecent = cachedParts.Count > 0 && (DateTime.Now - new FileInfo(cachedParts[0]).LastWriteTime).TotalHours <= 2;

    if (isCacheRecent && cachedParts.Count >= 3) {
      useCache = true;
    }
    else if (isCacheRecent) {
      Console.WriteLine($"\n  [Cache] Ignoriere unvollständigen Cache für {baseName} ({cachedParts.Count} Teil(e) gefunden, erwartet: 3). FFmpeg wird neu gestartet...");
      foreach (var f in cachedParts) {
        try { System.IO.File.Delete(f); }
        catch (Exception ex) {
          Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
          Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
          Console.WriteLine($"  [Cache] Fehler beim Löschen der gecachten Datei {f}");
        }
      }
    }

    List<string> videoParts = new List<string>();

    if (useCache) {
      Console.WriteLine($"  [Cache] FFmpeg übersprungen für {Path.GetFileName(file)}. Verwende gecachte Dateien (jünger als 2h).");
      cachedParts.Sort();
      videoParts = cachedParts;
    }
    else {
      Console.WriteLine($"  Schritt 1: Konvertiere Video für Vertex (1 FPS, 720p, Mono, {_config.SpeedMultiplier}x Speed)...");
      string? processedVideo = await toolkit.ProcessGeneralVideoAsync(file, tmpFolder, speedMultiplier: _config.SpeedMultiplier, fps: 1, downmixToMono: true, scaleTo720p: true);

      if (processedVideo == null) {
        Console.WriteLine($"  [Fehler] Konvertierung fehlgeschlagen. Überspringe.");
        return videoParts;
      }

      Console.WriteLine("  Schritt 2: Schneide Video in Teile mit Overlap...");
      var rawParts = await toolkit.ProcessSplitVideoAsync(processedVideo, tmpFolder, parts: 3, overlapSeconds: 180, downmixToMono: false, streamCopy: true);

      if (rawParts.Count == 0) {
        Console.WriteLine($"  [Fehler] Splitten fehlgeschlagen. Überspringe.");
        return videoParts;
      }

      for (int i = 0; i < rawParts.Count; i++) {
        string safePartPath = Path.Combine(tmpFolder, $"{baseName}-{dateStr}-part{i + 1}.mp4");
        if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
        System.IO.File.Move(rawParts[i], safePartPath);
        videoParts.Add(safePartPath);
      }
    }

    return videoParts;
  }

  private async Task<string> ProcessVideoPartAsync(string partFile, int partIndex, int totalParts, string originalFile, List<Content> sessionPreamble, List<string> generatedTexFiles, string systemInstruction) {
    string prompt = _config.Prompt;
    var dateInfo = VideoDateParser.Parse(originalFile);

    prompt += $"\n\n[Meta-Information]: These {totalParts} video parts (and corresponding .tex files) originate from the lecture on {dateInfo.Weekday}, {dateInfo.DateString}. Do not include this date in the compiled LaTeX code right now; it is just for your internal context.";
    prompt += $"\n\nThe uploaded video is part {partIndex + 1} of {totalParts} from this lecture.";
    prompt += $"\n\nThe video is played back / scaled to {_config.SpeedMultiplier}x speed.";

    if (partIndex > 0) {
      prompt += "\n\nThe previously generated LaTeX documents for the prior parts are included in the context (see --- DOKUMENT START ---). Please use them to maintain context continuity.";
      prompt += "\n\nNote: Consecutive video parts have an intentional 3-minute overlap to prevent context loss. If the video starts mid-sentence, use the provided LaTeX context from the previous part to reconstruct the full sentence.";
    }

    prompt += "\n\nIMPORTANT: Do NOT calculate any time offset for the 'spoken-clean' environment. You may start normally at 00:00:00. Furthermore, do NOT calculate any time scaling factor for the speed adjustments. Just transcribe the timestamps exactly as they appear in the video player.";
    prompt += "\n\nTranscribe more content into the 'spoken-clean' environment rather than less. Do NOT attempt to merge the current part with the previous parts. A dedicated post-processing script will handle the final merging and duplicate removal later. Just focus on transcribing the currently uploaded video. Ensure that related mathematical derivations and explanations are grouped together within a single 'math-stroke' environment to keep the logical flow cohesive, self-contained and unbroken.";

    var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
    if (!uploadSuccess || attachmentParts.Count == 0) {
      Console.WriteLine($"\n  [Fehler] Upload fehlgeschlagen für Teil {partIndex + 1}. Überspringe.");
      return string.Empty;
    }

    var userPromptParts = new List<Part>(attachmentParts);

    foreach (var texFile in generatedTexFiles) {
      string content = await System.IO.File.ReadAllTextAsync(texFile);
      userPromptParts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(texFile)}) ---\n{content}\n--- DOKUMENT ENDE ---" });
    }

    userPromptParts.Add(new Part { Text = parsedPrompt });

    var contents = new List<Content>();
    contents.AddRange(sessionPreamble);
    contents.Add(new Content { Role = "user", Parts = userPromptParts });

    var requestConfig = new GenerateContentConfig {
      Temperature = 0.0f,
      MaxOutputTokens = 65535
    };

    if (!string.IsNullOrWhiteSpace(systemInstruction)) requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
    if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
    Console.CancelKeyPress += cancelHandler;

    string outputTextForPart = await GenerateWithContinuationsAsync(contents, requestConfig, partIndex, originalFile, prompt, cts);

    Console.CancelKeyPress -= cancelHandler;

    return ExtractionHelpers.CleanLatexResponse(outputTextForPart);
  }

  private async Task<string> GenerateWithContinuationsAsync(List<Content> contents, GenerateContentConfig requestConfig, int partIndex, string originalFile, string prompt, CancellationTokenSource cts) {
    int backoff = 30;
    int maxRetries = 5;
    string outputTextForPart = "";
    int currentRequest = 1;
    int maxRequests = 15;
    int interactionInputTokens = 0;
    int interactionOutputTokens = 0;

    while (true) {
      var result = await TryStreamChunkAsync(contents, requestConfig, partIndex, currentRequest, maxRequests, maxRetries, backoff, cts);
      backoff = result.newBackoff;

      interactionInputTokens += result.requestInputTokens;
      interactionOutputTokens += result.requestOutputTokens;
      _sessionTotalInputTokens += result.requestInputTokens;
      _sessionTotalOutputTokens += result.requestOutputTokens;

      Console.WriteLine($"\n  [Request Tokens] Input: {result.requestInputTokens} | Output: {result.requestOutputTokens}");
      Console.WriteLine($"  [Part Total Tokens] Input: {interactionInputTokens} | Output: {interactionOutputTokens}");
      Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

      if (result.userCancelled) break;

      outputTextForPart += result.chunkOutput;
      await _sessionLogger.LogChatAsync($"[Part {partIndex + 1}] {Path.GetFileName(originalFile)}", prompt ?? "", _config.Model, result.chunkOutput, "VertexAutoExtraction");

      bool segmentComplete = System.Text.RegularExpressions.Regex.IsMatch(result.chunkOutput, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      bool videoComplete = System.Text.RegularExpressions.Regex.IsMatch(result.chunkOutput, @"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

      if (!videoComplete) {
        if (currentRequest >= maxRequests) {
          Console.WriteLine($"\n  [WARNUNG] Max Requests ({maxRequests}) erreicht. Breche ab.");
          break;
        }

        if (segmentComplete) Console.WriteLine("\n  [Vertex] Segment-Limit erreicht. Bereite 'Continue'-Prompt vor...");
        else if (result.streamDropped) Console.WriteLine("\n  [Vertex] Stream abgebrochen. Bereite automatisierten 'Continue'-Prompt zur Wiederaufnahme vor...");
        else Console.WriteLine("\n  [Vertex] KI hat abgebrochen (Max Tokens). Bereite automatisierten 'Continue'-Prompt vor...");

        string snippet = result.chunkOutput.Length > 300 ? "...\n" + result.chunkOutput.Substring(result.chunkOutput.Length - 300) : result.chunkOutput;
        string continuePrompt = "[IMPORTANT] Your response has been cut by the system's automatic length-detection. Your last latex block ended with:\n\n" +
                                $"```latex\n{snippet}\n```\n\n" +
                                "Please \"continue\" exactly where you left off...";

        Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

        contents.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = result.chunkOutput } } });
        contents.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = continuePrompt } } });
        backoff = 30;
        currentRequest++;
        continue;
      }

      break; // Finished
    }

    return outputTextForPart;
  }

  private async Task<(string chunkOutput, bool userCancelled, bool streamDropped, int newBackoff, int requestInputTokens, int requestOutputTokens)> TryStreamChunkAsync(
    List<Content> contents, GenerateContentConfig requestConfig, int partIndex, int currentRequest, int maxRequests, int maxRetries, int backoff, CancellationTokenSource cts) {
    string chunkOutput = "";
    bool streamDropped = false;
    bool userCancelled = false;
    int requestInputTokens = 0;
    int requestOutputTokens = 0;

    int attempt = 1;
    for (; attempt <= maxRetries; attempt++) {
      bool isGenerating = true;
      var inputInterceptorTask = Task.Run(async () => {
        while (isGenerating) {
          if (!Console.IsInputRedirected && Console.KeyAvailable) {
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            Console.WriteLine("\n[AI-Model] Still waiting for the acknowledgment / processing...");
          }
          await Task.Delay(100);
        }
      });

      try {
        if (currentRequest == 1)
          Console.WriteLine($"  [API] Sende initiale Anfrage für Part {partIndex + 1} an {_config.Model} (Request {currentRequest}/{maxRequests}, Versuch {attempt}/{maxRetries})...");
        else
          Console.WriteLine($"  [API] Sende Fortsetzungs-Anfrage (Continue) für Part {partIndex + 1} an {_config.Model} (Request {currentRequest}/{maxRequests}, Versuch {attempt}/{maxRetries})...");

        var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, contents, requestConfig);
        await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
          if (cts.IsCancellationRequested) break;
          string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(txt);
          chunkOutput += txt;
          if (chunk.UsageMetadata != null) {
            if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
            if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
          }
        }

        isGenerating = false;
        await inputInterceptorTask;

        if (cts.IsCancellationRequested) userCancelled = true;
        break;
      }
      catch (Exception ex) {
        isGenerating = false;
        await inputInterceptorTask;

        if (ex is OperationCanceledException && cts.IsCancellationRequested) {
          userCancelled = true;
          break;
        }

        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

        if (ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)) {
          var metricMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Quota exceeded for metric: ([^,]+)");
          if (metricMatch.Success) Console.WriteLine($"  [Quota-Info] Limit erreicht für: {metricMatch.Groups[1].Value.Trim()}");

          var retryTimeMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"Please retry in ([^s]+s)");
          if (retryTimeMatch.Success) Console.WriteLine($"  [Quota-Info] API-Sperre aktiv für: {retryTimeMatch.Groups[1].Value}");
        }

        if (chunkOutput.Length > 100) {
          Console.WriteLine("\n[INFO] Verbindung während der Generierung abgebrochen. Versuche, die unvollständige Antwort zu retten und fortzusetzen...");
          streamDropped = true;
          break;
        }

        bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("500") || ex.ToString().Contains("ServerError") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase);
        if (isOverloaded && attempt < maxRetries) {
          var retryMatch = System.Text.RegularExpressions.Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
          if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
            int waitTime = serverSuggestedDelay + 10;
            Console.WriteLine($"\n  [Rate Limit] API schlägt Wartezeit von {serverSuggestedDelay}s vor. Warte {waitTime} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await ExtractionHelpers.SmartDelayAsync(waitTime)) { userCancelled = true; break; }
          }
          else {
            Console.WriteLine($"\n  [Rate Limit / Überlastung / Verbindungsabbruch] Warte {backoff} Sekunden... (Versuch {attempt}/{maxRetries})");
            if (!await ExtractionHelpers.SmartDelayAsync(backoff)) { userCancelled = true; break; }
          }
          backoff *= 2;
        }
        else {
          Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Breche Verarbeitung für diesen Teil ab.");
          userCancelled = true;
          break;
        }
      }
    }

    return (chunkOutput, userCancelled, streamDropped, backoff, requestInputTokens, requestOutputTokens);
  }

  /// <summary>
  /// [AI Context] Financial Guardrail: Ensures the cloud storage bucket is purged immediately after processing to prevent accumulating storage costs for massive temporary video files.
  /// [Human] Löscht sofort nach der Verarbeitung alle temporären Videodateien aus dem Cloud-Speicher, um unnötige GCP-Kosten zu vermeiden.
  /// </summary>
  private async Task CleanupBucketAsync() {
    if (string.IsNullOrWhiteSpace(_config.GcsBucketName)) return;
    try {
      var storageClient = await StorageClient.CreateAsync();
      var objects = storageClient.ListObjectsAsync(_config.GcsBucketName);
      int count = 0;
      await foreach (var obj in objects) {
        await storageClient.DeleteObjectAsync(_config.GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0) Console.WriteLine($"  [GCS] {count} temporäre Datei(en) gelöscht, um Storage-Kosten zu sparen.");
    }
    catch (Exception ex) {
      Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
      Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
      Console.WriteLine($"  [GCS Warnung] Konnte Bucket nicht bereinigen.");
    }
  }
}