using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using DocumentUtilities;
using Config;
using AutoExtraction;
using Infrastructure;
using Google.Cloud.Storage.V1;

namespace DirectChatAiInteraction;

public class LatexRefinementSession {
  private readonly Client _client;
  private readonly LatexRefinementSessionConfig _config;
  private readonly string? _singleFilePathToProcess;
  private readonly string[]? _multipleFilesToProcess;
  private readonly IAutoExtractionConfig? _extractionConfig;
  private readonly string? _audioFilePath;

  public LatexRefinementSession(Client client, LatexRefinementSessionConfig config) {
    _client = client;
    _config = config;
    _singleFilePathToProcess = null;
    _multipleFilesToProcess = null;
    _extractionConfig = null;
    _audioFilePath = null;
  }

  public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string singleFilePathToProcess) {
    _client = client;
    _config = config;
    _singleFilePathToProcess = singleFilePathToProcess;
    _multipleFilesToProcess = null;
    _extractionConfig = null;
    _audioFilePath = null;
  }

  public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string singleFilePathToProcess, IAutoExtractionConfig extractionConfig, string? audioFilePath = null) {
    _client = client;
    _config = config;
    _singleFilePathToProcess = singleFilePathToProcess;
    _multipleFilesToProcess = null;
    _extractionConfig = extractionConfig;
    _audioFilePath = audioFilePath;
  }

  public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string[] multipleFilesToProcess, IAutoExtractionConfig extractionConfig, string? audioFilePath = null) {
    _client = client;
    _config = config;
    _singleFilePathToProcess = null;
    _multipleFilesToProcess = multipleFilesToProcess;
    _extractionConfig = extractionConfig;
    _audioFilePath = audioFilePath;
  }

  public async Task StartAsync() {
    if (!_config.Enabled) {
      Console.WriteLine("\n[INFO] LaTeX Refinement ist in der Konfiguration (LatexRefinementSessionConfig) deaktiviert. Überspringe die Ausführung.");
      return;
    }

    if ((_singleFilePathToProcess != null || _multipleFilesToProcess != null) && _extractionConfig != null) {
      if (!_extractionConfig.GoIntoLatexRefinement || !_extractionConfig.GenerateOffsetFiles || !_extractionConfig.GenerateAudioFile) {
        Console.WriteLine("\n[WARNUNG] LaTeX Refinement übersprungen.");
        Console.WriteLine("  Grund: Die Voraussetzungen in AutoExtractionConfig sind nicht erfüllt.");
        return;
      }
      
      if (_singleFilePathToProcess != null && !System.IO.File.Exists(_singleFilePathToProcess)) {
        Console.WriteLine($"\n[WARNUNG] LaTeX Refinement übersprungen. Die Zieldatei fehlt: {_singleFilePathToProcess}");
        return;
      }

      if (_audioFilePath == null || !System.IO.File.Exists(_audioFilePath)) {
        Console.WriteLine($"\n[INFO] LaTeX Refinement wird ohne Audio-Datei ausgeführt (Pfad: {_audioFilePath ?? "null"}).");
      }
    }

    Console.WriteLine("\n==================================================");
    Console.WriteLine("   Starte LaTeX Refinement Pipeline");
    Console.WriteLine("==================================================");

    await ExecutePipelineAsync();
  }

  private async Task ExecutePipelineAsync() {
    string[] currentFiles;
    string targetFolder;
    string baseName;

    if (_multipleFilesToProcess != null && _multipleFilesToProcess.Length > 0) {
      currentFiles = _multipleFilesToProcess;
      targetFolder = Path.GetDirectoryName(currentFiles[0]) ?? _config.TargetFolder;
      baseName = Path.GetFileNameWithoutExtension(currentFiles[0]).Replace("-part1", "").Replace("-offset", "");
    }
    else if (_singleFilePathToProcess != null) {
      currentFiles = new[] { _singleFilePathToProcess };
      targetFolder = Path.GetDirectoryName(_singleFilePathToProcess) ?? _config.TargetFolder;
      baseName = Path.GetFileNameWithoutExtension(_singleFilePathToProcess).Replace("-offset", "");
    }
    else {
      string sourceFolder = _config.SourceFolder;
      if (!Directory.Exists(sourceFolder)) {
        Console.WriteLine("Ordner nicht gefunden. Bitte prüfe den SourceFolder in der Konfiguration.");
        return;
      }
      currentFiles = Directory.GetFiles(sourceFolder, "*.tex");
      if (currentFiles.Length == 0) return;
      targetFolder = string.IsNullOrWhiteSpace(_config.TargetFolder) ? sourceFolder : _config.TargetFolder;
      baseName = "refined_output";
    }

    // Step 1: Merge and Timestamp Control
    string? step1Output = null;
    if (_config.Step1MergeAndTimestamp.Enabled) {
      Console.WriteLine("\n--- [Schritt 1: Merge & Timestamp Control] ---");
      step1Output = await ExecuteStep1MergeAsync(currentFiles, _audioFilePath, baseName, targetFolder);
      if (step1Output == null) {
        Console.WriteLine("[FEHLER] Schritt 1 fehlgeschlagen. Breche Pipeline ab.");
        return;
      }
      currentFiles = new[] { step1Output };
    }

    // Step 2: Speech Refinement
    string? step2Output = null;
    if (_config.Step2SpeechRefinement.Enabled) {
      Console.WriteLine("\n--- [Schritt 2: Speech Refinement] ---");
      step2Output = await ExecuteStep2SpeechRefinementAsync(currentFiles[0], _audioFilePath, baseName, targetFolder);
      if (step2Output == null) {
        Console.WriteLine("[FEHLER] Schritt 2 fehlgeschlagen. Breche Pipeline ab.");
        return;
      }
      currentFiles = new[] { step2Output };
    }

    // Step 3: Last Refinement
    if (_config.Step3LastRefinement.Enabled) {
      Console.WriteLine("\n--- [Schritt 3: Last Refinement] ---");
      var finalOutput = await ExecuteStep3LastRefinementAsync(currentFiles[0], baseName, targetFolder);
      if (finalOutput == null) {
         Console.WriteLine("[FEHLER] Schritt 3 fehlgeschlagen.");
      }
    }
    
    Console.WriteLine("\n[AutoExtraction] LaTeX Refinement Pipeline erfolgreich abgeschlossen!");
  }

  // Overload that takes string array
  private async Task<string?> ExecuteStep1MergeAsync(string[] inputFiles, string? audioFilePath, string baseName, string targetFolder) {
    if (inputFiles.Length == 0) return null;
    int partsCount = _extractionConfig?.NumberOfParts ?? inputFiles.Length;
    int overlapMin = (_extractionConfig?.OverlapSeconds ?? 180) / 60;
    
    // Upload audio if available
    var parts = new List<Part>();
    string audioLengthStr = "unknown";
    if (audioFilePath != null && System.IO.File.Exists(audioFilePath)) {
      var toolkit = new FfmpegUtilities.FfmpegToolkit();
      double dur = await toolkit.GetVideoDurationAsync(audioFilePath);
      TimeSpan t = TimeSpan.FromSeconds(dur);
      audioLengthStr = $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
      
      var handler = new AttachmentHandler(_client, targetFolder, new[] { targetFolder }, !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
      var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
      if (success) {
          parts.AddRange(attached);
          Console.WriteLine($"  [INFO] Audio-Datei erfolgreich an die API übermittelt: {audioFilePath}");
      }
    }

    string promptText = $"Here is the generated audio file alongside with the combined file with all the offset parts together. " +
                        $"The .tex file was generated with {partsCount} parts by some lecture videos provided with {overlapMin} minutes overlap. " +
                        $"The actual audio length is exactly {audioLengthStr}. The `spoken-clean` blocks timestamps need to perfectly align with this full duration. " +
                        $"Please note that sometimes the timestamps in the `spoken-clean` blocks are horribly misaligned, so each block must be carefully checked and corrected to match the audio.";
    
    parts.Add(new Part { Text = promptText });

    foreach (var file in inputFiles) {
       Console.WriteLine($"  [INFO] Füge Datei als Text in den Prompt ein: {file}");
       string content = await System.IO.File.ReadAllTextAsync(file);
       parts.Add(new Part { Text = $"=== FILE: {Path.GetFileName(file)} ===\n{content}\n=== END FILE ===" });
    }

    string outputFileName = $"{baseName}-offset-merged.tex";
    var result = await ExecuteGenerativeStepAsync(_config.Step1MergeAndTimestamp, parts, targetFolder, outputFileName);
    
    if (_config.UseVertex) {
        await CleanupBucketAsync();
    }
    
    return result;
  }

  // Overload that takes single string
  private async Task<string?> ExecuteStep1MergeAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
     return await ExecuteStep1MergeAsync(new[] { inputFile }, audioFilePath, baseName, targetFolder);
  }

  private async Task<string?> ExecuteStep2SpeechRefinementAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
    var parts = new List<Part>();
    
    if (audioFilePath != null && System.IO.File.Exists(audioFilePath)) {
      var handler = new AttachmentHandler(_client, targetFolder, new[] { targetFolder }, !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
      var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
      if (success) {
          parts.AddRange(attached);
          Console.WriteLine($"  [INFO] Audio-Datei erfolgreich an die API übermittelt: {audioFilePath}");
      }
    }

    parts.Add(new Part { Text = "Please refine the text strictly in between the `spoken-clean` environments according to the system instructions. Do not alter the math or the timestamps." });
    
    Console.WriteLine($"  [INFO] Füge Datei als Text in den Prompt ein: {inputFile}");
    string content = await System.IO.File.ReadAllTextAsync(inputFile);
    parts.Add(new Part { Text = $"=== INPUT TEX ===\n{content}\n=== END INPUT TEX ===" });

    string outputFileName = $"{baseName}-offset-speech_refined.tex";
    var result = await ExecuteGenerativeStepAsync(_config.Step2SpeechRefinement, parts, targetFolder, outputFileName);
    
    if (_config.UseVertex) {
        await CleanupBucketAsync();
    }
    
    return result;
  }

  private async Task<string?> ExecuteStep3LastRefinementAsync(string inputFile, string baseName, string targetFolder) {
    var parts = new List<Part>();
    parts.Add(new Part { Text = "Perform the final refinement and formatting pass on this document according to the system instructions." });
    
    Console.WriteLine($"  [INFO] Füge Datei als Text in den Prompt ein: {inputFile}");
    string content = await System.IO.File.ReadAllTextAsync(inputFile);
    parts.Add(new Part { Text = $"=== INPUT TEX ===\n{content}\n=== END INPUT TEX ===" });

    string outputFileName = $"{baseName}-offset-final.tex";
    var result = await ExecuteGenerativeStepAsync(_config.Step3LastRefinement, parts, targetFolder, outputFileName);
    
    if (_config.UseVertex) {
        await CleanupBucketAsync();
    }
    
    return result;
  }

  private async Task<string?> ExecuteGenerativeStepAsync(RefinementStepConfig stepConfig, List<Part> userPromptParts, string targetOutputFolder, string outputFileName) {
    BackendParameters backendParams = _config.UseVertex ? stepConfig.Vertex : stepConfig.AiStudio;

    string systemInstructionText = "";
    if (stepConfig.SystemInstructionPaths != null && stepConfig.SystemInstructionPaths.Any()) {
        var resolved = ExtractionHelpers.ResolveHistoryFiles(stepConfig.SystemInstructionPaths);
        foreach (var path in resolved) {
             if (System.IO.File.Exists(path)) {
                 Console.WriteLine($"  [INFO] Lade System-Instruktion: {path}");
                 systemInstructionText += await System.IO.File.ReadAllTextAsync(path) + "\n\n";
             } else {
                 Console.WriteLine($"  [WARNUNG] System-Instruktion nicht gefunden und übersprungen: {path}");
             }
        }
    }

    var requestConfig = new GenerateContentConfig {
      Temperature = backendParams.Temperature,
      TopP = backendParams.TopP,
      TopK = backendParams.TopK,
      MaxOutputTokens = backendParams.MaxOutputTokens
    };
    if (!string.IsNullOrWhiteSpace(systemInstructionText)) {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstructionText } } };
    }

    if (backendParams.Model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) || backendParams.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
      if (backendParams.ThinkingBudget.HasValue || !string.IsNullOrEmpty(backendParams.ThinkingLevel)) {
        requestConfig.ThinkingConfig = new ThinkingConfig();
        if (!string.IsNullOrEmpty(backendParams.ThinkingLevel)) {
          requestConfig.ThinkingConfig.ThinkingLevel = backendParams.ThinkingLevel;
        } else if (backendParams.ThinkingBudget.HasValue) {
            requestConfig.ThinkingConfig.ThinkingBudget = backendParams.ThinkingBudget;
        }
      }
    }

    // Inject completion marker constraint
    var finalPromptParts = new List<Part>(userPromptParts);
    finalPromptParts.Add(new Part { Text = "\n\nCRITICAL INSTRUCTION: When you have completely finished writing your response and there is nothing left to output, you MUST append the exact text '% [SYSTEM] Refinement complete' on a new line at the very end of your response. This is mandatory for the system to know you are done." });

    var history = new List<Content> { new Content { Role = "user", Parts = finalPromptParts } };

    string fullResponseText = "";
    int currentRequest = 1;
    int maxRequests = 5;

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (sender, e) => { e.Cancel = true; try { cts.Cancel(); } catch { } };
    Console.CancelKeyPress += cancelHandler;

    while (true) {
      Console.WriteLine($"\n  [API] Sende Anfrage an Gemini ({backendParams.Model}) (Request {currentRequest}/{maxRequests})...");
      string chunkResp = "";
      bool callSuccess = false;

      try {
        callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
          streamFactory: () => _client.Models.GenerateContentStreamAsync(backendParams.Model, history, requestConfig),
          onChunkReceived: async (chunk) => {
            string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
            
            if (string.IsNullOrEmpty(text) && chunk.Candidates != null && chunk.Candidates.Count > 0) {
              Console.WriteLine($"\n[DEBUG] Empty text in chunk. FinishReason: {chunk.Candidates[0].FinishReason}");
            }
            
            Console.Write(text);
            chunkResp += text;
            await Task.CompletedTask;
          },
          cancellationToken: cts.Token,
          retryContext: outputFileName
        );
      }
      catch (Exception ex) {
        Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
        Console.WriteLine($"Finaler Fehler: {ex.Message}");
        break;
      }

      if (!callSuccess) {
        Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
        break;
      }

      fullResponseText += chunkResp;

      // Check for completion using the explicit marker we requested
      bool isComplete = chunkResp.Contains("% [SYSTEM] Refinement complete", StringComparison.OrdinalIgnoreCase);
      
      if (isComplete) {
          break;
      }

      if (currentRequest >= maxRequests) {
        Console.WriteLine($"\n\n[WARNUNG] Maximale Anzahl an Requests ({maxRequests}) für dieses Refinement erreicht. Breche ab.");
        break;
      }

      string continuePrompt = $"[IMPORTANT] Your response was cut short due to token limits. Your last output ended with:\n\n" +
          $"```latex\n{(chunkResp.Length > 300 ? "...\n" + chunkResp.Substring(chunkResp.Length - 300) : chunkResp)}\n```\n\n" +
          "Please \"continue\" exactly where you left off. Do not repeat what you already wrote.";

      Console.WriteLine("\n  [Refinement] Unerwartetes Ende der Antwort (Max Tokens?). Bereite automatisierten 'Continue'-Prompt vor...");
      Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

      history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = chunkResp } } });
      history.Add(new Content { Role = "user", Parts = new List<Part> { new Part { Text = continuePrompt } } });

      Console.WriteLine($"\n  [Timer] Warte 20 Sekunden vor der Fortsetzung, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
      if (!await ExtractionHelpers.SmartDelayAsync(20, "Warte auf Rate-Limits (Token Refill)...")) {
          Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
          break;
      }

      currentRequest++;
    }

    Console.CancelKeyPress -= cancelHandler;

    if (!string.IsNullOrEmpty(fullResponseText)) {
      if (!Directory.Exists(targetOutputFolder)) Directory.CreateDirectory(targetOutputFolder);
      string outPath = Path.Combine(targetOutputFolder, outputFileName);
      string cleanedText = ExtractionHelpers.CleanLatexResponse(fullResponseText);
      
      string fileHeader = $"% ==========================================\n" +
                          $"% LatexRefinement Step Output: {outputFileName}\n" +
                          $"% Model: {backendParams.Model}\n" +
                          $"% Temperature: {backendParams.Temperature}\n" +
                          $"% TopP: {backendParams.TopP}\n" +
                          $"% TopK: {backendParams.TopK}\n" +
                          $"% MaxOutputTokens: {backendParams.MaxOutputTokens}\n" +
                          (backendParams.ThinkingBudget.HasValue ? $"% ThinkingBudget: {backendParams.ThinkingBudget.Value}\n" : "") +
                          (!string.IsNullOrEmpty(backendParams.ThinkingLevel) ? $"% ThinkingLevel: {backendParams.ThinkingLevel}\n" : "") +
                          $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"% ==========================================\n\n";

      await System.IO.File.WriteAllTextAsync(outPath, fileHeader + cleanedText);
      Console.WriteLine($"\n\n[Erfolg] Ergebnis gespeichert unter: {outPath}");
      return outPath;
    }
    else {
      Console.WriteLine($"\n[Fehler] Beim Refinement ist ein Fehler aufgetreten oder der Vorgang wurde abgebrochen.");
      return null;
    }
  }

  private async Task CleanupBucketAsync() {
    if (string.IsNullOrWhiteSpace(_config.VertexGcsBucketName)) return;
    try {
      var storageClient = await StorageClient.CreateAsync();
      var objects = storageClient.ListObjectsAsync(_config.VertexGcsBucketName);
      int count = 0;
      await foreach (var obj in objects) {
        await storageClient.DeleteObjectAsync(_config.VertexGcsBucketName, obj.Name);
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
