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

namespace DirectChatAiInteraction;

public class LatexRefinementSession {
  private readonly Client _client;
  private readonly LatexRefinementSessionConfig _config;
  private readonly string? _singleFilePathToProcess;
  private readonly string[]? _multipleFilesToProcess;
  private readonly AiStudioAutoExtractionConfig? _extractionConfig;
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

  public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string singleFilePathToProcess, AiStudioAutoExtractionConfig extractionConfig, string audioFilePath) {
    _client = client;
    _config = config;
    _singleFilePathToProcess = singleFilePathToProcess;
    _multipleFilesToProcess = null;
    _extractionConfig = extractionConfig;
    _audioFilePath = audioFilePath;
  }

  public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string[] multipleFilesToProcess, AiStudioAutoExtractionConfig extractionConfig, string audioFilePath) {
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
        Console.WriteLine("  Grund: Die Voraussetzungen in AiStudioAutoExtractionConfig sind nicht erfüllt.");
        return;
      }
      
      if (_singleFilePathToProcess != null && !System.IO.File.Exists(_singleFilePathToProcess)) {
        Console.WriteLine($"\n[WARNUNG] LaTeX Refinement übersprungen. Die Zieldatei fehlt: {_singleFilePathToProcess}");
        return;
      }

      if (_audioFilePath == null || !System.IO.File.Exists(_audioFilePath)) {
        Console.WriteLine($"\n[WARNUNG] LaTeX Refinement übersprungen. Die Audio-Datei fehlt: {_audioFilePath ?? "null"}");
        return;
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
      
      var handler = new AttachmentHandler(_client, targetFolder, new[] { targetFolder }, true, "");
      var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
      if (success) parts.AddRange(attached);
    }

    string promptText = $"Here is the generated audio file alongside with the combined file with all the offset parts together. " +
                        $"The .tex file was generated with {partsCount} parts by some lecture videos provided with {overlapMin} minutes overlap. " +
                        $"The actual audio length is exactly {audioLengthStr}. The `spoken-clean` blocks timestamps need to perfectly align with this full duration. " +
                        $"Please note that sometimes the timestamps in the `spoken-clean` blocks are horribly misaligned, so each block must be carefully checked and corrected to match the audio.";
    
    parts.Add(new Part { Text = promptText });

    foreach (var file in inputFiles) {
       string content = await System.IO.File.ReadAllTextAsync(file);
       parts.Add(new Part { Text = $"=== FILE: {Path.GetFileName(file)} ===\n{content}\n=== END FILE ===" });
    }

    string outputFileName = $"{baseName}-offset-merged.tex";
    return await ExecuteGenerativeStepAsync(_config.Step1MergeAndTimestamp, parts, targetFolder, outputFileName);
  }

  // Overload that takes single string
  private async Task<string?> ExecuteStep1MergeAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
     return await ExecuteStep1MergeAsync(new[] { inputFile }, audioFilePath, baseName, targetFolder);
  }

  private async Task<string?> ExecuteStep2SpeechRefinementAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
    var parts = new List<Part>();
    
    if (audioFilePath != null && System.IO.File.Exists(audioFilePath)) {
      var handler = new AttachmentHandler(_client, targetFolder, new[] { targetFolder }, true, "");
      var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
      if (success) parts.AddRange(attached);
    }

    parts.Add(new Part { Text = "Please refine the text strictly in between the `spoken-clean` environments according to the system instructions. Do not alter the math or the timestamps." });
    
    string content = await System.IO.File.ReadAllTextAsync(inputFile);
    parts.Add(new Part { Text = $"=== INPUT TEX ===\n{content}\n=== END INPUT TEX ===" });

    string outputFileName = $"{baseName}-offset-speech_refined.tex";
    return await ExecuteGenerativeStepAsync(_config.Step2SpeechRefinement, parts, targetFolder, outputFileName);
  }

  private async Task<string?> ExecuteStep3LastRefinementAsync(string inputFile, string baseName, string targetFolder) {
    var parts = new List<Part>();
    parts.Add(new Part { Text = "Perform the final refinement and formatting pass on this document according to the system instructions." });
    
    string content = await System.IO.File.ReadAllTextAsync(inputFile);
    parts.Add(new Part { Text = $"=== INPUT TEX ===\n{content}\n=== END INPUT TEX ===" });

    string outputFileName = $"{baseName}-offset-final.tex";
    return await ExecuteGenerativeStepAsync(_config.Step3LastRefinement, parts, targetFolder, outputFileName);
  }

  private async Task<string?> ExecuteGenerativeStepAsync(RefinementStepConfig stepConfig, List<Part> userPromptParts, string targetOutputFolder, string outputFileName) {
    string systemInstructionText = "";
    if (stepConfig.SystemInstructionPaths != null && stepConfig.SystemInstructionPaths.Any()) {
        var resolved = ExtractionHelpers.ResolveHistoryFiles(stepConfig.SystemInstructionPaths);
        foreach (var path in resolved) {
             systemInstructionText += await System.IO.File.ReadAllTextAsync(path) + "\n\n";
        }
    }

    var requestConfig = new GenerateContentConfig {
      Temperature = stepConfig.Temperature,
      TopP = stepConfig.TopP,
      TopK = stepConfig.TopK,
      MaxOutputTokens = stepConfig.MaxOutputTokens
    };
    if (!string.IsNullOrWhiteSpace(systemInstructionText)) {
      requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstructionText } } };
    }

    var history = new List<Content> { new Content { Role = "user", Parts = userPromptParts } };

    Console.WriteLine($"\nSende Anfrage an Gemini ({stepConfig.Model})...");

    string? fullText = await ApiResilience.ExecuteWithRetryAsync(
        apiCall: async () => {
          string responseText = "";
          var responseStream = _client.Models.GenerateContentStreamAsync(stepConfig.Model, history, requestConfig);
          await foreach (var chunk in responseStream) {
            string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
            Console.Write(text);
            responseText += text;
          }
          return responseText;
        },
        maxRetries: 5,
        retryContext: outputFileName
    );

    if (!string.IsNullOrEmpty(fullText)) {
      if (!Directory.Exists(targetOutputFolder)) Directory.CreateDirectory(targetOutputFolder);
      string outPath = Path.Combine(targetOutputFolder, outputFileName);
      await System.IO.File.WriteAllTextAsync(outPath, fullText);
      Console.WriteLine($"\n\n[Erfolg] Ergebnis gespeichert unter: {outPath}");
      return outPath;
    }
    else {
      Console.WriteLine($"\n[Fehler] Beim Refinement ist ein Fehler aufgetreten oder der Vorgang wurde abgebrochen.");
      return null;
    }
  }
}
