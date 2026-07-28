using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] Post-processing pipeline that takes sequentially extracted LaTeX chunks and deterministically merges them into a single, cohesive document.
/// [Human] Der letzte Schritt in der Pipeline. Fügt die überlappenden LaTeX-Fragmente nahtlos zu einem kompilierbaren PDF zusammen.
/// </summary>
public partial class LatexRefinementSession {
    private readonly Client _client;
    private readonly LatexRefinementSessionConfig _config;
    private readonly string? _singleFilePathToProcess;
    private readonly string[]? _multipleFilesToProcess;
    private readonly IAutoExtractionConfig? _extractionConfig;
    private readonly string? _audioFilePath;
    private List<Part>? _preUploadedAudioAttachments;

    public LatexRefinementSession(Client client, LatexRefinementSessionConfig config) {
        _client = client;
        _config = config;
        _singleFilePathToProcess = null;
        _multipleFilesToProcess = null;
        _extractionConfig = null;
        _audioFilePath = null;
        _preUploadedAudioAttachments = null;
    }

    public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string singleFilePathToProcess) {
        _client = client;
        _config = config;
        _singleFilePathToProcess = singleFilePathToProcess;
        _multipleFilesToProcess = null;
        _extractionConfig = null;
        _audioFilePath = null;
        _preUploadedAudioAttachments = null;
    }

    public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string singleFilePathToProcess, IAutoExtractionConfig extractionConfig, string? audioFilePath = null, List<Part>? preUploadedAudioAttachments = null) {
        _client = client;
        _config = config;
        _singleFilePathToProcess = singleFilePathToProcess;
        _multipleFilesToProcess = null;
        _extractionConfig = extractionConfig;
        _audioFilePath = audioFilePath;
        _preUploadedAudioAttachments = preUploadedAudioAttachments;
    }

    public LatexRefinementSession(Client client, LatexRefinementSessionConfig config, string[] multipleFilesToProcess, IAutoExtractionConfig extractionConfig, string? audioFilePath = null, List<Part>? preUploadedAudioAttachments = null) {
        _client = client;
        _config = config;
        _singleFilePathToProcess = null;
        _multipleFilesToProcess = multipleFilesToProcess;
        _extractionConfig = extractionConfig;
        _audioFilePath = audioFilePath;
        _preUploadedAudioAttachments = preUploadedAudioAttachments;
    }

    /// <summary>
    /// [AI Context] Entry point for the refinement pipeline. Validates dependencies and starts the execution if prerequisites are met.
    /// [Human] Startet die Refinement-Pipeline, prüft aber vorher, ob die Ziel-Ordner und Audio-Dateien überhaupt vorhanden sind.
    /// </summary>
    public async Task StartAsync() {
        if (!_config.Enabled) {
            Ui.Info("LaTeX Refinement ist in der Konfiguration deaktiviert. Überspringe die Ausführung.", "LaTeX Refinement");
            return;
        }

        if ((_singleFilePathToProcess != null || _multipleFilesToProcess != null) && _extractionConfig != null) {
            if (!_extractionConfig.GoIntoLatexRefinement || !_extractionConfig.GenerateOffsetFiles || !_extractionConfig.GenerateAudioFile) {
                Ui.Warn("LaTeX Refinement übersprungen.", "LaTeX Refinement");
                Ui.Detail("Grund: Die Voraussetzungen in AutoExtractionConfig sind nicht erfüllt.");
                return;
            }

            if (_singleFilePathToProcess != null && !System.IO.File.Exists(_singleFilePathToProcess)) {
                Ui.Warn($"LaTeX Refinement übersprungen. Die Zieldatei fehlt: {_singleFilePathToProcess}", "LaTeX Refinement");
                return;
            }

            if (_audioFilePath == null || !System.IO.File.Exists(_audioFilePath)) {
                Ui.Info($"Ausführung erfolgt ohne Audio-Datei (Pfad: {_audioFilePath ?? "null"}).", "LaTeX Refinement");
            }
        }

        Ui.Step("Starte LaTeX Refinement Pipeline");

        // [AI Context] Reset HasJustUploaded when starting the pipeline so that any background audio upload
        // or prior extraction steps don't suppress the initial 130-second token refill timer.
        AttachmentUploader.HasJustUploaded = false;

        await ExecutePipelineAsync();
    }

    /// <summary>
    /// [AI Context] Orchestrates the 4-step pipeline: Merge, Speech Refinement, Final Format, and PDF Compilation.
    /// [Human] Steuert die einzelnen Schritte (Zusammenfügen, Sprach-Korrektur, Finale Formatierung und PDF-Erstellung).
    /// </summary>
    private async Task ExecutePipelineAsync() {
        string[] currentFiles;
        string targetFolder;
        string baseName;

        if (_multipleFilesToProcess != null && _multipleFilesToProcess.Length > 0) {
            currentFiles = _multipleFilesToProcess;
            targetFolder = Path.GetDirectoryName(currentFiles[0]) ?? _config.TargetFolder;
            baseName = GetCleanBaseName(currentFiles[0]);
        }
        else if (_singleFilePathToProcess != null) {
            currentFiles = [_singleFilePathToProcess];
            targetFolder = Path.GetDirectoryName(_singleFilePathToProcess) ?? _config.TargetFolder;
            baseName = GetCleanBaseName(_singleFilePathToProcess);
        }
        else {
            string sourceFolder = _config.SourceFolder;
            if (!Directory.Exists(sourceFolder)) {
                Ui.Error("Ordner nicht gefunden. Bitte prüfe den SourceFolder in der Konfiguration.", "LaTeX Refinement");
                return;
            }
            currentFiles = Directory.GetFiles(sourceFolder, "*.tex");
            if (currentFiles.Length == 0) return;
            targetFolder = string.IsNullOrWhiteSpace(_config.TargetFolder) ? sourceFolder : _config.TargetFolder;
            baseName = "refined_output";
        }

        // Step 1: Merge and Timestamp Control
        int partsCount = _extractionConfig?.NumberOfParts ?? currentFiles.Length;
        if (_config.Step1MergeAndTimestamp.Enabled) {
            Ui.Step("LaTeX Refinement - Schritt 1: Merge & Zeitstempel-Abgleich");
            if (partsCount <= 1) {
                Ui.Info($"NumberOfParts = {partsCount} (<= 1). Ein Merger ist nicht erforderlich. Überspringe Schritt 1.");
            }
            else {
                string? step1Output = await MergeSegmentsAndAlignTimestampsAsync(currentFiles, _audioFilePath, baseName, targetFolder);
                if (step1Output == null) {
                    Ui.Error("Schritt 1 (Merge) fehlgeschlagen. Breche Pipeline ab.", "LaTeX Refinement");
                    return;
                }
                currentFiles = [step1Output];
            }
        }

        // Step 2: Speech Refinement
        if (_config.Step2SpeechRefinement.Enabled) {
            Ui.Step("LaTeX Refinement - Schritt 2: Textkorrektur & Grammatik-Polishing");
            string? step2Output = await RefineAgainstSpeechAsync(currentFiles[0], _audioFilePath, baseName, targetFolder);
            if (step2Output == null) {
                Ui.Error("Schritt 2 (Speech Refinement) fehlgeschlagen. Breche Pipeline ab.", "LaTeX Refinement");
                return;
            }
            currentFiles = [step2Output];
        }

        // Step 3: Last Refinement
        if (_config.Step3LastRefinement.Enabled) {
            Ui.Step("LaTeX Refinement - Schritt 3: Endprüfung & Validierung");
            Ui.Info("Führe Probe-Kompilierung des aktuellen Dokuments aus...");
            bool alreadyCompiles = await CompilePdfAsync(currentFiles[0], baseName, targetFolder, "step3-precheck", allowRetryOnFailure: false);

            string compileLogPath = Path.Combine(targetFolder, "step3-precheck-compile-log.txt");
            string compileLog = System.IO.File.Exists(compileLogPath) ? await System.IO.File.ReadAllTextAsync(compileLogPath) : "";

            if (alreadyCompiles) {
                Ui.Info("Probe-Kompilierung erfolgreich! Keine Syntaxfehler vorhanden. Gebe diese Info an Schritt 3 weiter.");
            }
            else {
                Ui.Info("Probe-Kompilierung meldet Syntaxfehler. Gebe das Fehlerprotokoll an Schritt 3 weiter zur Korrektur.");
            }

            // [AI Context] Clean up temporary test-compile files (pdf, aux, log, out, toc, wrapper tex, precheck log)
            // so they do not clutter the output directory before final Step 4 PDF generation.
            CleanupPrecheckFiles(targetFolder, currentFiles[0], "step3-precheck", alreadyCompiles);

            Ui.Info("Starte finalen Durchlauf für Schritt 3 (Last Refinement)...");
            var finalOutput = await ApplyFinalPolishAsync(currentFiles[0], baseName, targetFolder, alreadyCompiles, compileLog);
            if (finalOutput == null) {
                Ui.Error("Schritt 3 (Last Refinement) fehlgeschlagen.", "LaTeX Refinement");
            }
            else {
                currentFiles = [finalOutput];
            }
        }

        // Step 4: PDF Compilation
        if (_config.PdfCompilation?.Enabled == true || _config.PdfCompilation?.UseAntiGravityAgent == true) {
            Ui.Step("LaTeX Refinement - Schritt 4: PDF Generierung & Validierung");
            await CompilePdfAsync(currentFiles[0], baseName, targetFolder);
        }

        Ui.Success("LaTeX Refinement Pipeline erfolgreich abgeschlossen!", "LaTeX Refinement");
    }
    /// <summary>
    /// [AI Context] Step 1: Merges overlapping LaTeX chunks. If an audio file is provided, its metadata is attached to align timestamps correctly.
    /// [Human] Schritt 1: Führt die einzelnen Video-Teile zusammen. Nutzt (falls vorhanden) die Audio-Spur, um kaputte Zeitstempel zu korrigieren.
    /// </summary>
    private async Task<string?> MergeSegmentsAndAlignTimestampsAsync(string[] inputFiles, string? audioFilePath, string baseName, string targetFolder) {
        if (inputFiles.Length == 0) return null;
        int partsCount = _extractionConfig?.NumberOfParts ?? inputFiles.Length;
        int overlapMin = (_extractionConfig?.OverlapSeconds ?? 180) / 60;

        string audioLengthStr = "unknown";
        string partTimestampsStr = "";

        bool audioExists = audioFilePath != null && System.IO.File.Exists(audioFilePath);

        List<Part> audioParts = [];
        if (audioExists) {
            double dur = await FfmpegToolkit.GetVideoDurationAsync(audioFilePath!);
            TimeSpan t = TimeSpan.FromSeconds(dur);
            audioLengthStr = $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

            // Calculate expected timestamps for each part
            int overlapSec = _extractionConfig?.OverlapSeconds ?? 180;
            double segmentLength = (dur + (partsCount - 1) * overlapSec) / partsCount;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < partsCount; i++) {
                double start = i * (segmentLength - overlapSec);
                double end = start + segmentLength;
                if (end > dur) end = dur;

                TimeSpan tStart = TimeSpan.FromSeconds(start);
                TimeSpan tEnd = TimeSpan.FromSeconds(end);
                sb.AppendLine($"- Part {i + 1}: {tStart.Hours:D2}:{tStart.Minutes:D2}:{tStart.Seconds:D2} - {tEnd.Hours:D2}:{tEnd.Minutes:D2}:{tEnd.Seconds:D2}");
            }
            partTimestampsStr = sb.ToString();

            if (_config.Step1MergeAndTimestamp.AttachAudio) {
                if (_preUploadedAudioAttachments != null && _preUploadedAudioAttachments.Count > 0) {
                    Ui.Info("Verwende parallel im Hintergrund hochgeladene Audio-Datei.", "Step 1");
                    audioParts.AddRange(_preUploadedAudioAttachments);
                    AttachmentUploader.HasJustUploaded = false;
                }
                else {
                    var handler = new AttachmentUploader(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
                    var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
                    if (success) {
                        audioParts.AddRange(attached);
                        Ui.Info($"Audio-Datei erfolgreich verarbeitet: {audioFilePath}", "Step 1");
                        _preUploadedAudioAttachments = attached;
                    }
                }
            }
        }

        bool audioAttached = audioExists && _config.Step1MergeAndTimestamp.AttachAudio;
        string outputFileName = $"step2-{baseName}-offset-merged.tex";
        string? result;

        if (audioAttached) {
            var round1Parts = new List<Part>();
            string round1Prompt = $"Here is the combined .tex file to process. It was generated with {partsCount} parts by some lecture videos provided with {overlapMin} minutes overlap. " +
                                  (string.IsNullOrEmpty(partTimestampsStr) ? "" : $"\nExpected total duration timestamps for each part:\n{partTimestampsStr}\n(Note: These timestamps represent the total chronological span of each video part, NOT the span of a single `spoken-clean` block!)\n\n") +
                                  "Please acknowledge you have read it. I will provide the audio file and final merge instructions in the next round.";
            round1Parts.Add(new Part { Text = round1Prompt });
            foreach (var file in inputFiles) {
                Ui.Info($"Lese Eingabedatei für Merge: {Path.GetFileName(file)}", "Step 1");
                string content = await System.IO.File.ReadAllTextAsync(file);
                round1Parts.Add(new Part { Text = $"<input_file name=\"{Path.GetFileName(file)}\">\n{content}\n</input_file>" });
            }

            List<Content> history = [];
            history.Add(new Content { Role = "user", Parts = round1Parts });
            history.Add(new Content { Role = "model", Parts = [new Part { Text = "Understood. I have read the .tex files and noted the expected timestamps. I am ready for the audio file and the merge instructions." }] });

            var round2Parts = new List<Part>();
            round2Parts.AddRange(audioParts);
            string round2Prompt = $"Here is the generated audio file. The actual audio length is exactly {audioLengthStr} (00:00:00 - {audioLengthStr}).\n\n" +
                                  $"The `spoken-clean` blocks timestamps need to perfectly align with this full duration. Please note that sometimes the timestamps in the `spoken-clean` blocks are horribly misaligned, so each block must be carefully checked and corrected to match the audio. Please perform the merge and timestamp correction according to the system instructions.";
            round2Parts.Add(new Part { Text = round2Prompt });

            history.Add(new Content { Role = "user", Parts = round2Parts });

            Ui.Info("Verwende Multi-Turn-Struktur für Schritt 1 (Simulation von Audio + Textsegmenten).", "Step 1");
            result = await RunRefinementStepAsync(_config.Step1MergeAndTimestamp, history, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep1);
        }
        else {
            var parts = new List<Part>();
            string promptText = "Here is the combined file with all the offset parts together. " +
                                $"The .tex file was generated with {partsCount} parts by some lecture videos provided with {overlapMin} minutes overlap. " +
                                $"The actual audio/lecture length is roughly {audioLengthStr} (00:00:00 - {audioLengthStr}).\n\n" +
                                (string.IsNullOrEmpty(partTimestampsStr) ? "" : $"Expected total duration timestamps for each part:\n{partTimestampsStr}\n(Note: These timestamps represent the total chronological span of each video part, NOT the span of a single `spoken-clean` block!)\n\n") +
                                "Important: Since no audio file is attached, the timestamps in subsequent parts have already been pre-adjusted to global lecture time. Please eliminate redundant overlapping blocks at the part seams and only fix timestamps that look completely out of order or severely broken across boundaries. Otherwise, trust and preserve the existing pre-calibrated timestamps.";
            parts.Add(new Part { Text = promptText });
            foreach (var file in inputFiles) {
                Ui.Info($"Lese Eingabedatei für Merge: {Path.GetFileName(file)}", "Step 1");
                string content = await System.IO.File.ReadAllTextAsync(file);
                parts.Add(new Part { Text = $"<input_file name=\"{Path.GetFileName(file)}\">\n{content}\n</input_file>" });
            }
            AttachmentUploader.HasJustUploaded = false;
            result = await RunRefinementStepAsync(_config.Step1MergeAndTimestamp, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep1);
        }

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    private async Task<string?> MergeSegmentsAndAlignTimestampsAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
        return await MergeSegmentsAndAlignTimestampsAsync([inputFile], audioFilePath, baseName, targetFolder);
    }

    private async Task<string?> RefineAgainstSpeechAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
        bool audioAttached = _config.Step2SpeechRefinement.AttachAudio && audioFilePath != null && System.IO.File.Exists(audioFilePath);
        var audioParts = new List<Part>();

        if (audioAttached) {
            if (_preUploadedAudioAttachments != null && _preUploadedAudioAttachments.Count > 0) {
                Ui.Info("Verwende parallel im Hintergrund hochgeladene Audio-Datei.", "Step 2");
                audioParts.AddRange(_preUploadedAudioAttachments);
                AttachmentUploader.HasJustUploaded = false;
            }
            else {
                var handler = new AttachmentUploader(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
                var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
                if (success) {
                    audioParts.AddRange(attached);
                    Ui.Info($"Audio-Datei erfolgreich verarbeitet: {audioFilePath}", "Step 2");
                    _preUploadedAudioAttachments = attached;
                }
                else {
                    audioAttached = false;
                }
            }
        }

        Ui.Info($"Lese Eingabedatei für Textkorrektur: {Path.GetFileName(inputFile)}", "Step 2");
        string content = await System.IO.File.ReadAllTextAsync(inputFile);
        string outputFileName = $"step3-{baseName}-offset-speech_refined.tex";
        string? result;

        if (audioAttached && audioParts.Count > 0) {
            var round1Parts = new List<Part> {
                new() { Text = "Here is the current merged LaTeX document (.tex file) to process. Please read and internalize the entire document structure, including all math containers, equations, and `spoken-clean` blocks. Please acknowledge that you have read it. I will provide the audio file and speech refinement instructions in the next round." },
                new() { Text = $"<input_tex name=\"{Path.GetFileName(inputFile)}\">\n{content}\n</input_tex>" }
            };

            List<Content> history = [
                new() { Role = "user", Parts = round1Parts },
                new() { Role = "model", Parts = [new Part { Text = "Understood. I have read the complete LaTeX document and internalized all mathematical structures, formulas, timestamps, and `spoken-clean` environments. I will preserve all math, timestamps, and LaTeX structure exactly as they are. I am ready for the audio file to listen to the speech and refine the spoken text inside the `spoken-clean` environments." }] }
            ];

            var round2Parts = new List<Part>();
            round2Parts.AddRange(audioParts);
            round2Parts.Add(new Part { Text = "Here is the lecture audio file. Please listen to the audio carefully and refine the text strictly inside the `spoken-clean` environments to fix any transcription, word choice, or grammatical errors according to the system instructions. Do not alter any mathematical formulas, equations, or timestamps. Output only the refined LaTeX code." });
            history.Add(new Content { Role = "user", Parts = round2Parts });

            Ui.Info("Verwende Multi-Turn-Struktur für Schritt 2 (Simulation von Text-Dokument + Audio-Refinement).", "Step 2");
            AttachmentUploader.HasJustUploaded = false;
            result = await RunRefinementStepAsync(_config.Step2SpeechRefinement, history, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2);
        }
        else {
            var parts = new List<Part> {
                new() { Text = "Please refine the text strictly in between the `spoken-clean` environments according to the system instructions. Do not alter the math or the timestamps." },
                new() { Text = $"<input_tex>\n{content}\n</input_tex>" }
            };
            AttachmentUploader.HasJustUploaded = false;
            result = await RunRefinementStepAsync(_config.Step2SpeechRefinement, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2);
        }

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    private async Task<string?> ApplyFinalPolishAsync(string inputFile, string baseName, string targetFolder, bool alreadyCompiles, string compilerFeedbackLog) {
        List<Part> parts = [new() { Text = "Perform the final refinement and formatting pass on this document according to the system instructions." }];
        if (alreadyCompiles) {
            parts.Add(new Part { Text = "<compiler_status>\nThe input LaTeX document ALREADY COMPILES successfully without any LaTeX errors! Please preserve its valid syntax and structure while performing any final textual/typographical refinements according to the system instructions.\n</compiler_status>" });
        }
        else if (!string.IsNullOrWhiteSpace(compilerFeedbackLog)) {
            parts.Add(new Part { Text = $"<compiler_error_feedback>\nWhen attempting to compile the input LaTeX document with pdflatex, the following errors and log messages were produced:\n\n{compilerFeedbackLog}\n\nPlease analyze and fix these LaTeX syntax/compilation errors during this final refinement pass.\n</compiler_error_feedback>" });
        }

        Ui.Info($"Lese Eingabedatei für Formatierung: {Path.GetFileName(inputFile)}", "Step 3");
        string content = await System.IO.File.ReadAllTextAsync(inputFile);
        parts.Add(new Part { Text = $"<input_tex>\n{content}\n</input_tex>" });

        string outputFileName = $"step4-{baseName}-offset-final.tex";
        AttachmentUploader.HasJustUploaded = false;
        var result = await RunRefinementStepAsync(_config.Step3LastRefinement, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep3);

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    private async Task<string?> RunRefinementStepAsync(RefinementStepConfig stepConfig, List<Part> userPromptParts, string targetOutputFolder, string outputFileName, string cacheStateFileName) {
        var finalPromptParts = new List<Part>(userPromptParts);
        var history = new List<Content> { new() { Role = "user", Parts = finalPromptParts } };
        return await RunRefinementStepAsync(stepConfig, history, targetOutputFolder, outputFileName, cacheStateFileName);
    }

    private async Task<string?> RunRefinementStepAsync(RefinementStepConfig stepConfig, List<Content> history, string targetOutputFolder, string outputFileName, string cacheStateFileName) {
        BackendParameters backendParams = _config.UseVertex ? stepConfig.Vertex : stepConfig.AiStudio;

        string systemInstructionText = await ResolveSystemInstructionTextAsync(stepConfig);
        string? cacheName = await EnsureContextCacheAsync(backendParams, systemInstructionText, outputFileName, cacheStateFileName);
        var requestConfig = BuildStepRequestConfig(backendParams, cacheName, systemInstructionText);

        var lastUserMsg = history.LastOrDefault(c => c.Role == "user");
        if (lastUserMsg != null && lastUserMsg.Parts != null) {
            lastUserMsg.Parts.Add(new Part { Text = "\n\nCRITICAL INSTRUCTION: When you have completely finished writing your response and there is nothing left to output, you MUST append the exact text '% [SYSTEM] Refinement complete' on a new line at the very end of your response. This is mandatory for the system to know you are done." });
        }

        await DumpPromptLogAsync(history, systemInstructionText, targetOutputFolder, outputFileName);
        var (expectedSpokenClean, expectedMathStroke) = ComputeExpectedStructuralCounts(history);

        var (fullResponseText, totalInputTokens, totalOutputTokens, totalCachedTokens) =
            await StreamAndCollectAsync(stepConfig, backendParams, history, requestConfig, outputFileName);

        if (!string.IsNullOrEmpty(fullResponseText) && (expectedSpokenClean > 0 || expectedMathStroke > 0)) {
            int actualSpokenClean = SpokenCleanRegex().Count(fullResponseText);
            int actualMathStroke = MathStrokeRegex().Count(fullResponseText);

            int minExpectedSpoken = (int)(expectedSpokenClean * 0.6);
            int minExpectedMath = (int)(expectedMathStroke * 0.6);

            if (actualSpokenClean < minExpectedSpoken || actualMathStroke < minExpectedMath) {
                Ui.Error($"SILENT TRUNCATION DETECTED! Erwartet: ~{expectedSpokenClean} spoken-clean / ~{expectedMathStroke} math-stroke, Erhalten: {actualSpokenClean} spoken-clean / {actualMathStroke} math-stroke.", "Refinement");
                return null;
            }
            else {
                Ui.Detail($"Structural Integrity Verified: {actualSpokenClean}/{expectedSpokenClean} spoken-clean, {actualMathStroke}/{expectedMathStroke} math-stroke.", "Refinement");
            }
        }

        if (!string.IsNullOrEmpty(fullResponseText)) {
            if (!Directory.Exists(targetOutputFolder)) Directory.CreateDirectory(targetOutputFolder);
            string outPath = Path.Combine(targetOutputFolder, outputFileName);

            if (System.IO.File.Exists(outPath)) {
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(outputFileName);
                string ext = Path.GetExtension(outputFileName);
                int copyIndex = 1;
                while (System.IO.File.Exists(outPath)) {
                    outPath = Path.Combine(targetOutputFolder, $"{fileNameWithoutExt}-copy{copyIndex}{ext}");
                    copyIndex++;
                }
                outputFileName = Path.GetFileName(outPath);
            }

            string cleanedText = LatexResponseCleaner.CleanLatexResponse(fullResponseText);

            string fileHeader = $"% ==========================================\n" +
                                $"% LatexRefinement Step Output: {outputFileName}\n" +
                                $"% Model: {backendParams.CurrentModel}\n" +
                                $"% Temperature: {backendParams.Temperature}\n" +
                                $"% TopP: {backendParams.TopP}\n" +
                                $"% TopK: {backendParams.TopK}\n" +
                                $"% MaxOutputTokens: {backendParams.MaxOutputTokens}\n" +
                                (backendParams.ThinkingBudget.HasValue ? $"% ThinkingBudget: {backendParams.ThinkingBudget.Value}\n" : "") +
                                (!string.IsNullOrEmpty(backendParams.ThinkingLevel) ? $"% ThinkingLevel: {backendParams.ThinkingLevel}\n" : "") +
                                $"% Prompt Tokens: {totalInputTokens:N0}\n" +
                                $"% Candidates Tokens: {totalOutputTokens:N0} (inkl. Thinking Tokens)\n" +
                                $"% Cached Tokens: {totalCachedTokens:N0}\n" +
                                $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                $"% ==========================================\n\n";

            await System.IO.File.WriteAllTextAsync(outPath, fileHeader + cleanedText);
            Ui.Success($"Ergebnis gespeichert unter: {outPath}", "Refinement");

            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            return outPath;
        }
        else {
            Ui.Error("Beim Refinement ist ein Fehler aufgetreten oder der Vorgang wurde abgebrochen.", "Refinement");
            return null;
        }
    }

    private async Task<string> ResolveSystemInstructionTextAsync(RefinementStepConfig stepConfig) {
        string systemInstructionText = "";
        if (stepConfig.SystemInstructionPaths != null && stepConfig.SystemInstructionPaths.Length > 0) {
            Ui.Info("Folgende System-Instruktionen sind konfiguriert:", "LaTeX Refinement");
            var resolved = HistoryFileResolver.ResolveHistoryFiles(stepConfig.SystemInstructionPaths);
            FileTreeRenderer.PrintFileTree(resolved);
            foreach (var path in resolved) {
                if (System.IO.File.Exists(path)) {
                    Ui.Info($"Lade System-Instruktion: {path}");
                    string relPath = Path.GetFileName(path);
                    systemInstructionText += $"******\n------\n******\nHere is the file `{relPath}`:\n\n" + await System.IO.File.ReadAllTextAsync(path) + "\n\n";
                }
                else {
                    Ui.Warn($"System-Instruktion nicht gefunden und übersprungen: {path}");
                }
            }
            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;
        }
        return systemInstructionText;
    }

    private async Task<string?> EnsureContextCacheAsync(BackendParameters backendParams, string systemInstructionText, string outputFileName, string cacheStateFileName) {
        string? cacheName = null;
        if (_config.UseVertex && backendParams.UseContextCaching && !string.IsNullOrWhiteSpace(systemInstructionText)) {
            string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
            var savedState = ContextCacheStateManager.LoadState(cacheStateFileName);
            bool match = ContextCacheStateManager.MatchesConfig(
                savedState,
                backendParams.CurrentModel,
                backendParams.Temperature,
                backendParams.TopP,
                backendParams.TopK,
                backendParams.MaxOutputTokens,
                backendParams.ThinkingBudget,
                backendParams.ThinkingLevel,
                checksum
            );
            if (match && await ContextCacheStateManager.IsValidRemoteAsync(_client, savedState.CacheName!)) {
                cacheName = savedState.CacheName;
                Ui.Info($"Bestehender Google Kontext-Cache geladen: {cacheName}", "Cache");
            }
            else {
                if (!string.IsNullOrEmpty(savedState.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
                }
                Ui.Info("Erstelle neuen Google Kontext-Cache...", "Cache");
                cacheName = await CreateContextCacheAsync(backendParams, systemInstructionText, outputFileName, checksum, cacheStateFileName, isRecreate: false);
            }
        }
        else if (_config.UseVertex && !backendParams.UseContextCaching) {
            var sState = ContextCacheStateManager.LoadState(cacheStateFileName);
            if (!string.IsNullOrEmpty(sState.CacheName)) {
                await ContextCacheStateManager.DeleteRemoteAsync(_client, sState.CacheName);
                ContextCacheStateManager.ClearState(cacheStateFileName);
            }
        }

        if (!string.IsNullOrEmpty(cacheName) && backendParams.UseContextCaching && !string.IsNullOrWhiteSpace(systemInstructionText)) {
            var cacheState = ContextCacheStateManager.LoadState(cacheStateFileName);
            double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
            bool cacheValid = false;

            if (remainingMin > 0) {
                if (remainingMin < backendParams.ContextCachingMinimumRemainingMinutes) {
                    Ui.Info($"TTL knapp ({remainingMin:F1} min). Verlängere automatisch um {backendParams.ContextCachingIncrementMinutes} min...", "Cache");
                    var updatedState = await ContextCacheStateManager.ExtendCacheAsync(_client, cacheState, backendParams.ContextCachingIncrementMinutes, cacheStateFileName);
                    if (updatedState != null) {
                        Ui.Info($"Cache erfolgreich verlängert bis: {updatedState.ExpireTimeUtc.ToLocalTime():t}", "Cache");
                        cacheValid = true;
                    }
                }
                else {
                    cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(_client, cacheName);
                }
            }

            if (!cacheValid) {
                Ui.Info("Cache abgelaufen oder ungültig. Erstelle neuen Google Kontext-Cache...", "Cache");
                ContextCacheStateManager.ClearState(cacheStateFileName);
                string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
                cacheName = await CreateContextCacheAsync(backendParams, systemInstructionText, outputFileName, checksum, cacheStateFileName, isRecreate: true);
            }
        }

        return cacheName;
    }

    /// <summary>
    /// [AI Context] Assembles the GenerateContentConfig for a refinement step: cached content or plain
    /// system instruction (mutually exclusive), plus thinking config.
    /// [Human] Baut die Anfrage-Konfiguration für einen Refinement-Schritt.
    /// </summary>
    private static GenerateContentConfig BuildStepRequestConfig(BackendParameters backendParams, string? cacheName, string systemInstructionText) {
        var requestConfig = new GenerateContentConfig {
            Temperature = backendParams.Temperature,
            TopP = backendParams.TopP,
            TopK = backendParams.TopK,
            MaxOutputTokens = backendParams.MaxOutputTokens
        };
        if (!string.IsNullOrEmpty(cacheName)) {
            requestConfig.CachedContent = cacheName;
        }
        else if (!string.IsNullOrWhiteSpace(systemInstructionText)) {
            // Simplified using target‑typed new for both the Content and Part objects.
            requestConfig.SystemInstruction = new() { Role = "system", Parts = [new() { Text = systemInstructionText }] };
        }

        if (ModelCapabilities.SupportsThinking(backendParams.CurrentModel)) {
            bool isGemini25 = backendParams.CurrentModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(backendParams.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = backendParams.ThinkingLevel };
            }
            else if (backendParams.ThinkingBudget.HasValue) {
                int budget = backendParams.ThinkingBudget.Value;
                if (budget > 32768) budget = 32768;
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = budget };
            }
        }

        return requestConfig;
    }

    /// <summary>
    /// [AI Context] Dumps the full conversation history Gemini will read into a log file, for debugging.
    /// Failure here is non-fatal -- only the log write is skipped.
    /// [Human] Speichert den vollständigen Gemini-Prompt-Verlauf als Log-Datei (nur Diagnose).
    /// </summary>
    private static async Task DumpPromptLogAsync(List<Content> history, string systemInstructionText, string targetOutputFolder, string outputFileName) {
        try {
            var sbPrompt = new System.Text.StringBuilder();
            sbPrompt.AppendLine("# SYSTEM INSTRUCTION");
            sbPrompt.AppendLine(systemInstructionText);
            sbPrompt.AppendLine("\n---\n");

            foreach (var turn in history) {
                sbPrompt.AppendLine($"# ROLE: {turn.Role}");
                if (turn.Parts != null) {
                    foreach (var part in turn.Parts) {
                        sbPrompt.AppendLine(part.Text ?? "[Media Attachment]");
                    }
                }
                sbPrompt.AppendLine("\n---\n");
            }

            string promptDumpPath = Path.Combine(targetOutputFolder, $"{outputFileName}-prompt-log.md");
            await System.IO.File.WriteAllTextAsync(promptDumpPath, sbPrompt.ToString());
            Ui.Info($"Gemini-Prompt-Log gespeichert unter: {promptDumpPath}");
        }
        catch (Exception ex) {
            Ui.Warn($"Konnte Prompt-Log nicht speichern: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private (int ExpectedSpokenClean, int ExpectedMathStroke) ComputeExpectedStructuralCounts(List<Content> history) {
        int expectedSpokenClean = 0;
        int expectedMathStroke = 0;
        try {
            string allInputText = "";
            foreach (var turn in history) {
                if (turn.Parts != null) {
                    foreach (var part in turn.Parts) {
                        if (!string.IsNullOrEmpty(part.Text)) {
                            allInputText += part.Text + "\n";
                        }
                    }
                }
            }
            expectedSpokenClean = SpokenCleanRegex().Count(allInputText);
            expectedMathStroke = MathStrokeRegex().Count(allInputText);
            if (expectedSpokenClean > 0 || expectedMathStroke > 0) {
                Ui.Info($"Structural Integrity Tracker: Erwarte ca. {expectedSpokenClean}x spoken-clean und {expectedMathStroke}x math-stroke Blöcke im Output.");
            }
        }
        catch { }
        return (expectedSpokenClean, expectedMathStroke);
    }

    private async Task<(string FullResponseText, int TotalInputTokens, int TotalOutputTokens, int TotalCachedTokens)> StreamAndCollectAsync(
        RefinementStepConfig stepConfig, BackendParameters backendParams, List<Content> history, GenerateContentConfig requestConfig, string outputFileName) {
        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int totalCachedTokens = 0;

        string fullResponseText = "";
        int currentRequest = 1;
        int maxRequests = 5;
        int emptyResponseRetries = 0;

        using var cts = new CancellationTokenSource();
        void CancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch (Exception ex) { Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}"); } }
        Console.CancelKeyPress += CancelHandler;

        while (true) {
            string providerName = _config.UseVertex ? "Vertex AI" : "Google AI Studio";

            int rateLimitDelay = stepConfig.RateLimitDelaySeconds > 0 ? stepConfig.RateLimitDelaySeconds : 130;
            double secondsSinceLastGen = (DateTime.UtcNow - InteractiveDelay.LastGenerationCompletionTimeUtc).TotalSeconds;
            if (secondsSinceLastGen < rateLimitDelay && !InteractiveDelay.IsInSmartDelay) {
                int waitRemaining = (int)Math.Ceiling(rateLimitDelay - secondsSinceLastGen);
                Ui.Detail($"Warte verbleibende {waitRemaining} Sekunden vor dem nächsten API-Aufruf...", "Rate-Limit & Quota");
                if (!await InteractiveDelay.SmartDelayAsync(waitRemaining, "Warte auf Rate-Limits (Token-Refill Schutz vor API-Aufruf)...")) {
                    break;
                }
            }
            AttachmentUploader.HasJustUploaded = false;

            Ui.Info($"Sende Anfrage an {providerName} ({backendParams.CurrentModel}) (Request {currentRequest}/{maxRequests})...", "API");

            string chunkResp = "";
            bool callSuccess = false;

            try {
                callSuccess = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                  streamFactory: () => _client.Models.GenerateContentStreamAsync(backendParams.CurrentModel, history, requestConfig),
                  onChunkReceived: async (chunk) => {
                      string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";

                      if (string.IsNullOrEmpty(text) && chunk.Candidates != null && chunk.Candidates.Count > 0) {
                          Ui.Detail($"Empty text in chunk. FinishReason: {chunk.Candidates[0].FinishReason}", "DEBUG");
                      }

                      Ui.Raw(text);
                      chunkResp += text;

                      if (chunk.UsageMetadata != null) {
                          if (chunk.UsageMetadata.PromptTokenCount.HasValue)
                              totalInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                          if (chunk.UsageMetadata.CandidatesTokenCount.HasValue)
                              totalOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                          if (chunk.UsageMetadata.CachedContentTokenCount.HasValue)
                              totalCachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                      }

                      await Task.CompletedTask;
                  },
                  cancellationToken: cts.Token,
                  retryContext: outputFileName,
                  onRetry: () => {
                      chunkResp = "";
                      totalInputTokens = 0;
                      totalOutputTokens = 0;
                      totalCachedTokens = 0;
                  }
                );
            }
            catch (Exception ex) {
                Ui.Error($"Der Fehler konnte nicht durch einen automatischen Retry behoben werden: {ex.GetType().Name} - {ex.Message}", "Abbruch");
                break;
            }

            if (!callSuccess) {
                Ui.Warn("Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
                break;
            }

            if (string.IsNullOrWhiteSpace(chunkResp)) {
                if (emptyResponseRetries < 3) {
                    emptyResponseRetries++;
                    Ui.Error("Das Modell hat eine komplett leere Antwort zurückgegeben (z.B. wegen MALFORMED_RESPONSE oder Safety-Filtern).");
                    Ui.Detail($"Warte 5 Sekunden vor Versuch {emptyResponseRetries}/3...");
                    await Task.Delay(5000, cts.Token);
                    continue;
                }
                else {
                    Ui.Error("Das Modell hat nach 3 Versuchen weiterhin eine komplett leere Antwort zurückgegeben. Der Vorgang wird abgebrochen.");
                    break;
                }
            }

            emptyResponseRetries = 0;
            fullResponseText += chunkResp;

            bool isComplete = chunkResp.Contains("% [SYSTEM] Refinement complete", StringComparison.OrdinalIgnoreCase);

            if (isComplete) {
                break;
            }

            if (currentRequest >= maxRequests) {
                Ui.Warn($"Maximale Anzahl an Requests ({maxRequests}) für dieses Refinement erreicht. Breche ab.");
                break;
            }

            bool closedBlock = chunkResp.TrimEnd().EndsWith("```");
            string continuePrompt = $"[IMPORTANT] Your response was cut short due to token limits. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Do not open a new ```latex block, do not open a new environment, and do not open new math delimiters if you were already inside one. Just print the very next character.";

            if (closedBlock) {
                continuePrompt += "\n\n[WARNING] It looks like you closed the ```latex markdown block, but you forgot the '% [SYSTEM] Refinement complete' marker. If you have not finished transcribing/refining the ENTIRE document, DO NOT just send the marker! You must continue transcribing the remaining content of the lecture. Open a new ```latex block and continue the transcription.";
            }

            Ui.Info("Unerwartetes Ende der Antwort. Bereite automatisierten 'Continue'-Prompt vor...", "Refinement");
            Ui.Detail($"Sende folgenden Continue-Prompt:\n{continuePrompt}");

            history.Add(new Content { Role = "model", Parts = [new Part { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new Part { Text = continuePrompt }] });

            Ui.Detail($"Warte {rateLimitDelay} Sekunden (Token Refill)...", "Rate-Limit");
            if (!await InteractiveDelay.SmartDelayAsync(rateLimitDelay, "Warte auf Rate-Limits (Token Refill)...")) {
                Ui.Warn("Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= CancelHandler;

        return (fullResponseText, totalInputTokens, totalOutputTokens, totalCachedTokens);
    }

    private async Task<string?> CreateContextCacheAsync(BackendParameters backendParams, string systemInstructionText, string outputFileName, string checksum, string cacheStateFileName, bool isRecreate) {
        try {
            var cacheConfig = new CreateCachedContentConfig {
                SystemInstruction = new() { Role = "system", Parts = [new() { Text = systemInstructionText }] },
                DisplayName = $"latex-ref-{Path.GetFileNameWithoutExtension(outputFileName)}",
                Ttl = $"{backendParams.ContextCachingMinutes * 60}s"
            };
            var created = await _client.Caches.CreateAsync(backendParams.CurrentModel, cacheConfig);
            if (created != null && !string.IsNullOrEmpty(created.Name)) {
                string cacheName = created.Name;
                var newState = new ContextCacheState {
                    CacheName = cacheName,
                    Model = backendParams.CurrentModel,
                    Temperature = backendParams.Temperature,
                    TopP = backendParams.TopP,
                    TopK = backendParams.TopK,
                    MaxOutputTokens = backendParams.MaxOutputTokens,
                    ThinkingBudget = backendParams.ThinkingBudget,
                    ThinkingLevel = backendParams.ThinkingLevel,
                    SystemInstructionChecksum = checksum,
                    ExpireTimeUtc = DateTime.UtcNow.AddMinutes(backendParams.ContextCachingMinutes)
                };
                if (created.ExpireTime.HasValue) {
                    newState.ExpireTimeUtc = created.ExpireTime.Value.ToUniversalTime();
                }
                ContextCacheStateManager.SaveState(newState, cacheStateFileName);
                if (isRecreate) {
                    Ui.Success($"Google Kontext-Cache erfolgreich neu erstellt: {cacheName}");
                }
                else {
                    Ui.Success($"Google Kontext-Cache erfolgreich erstellt: {cacheName}");
                }
                return cacheName;
            }
        }
        catch (Exception ex) {
            Ui.Error($"Kontext-Caching fehlgeschlagen: {ex.GetType().Name} - {ex.Message}");
        }
        return null;
    }

    private Task CleanupBucketAsync() => GcsWorkspace.PurgeAsync(_config.VertexGcsBucketName);

    private static string GetCleanBaseName(string filePath) {
        string name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Length > 6 && name.StartsWith("step", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[4]) && name[5] == '-') {
            name = name[6..];
        }
        string[] suffixes = ["-merged", "-speech_refined", "-final", "-last-fix", "-last-fix-standalone", "-final-attempt", "-final-main", "-last_try-main", "-last_try"];
        foreach (var suffix in suffixes) {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                name = name[..^suffix.Length];
            }
        }
        int partIdx = name.IndexOf("-part", StringComparison.OrdinalIgnoreCase);
        if (partIdx >= 0) {
            name = name[..partIdx];
        }
        if (name.EndsWith("-offset", StringComparison.OrdinalIgnoreCase)) {
            name = name[..^"-offset".Length];
        }
        return name;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{spoken-clean\}")]
    private static partial System.Text.RegularExpressions.Regex SpokenCleanRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{math-stroke\}")]
    private static partial System.Text.RegularExpressions.Regex MathStrokeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{document\}|\\end\{document\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DocumentTagsRegex();
}
