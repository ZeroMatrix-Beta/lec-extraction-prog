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

/// <summary>
/// [AI Context] Post-processing pipeline that takes sequentially extracted LaTeX chunks and deterministically merges them into a single, cohesive document.
/// [Human] Der letzte Schritt in der Pipeline. Fügt die überlappenden LaTeX-Fragmente nahtlos zu einem kompilierbaren PDF zusammen.
/// </summary>
public class LatexRefinementSession {
    private readonly Client _client;
    private readonly LatexRefinementSessionConfig _config;
    private readonly string? _singleFilePathToProcess;
    private readonly string[]? _multipleFilesToProcess;
    private readonly IAutoExtractionConfig? _extractionConfig;
    private readonly string? _audioFilePath;
    private readonly List<Part>? _preUploadedAudioAttachments;

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
            Console.WriteLine("\n[LaTeX Refinement] LaTeX Refinement ist in der Konfiguration deaktiviert. Überspringe die Ausführung.");
            return;
        }

        if ((_singleFilePathToProcess != null || _multipleFilesToProcess != null) && _extractionConfig != null) {
            if (!_extractionConfig.GoIntoLatexRefinement || !_extractionConfig.GenerateOffsetFiles || !_extractionConfig.GenerateAudioFile) {
                Console.WriteLine("\n[LaTeX Refinement] [WARNUNG] LaTeX Refinement übersprungen.");
                Console.WriteLine("  Grund: Die Voraussetzungen in AutoExtractionConfig sind nicht erfüllt.");
                return;
            }

            if (_singleFilePathToProcess != null && !System.IO.File.Exists(_singleFilePathToProcess)) {
                Console.WriteLine($"\n[LaTeX Refinement] [WARNUNG] LaTeX Refinement übersprungen. Die Zieldatei fehlt: {_singleFilePathToProcess}");
                return;
            }

            if (_audioFilePath == null || !System.IO.File.Exists(_audioFilePath)) {
                Console.WriteLine($"\n[LaTeX Refinement] [INFO] Ausführung erfolgt ohne Audio-Datei (Pfad: {_audioFilePath ?? "null"}).");
            }
        }

        Console.WriteLine("\n==================================================");
        Console.WriteLine("   Starte [LaTeX Refinement] Pipeline");
        Console.WriteLine("==================================================");

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
                Console.WriteLine("\n[LaTeX Refinement] [FEHLER] Ordner nicht gefunden. Bitte prüfe den SourceFolder in der Konfiguration.");
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
            if (partsCount <= 1) {
                Console.WriteLine("\n--- [LaTeX Refinement - Schritt 1: Merge & Zeitstempel-Abgleich] ---");
                Console.WriteLine($"  [INFO] NumberOfParts = {partsCount} (<= 1). Ein Merger ist nicht erforderlich. Überspringe Schritt 1.");
            }
            else {
                Console.WriteLine("\n--- [LaTeX Refinement - Schritt 1: Merge & Zeitstempel-Abgleich] ---");
                string? step1Output = await ExecuteStep1MergeAsync(currentFiles, _audioFilePath, baseName, targetFolder);
                if (step1Output == null) {
                    Console.WriteLine("\n[LaTeX Refinement] [FEHLER] Schritt 1 (Merge) fehlgeschlagen. Breche Pipeline ab.");
                    return;
                }
                currentFiles = [step1Output];
            }
        }

        // Step 2: Speech Refinement
        if (_config.Step2SpeechRefinement.Enabled) {
            Console.WriteLine("\n--- [LaTeX Refinement - Schritt 2: Textkorrektur & Grammatik-Polishing] ---");
            string? step2Output = await ExecuteStep2SpeechRefinementAsync(currentFiles[0], _audioFilePath, baseName, targetFolder);
            if (step2Output == null) {
                Console.WriteLine("\n[LaTeX Refinement] [FEHLER] Schritt 2 (Speech Refinement) fehlgeschlagen. Breche Pipeline ab.");
                return;
            }
            currentFiles = [step2Output];
        }

        // Step 3: Last Refinement
        if (_config.Step3LastRefinement.Enabled) {
            Console.WriteLine("\n--- [LaTeX Refinement - Schritt 3: Endprüfung & Validierung] ---");
            Console.WriteLine("  [INFO] Führe Probe-Kompilierung des aktuellen Dokuments aus...");
            bool alreadyCompiles = await CompilePdfAsync(currentFiles[0], baseName, targetFolder, "step3-precheck", allowRetryOnFailure: false);
            
            string compileLogPath = Path.Combine(targetFolder, "step3-precheck-compile-log.txt");
            string compileLog = System.IO.File.Exists(compileLogPath) ? await System.IO.File.ReadAllTextAsync(compileLogPath) : "";

            if (alreadyCompiles) {
                Console.WriteLine("  [INFO] Probe-Kompilierung erfolgreich! Keine Syntaxfehler vorhanden. Gebe diese Info an Schritt 3 weiter.");
            }
            else {
                Console.WriteLine("  [INFO] Probe-Kompilierung meldet Syntaxfehler. Gebe das Fehlerprotokoll an Schritt 3 weiter zur Korrektur.");
            }

            // [AI Context] Clean up temporary test-compile files (pdf, aux, log, out, toc, wrapper tex, precheck log)
            // so they do not clutter the output directory before final Step 4 PDF generation.
            CleanupPrecheckFiles(targetFolder, currentFiles[0], "step3-precheck");

            Console.WriteLine("  [INFO] Starte finalen Durchlauf für Schritt 3 (Last Refinement)...");
            var finalOutput = await ExecuteStep3LastRefinementAsync(currentFiles[0], baseName, targetFolder, alreadyCompiles, compileLog);
            if (finalOutput == null) {
                Console.WriteLine("\n[LaTeX Refinement] [FEHLER] Schritt 3 (Last Refinement) fehlgeschlagen.");
            }
            else {
                currentFiles = [finalOutput];
            }
        }

        // Step 4: PDF Compilation
        if (_config.PdfCompilation?.Enabled == true) {
            Console.WriteLine("\n--- [LaTeX Refinement - Schritt 4: PDF Generierung] ---");
            await CompilePdfAsync(currentFiles[0], baseName, targetFolder);
        }

        Console.WriteLine("\n[LaTeX Refinement] LaTeX Refinement Pipeline erfolgreich abgeschlossen!\n");
    }

    /// <summary>
    /// [AI Context] Compiles the final merged LaTeX file into a PDF using local pdflatex. Uses a wrapper file to inject the preamble.
    /// [Human] Baut das fertige LaTeX-Skript mithilfe einer Preamble (Design-Vorlage) zu einem PDF zusammen.
    /// </summary>
    private async Task<bool> CompilePdfAsync(string finalTexFile, string baseName, string targetFolder, string stepPrefix = "step4", bool allowRetryOnFailure = true) {
        if (!System.IO.File.Exists(finalTexFile)) {
            Console.WriteLine($"\n[LaTeX Refinement] [FEHLER] Kann PDF nicht generieren: {finalTexFile} existiert nicht.");
            return false;
        }

        string preamblePath = _config.PdfCompilation?.PreamblePath ?? "pdf-preamble.tex";
        if (!System.IO.File.Exists(preamblePath)) {
            Console.WriteLine($"\n[LaTeX Refinement] [WARNUNG] Preamble-Datei ({preamblePath}) nicht gefunden. Überspringe PDF-Generierung.");
            return false;
        }

        try {
            string preambleText = await System.IO.File.ReadAllTextAsync(preamblePath);
            string finalFileName = Path.GetFileName(finalTexFile);

            // Create the wrapper .tex file
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperFileName = $"{inputBaseName}-main.tex";
            string wrapperPath = Path.Combine(targetFolder, wrapperFileName);

            string wrapperContent = preambleText + "\n\\begin{document}\n\n" +
                                    $"\\input{{{finalFileName}}}\n\n" +
                                    "\\end{document}\n";

            await System.IO.File.WriteAllTextAsync(wrapperPath, wrapperContent);
            Console.WriteLine($"  [INFO] Wrapper-Datei erstellt: {wrapperPath}");

            var (success, log) = await LatexToolkit.CompilePdfAsync(wrapperPath);

            string logContent = FormatLatexLog(log, success);
            string logPath = Path.Combine(targetFolder, $"{stepPrefix}-compile-log.txt");
            await System.IO.File.WriteAllTextAsync(logPath, logContent);

            if (success) {
                // LaTeX creates aux files which can clutter the directory. 
                // We'll leave them for now in case the user wants to inspect them.
                Console.WriteLine($"  [INFO] PDF erfolgreich erstellt im Zielordner: {targetFolder}");
                if (logContent.Contains("⚠️ WARNING:")) {
                    Console.WriteLine($"  [INFO] Es gab LaTeX-Warnungen während der Kompilation. Details in: {stepPrefix}-compile-log.txt");
                }
                return true;
            }
            else {
                Console.WriteLine($"  [FEHLER] Fehler bei der PDF-Generierung. Protokoll gespeichert in: {logPath}");
                if (allowRetryOnFailure) {
                    Console.WriteLine("  [INFO] Starte automatische Fehlerbehebung durch erneute Korrekturanfrage an Gemini (-final-attempt)...");
                    string finalTexContent = await System.IO.File.ReadAllTextAsync(finalTexFile);
                    await ExecutePdfFixAttemptAsync(preambleText, finalTexContent, logContent, baseName, targetFolder);
                }
                return false;
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[LaTeX Refinement] [Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            Console.WriteLine("  [FEHLER] Unerwarteter Fehler bei der PDF-Generierung.");
            return false;
        }
    }

    /// <summary>
    /// [AI Context] Deletes intermediate files (.pdf, .aux, .log, wrapper .tex, etc.) generated during the Step 3 precheck compilation.
    /// [Human] Löscht temporäre Test-Dateien aus dem Pre-Check, damit der Zielordner sauber bleibt.
    /// </summary>
    private static void CleanupPrecheckFiles(string targetFolder, string finalTexFile, string stepPrefix) {
        try {
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperBase = $"{inputBaseName}-main";
            string[] extensions = [".tex", ".pdf", ".log", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"];
            foreach (var ext in extensions) {
                string filePath = Path.Combine(targetFolder, wrapperBase + ext);
                if (System.IO.File.Exists(filePath)) {
                    System.IO.File.Delete(filePath);
                }
            }
            string precheckLogPath = Path.Combine(targetFolder, $"{stepPrefix}-compile-log.txt");
            if (System.IO.File.Exists(precheckLogPath)) {
                System.IO.File.Delete(precheckLogPath);
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[LaTeX Refinement] [Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            Console.WriteLine("  [WARNUNG] Konnte temporäre Precheck-Dateien nicht vollständig bereinigen.");
        }
    }

    private static string FormatLatexLog(string rawLog, bool success) {
        var lines = rawLog.Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==========================================");
        sb.AppendLine($" LaTeX Compilation Log - {(success ? "SUCCESS" : "FAILED")}");
        sb.AppendLine("==========================================");
        sb.AppendLine();

        bool inError = false;
        int errorCount = 0;
        int warningCount = 0;

        foreach (var line in lines) {
            string tLine = line.Trim();
            if (tLine.StartsWith("! ") || tLine.StartsWith("Runaway argument?")) {
                sb.AppendLine();
                string errMsg = tLine.StartsWith("! ") ? tLine[2..] : tLine;
                sb.AppendLine("❌ ERROR: " + errMsg);
                inError = true;
                errorCount++;
            }
            else if (tLine.StartsWith("l.")) {
                sb.AppendLine("   Line: " + tLine);
                inError = false;
            }
            else if (tLine.Contains("Warning:", StringComparison.OrdinalIgnoreCase)) {
                sb.AppendLine("⚠️ WARNING: " + tLine);
                inError = false;
                warningCount++;
            }
            else if (tLine.StartsWith("Overfull") || tLine.StartsWith("Underfull")) {
                // Ignore layout noise
            }
            else if (inError && !string.IsNullOrWhiteSpace(tLine)) {
                sb.AppendLine("   " + tLine); // Continuation of error message
            }
            else if (string.IsNullOrWhiteSpace(tLine)) {
                inError = false;
            }
        }

        sb.Insert(0, $"Summary: {errorCount} Errors, {warningCount} Warnings\n\n");

        // If we failed but couldn't parse the errors cleanly, append raw log so nothing is lost
        if (!success && errorCount == 0) {
            sb.AppendLine("\n--- Raw Output (Could not format cleanly) ---");
            sb.AppendLine(rawLog);
        }

        return sb.ToString();
    }

    /// <summary>
    /// [AI Context] Step 1: Merges overlapping LaTeX chunks. If an audio file is provided, its metadata is attached to align timestamps correctly.
    /// [Human] Schritt 1: Führt die einzelnen Video-Teile zusammen. Nutzt (falls vorhanden) die Audio-Spur, um kaputte Zeitstempel zu korrigieren.
    /// </summary>
    private async Task<string?> ExecuteStep1MergeAsync(string[] inputFiles, string? audioFilePath, string baseName, string targetFolder) {
        if (inputFiles.Length == 0) return null;
        int partsCount = _extractionConfig?.NumberOfParts ?? inputFiles.Length;
        int overlapMin = (_extractionConfig?.OverlapSeconds ?? 180) / 60;

        string audioLengthStr = "unknown";
        string partTimestampsStr = "";

        bool audioExists = audioFilePath != null && System.IO.File.Exists(audioFilePath);

        List<Part> audioParts = [];
        if (audioExists) {
            double dur = await FfmpegUtilities.FfmpegToolkit.GetVideoDurationAsync(audioFilePath!);
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
                    Console.WriteLine("  [INFO] Verwende parallel im Hintergrund hochgeladene Audio-Datei.");
                    audioParts.AddRange(_preUploadedAudioAttachments);
                }
                else {
                    var handler = new AttachmentHandler(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
                    var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
                    if (success) {
                        audioParts.AddRange(attached);
                        Console.WriteLine($"  [INFO] Audio-Datei erfolgreich verarbeitet: {audioFilePath}");
                    }
                }
            }
        }

        bool audioAttached = audioExists && _config.Step1MergeAndTimestamp.AttachAudio;
        string outputFileName = $"step2-{baseName}-offset-merged.tex";
        string? result;

        if (audioAttached) {
            // FAKE HISTORY (Prefilling) APPROACH
            // Round 1 User Prompt (Tex Files)
            var round1Parts = new List<Part>();
            string round1Prompt = $"Here is the combined .tex file to process. It was generated with {partsCount} parts by some lecture videos provided with {overlapMin} minutes overlap. " +
                                  (string.IsNullOrEmpty(partTimestampsStr) ? "" : $"\nExpected total duration timestamps for each part:\n{partTimestampsStr}\n(Note: These timestamps represent the total chronological span of each video part, NOT the span of a single `spoken-clean` block!)\n\n") +
                                  "Please acknowledge you have read it. I will provide the audio file and final merge instructions in the next round.";
            round1Parts.Add(new Part { Text = round1Prompt });
            foreach (var file in inputFiles) {
                Console.WriteLine($"  [INFO] Lese Eingabedatei für Merge: {Path.GetFileName(file)}");
                string content = await System.IO.File.ReadAllTextAsync(file);
                round1Parts.Add(new Part { Text = $"=== FILE: {Path.GetFileName(file)} ===\n{content}\n=== END FILE ===" });
            }

            // Fake History: User Turn
            List<Content> history = [];
            history.Add(new Content { Role = "user", Parts = round1Parts });

            // Fake History: Model Acknowledgment Turn
            history.Add(new Content { Role = "model", Parts = [new Part { Text = "Understood. I have read the .tex files and noted the expected timestamps. I am ready for the audio file and the merge instructions." }] });

            // Round 2 User Prompt (Audio & Instructions)
            var round2Parts = new List<Part>();
            round2Parts.AddRange(audioParts);
            string round2Prompt = $"Here is the generated audio file. The actual audio length is exactly {audioLengthStr} (00:00:00 - {audioLengthStr}).\n\n" +
                                  $"The `spoken-clean` blocks timestamps need to perfectly align with this full duration. Please note that sometimes the timestamps in the `spoken-clean` blocks are horribly misaligned, so each block must be carefully checked and corrected to match the audio. Please perform the merge and timestamp correction according to the system instructions.";
            round2Parts.Add(new Part { Text = round2Prompt });

            // Final User Turn
            history.Add(new Content { Role = "user", Parts = round2Parts });

            Console.WriteLine("  [INFO] Verwende Multi-Turn-Struktur für Schritt 1 (Simulation von Audio + Textsegmenten).");
            result = await ExecuteGenerativeStepAsync(_config.Step1MergeAndTimestamp, history, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep1);
        }
        else {
            // SINGLE TURN APPROACH (Fallback)
            var parts = new List<Part>();
            string promptText = "Here is the combined file with all the offset parts together. " +
                                $"The .tex file was generated with {partsCount} parts by some lecture videos provided with {overlapMin} minutes overlap. " +
                                $"The actual audio/lecture length is roughly {audioLengthStr} (00:00:00 - {audioLengthStr}).\n\n" +
                                (string.IsNullOrEmpty(partTimestampsStr) ? "" : $"Expected total duration timestamps for each part:\n{partTimestampsStr}\n(Note: These timestamps represent the total chronological span of each video part, NOT the span of a single `spoken-clean` block!)\n\n") +
                                "Important: Since no audio file is attached, the timestamps in subsequent parts have already been pre-adjusted to global lecture time. Please eliminate redundant overlapping blocks at the part seams and only fix timestamps that look completely out of order or severely broken across boundaries. Otherwise, trust and preserve the existing pre-calibrated timestamps.";
            parts.Add(new Part { Text = promptText });
            foreach (var file in inputFiles) {
                Console.WriteLine($"  [INFO] Lese Eingabedatei für Merge: {Path.GetFileName(file)}");
                string content = await System.IO.File.ReadAllTextAsync(file);
                parts.Add(new Part { Text = $"=== FILE: {Path.GetFileName(file)} ===\n{content}\n=== END FILE ===" });
            }
            result = await ExecuteGenerativeStepAsync(_config.Step1MergeAndTimestamp, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep1);
        }

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    // Overload that takes single string
    private async Task<string?> ExecuteStep1MergeAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
        return await ExecuteStep1MergeAsync([inputFile], audioFilePath, baseName, targetFolder);
    }

    /// <summary>
    /// [AI Context] Step 2: Focuses strictly on fixing transcription errors within the `spoken-clean` environments by listening to the full audio.
    /// [Human] Schritt 2: Konzentriert sich nur auf den gesprochenen Text und verbessert ihn (Grammatik, Fehler), ohne den Mathe-Code kaputt zu machen.
    /// </summary>
    private async Task<string?> ExecuteStep2SpeechRefinementAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
        var parts = new List<Part>();

        bool audioAttached = _config.Step2SpeechRefinement.AttachAudio && audioFilePath != null && System.IO.File.Exists(audioFilePath);
        if (audioAttached) {
            var handler = new AttachmentHandler(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
            var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
            if (success) {
                parts.AddRange(attached);
                Console.WriteLine($"  [INFO] Audio-Datei erfolgreich verarbeitet: {audioFilePath}");
            }
        }

        string promptText = audioAttached ?
            "Please refine the text strictly in between the `spoken-clean` environments according to the system instructions. Listen to the provided audio to correct transcription mistakes. Do not alter the math or the timestamps." :
            "Please refine the text strictly in between the `spoken-clean` environments according to the system instructions. Do not alter the math or the timestamps.";
        parts.Add(new Part { Text = promptText });

        Console.WriteLine($"  [INFO] Lese Eingabedatei für Textkorrektur: {Path.GetFileName(inputFile)}");
        string content = await System.IO.File.ReadAllTextAsync(inputFile);
        parts.Add(new Part { Text = $"=== INPUT TEX ===\n{content}\n=== END INPUT TEX ===" });

        string outputFileName = $"step3-{baseName}-offset-speech_refined.tex";
        var result = await ExecuteGenerativeStepAsync(_config.Step2SpeechRefinement, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2);

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    /// <summary>
    /// [AI Context] Step 3: Final pass to fix general formatting issues or minor logical inconsistencies according to the system instructions.
    /// [Human] Schritt 3: Der letzte Feinschliff für das LaTeX-Dokument, bevor es kompiliert wird.
    /// </summary>
    private async Task<string?> ExecuteStep3LastRefinementAsync(string inputFile, string baseName, string targetFolder, bool alreadyCompiles, string compilerFeedbackLog) {
        // Simplified using target‑typed new and collection literal; the compiler infers List<Part>.
        List<Part> parts = [new() { Text = "Perform the final refinement and formatting pass on this document according to the system instructions." }];

        if (alreadyCompiles) {
            parts.Add(new Part { Text = "=== COMPILER STATUS ===\nThe input LaTeX document ALREADY COMPILES successfully without any LaTeX errors! Please preserve its valid syntax and structure while performing any final textual/typographical refinements according to the system instructions.\n=== END COMPILER STATUS ===" });
        }
        else if (!string.IsNullOrWhiteSpace(compilerFeedbackLog)) {
            parts.Add(new Part { Text = $"=== COMPILER ERROR FEEDBACK ===\nWhen attempting to compile the input LaTeX document with pdflatex, the following errors and log messages were produced:\n\n{compilerFeedbackLog}\n\nPlease analyze and fix these LaTeX syntax/compilation errors during this final refinement pass.\n=== END COMPILER ERROR FEEDBACK ===" });
        }

        Console.WriteLine($"  [INFO] Lese Eingabedatei für Formatierung: {Path.GetFileName(inputFile)}");
        string content = await System.IO.File.ReadAllTextAsync(inputFile);
        parts.Add(new Part { Text = $"=== INPUT TEX ===\n{content}\n=== END INPUT TEX ===" });

        string outputFileName = $"step4-{baseName}-offset-final.tex";
        var result = await ExecuteGenerativeStepAsync(_config.Step3LastRefinement, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep3);

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    /// <summary>
    /// [AI Context] Generic method to execute a generative API call. Handles automated retries, thinking budgets, system instructions, and completion markers.
    /// [Human] Die zentrale Funktion, um Prompts an Gemini zu senden. Behandelt auch Fehler, Warteschlangen und die "Thinking"-Modelle.
    /// </summary>
    private async Task<string?> ExecuteGenerativeStepAsync(RefinementStepConfig stepConfig, List<Part> userPromptParts, string targetOutputFolder, string outputFileName, string cacheStateFileName) {
        var finalPromptParts = new List<Part>(userPromptParts);
        var history = new List<Content> { new() { Role = "user", Parts = finalPromptParts } };
        return await ExecuteGenerativeStepAsync(stepConfig, history, targetOutputFolder, outputFileName, cacheStateFileName);
    }

    private async Task<string?> ExecuteGenerativeStepAsync(RefinementStepConfig stepConfig, List<Content> history, string targetOutputFolder, string outputFileName, string cacheStateFileName) {
        BackendParameters backendParams = _config.UseVertex ? stepConfig.Vertex : stepConfig.AiStudio;

        string systemInstructionText = "";
        // [AI Context] Note on Performance (.Length vs .Any()):
        // For arrays, checking '.Length > 0' is a direct property lookup (O(1)).
        // Calling '.Any()' creates an enumerator object under the hood, which causes unnecessary memory allocation.
        if (stepConfig.SystemInstructionPaths != null && stepConfig.SystemInstructionPaths.Length > 0) {
            Console.WriteLine("\n[LaTeX Refinement] Folgende System-Instruktionen sind konfiguriert:");
            var resolved = ExtractionHelpers.ResolveHistoryFiles(stepConfig.SystemInstructionPaths);
            ExtractionHelpers.PrintFileTree(resolved);
            foreach (var path in resolved) {
                if (System.IO.File.Exists(path)) {
                    Console.WriteLine($"  [INFO] Lade System-Instruktion: {path}");
                    string relPath = Path.GetFileName(path);
                    systemInstructionText += $"******\n------\n******\nHere is the file `{relPath}`:\n\n" + await System.IO.File.ReadAllTextAsync(path) + "\n\n";
                }
                else {
                    Console.WriteLine($"  [WARNUNG] System-Instruktion nicht gefunden und übersprungen: {path}");
                }
            }
        }

        // [AI Context] Context caching is Vertex AI only. AiStudio (Google API key) does not support caching.
        string? cacheName = null;
        if (_config.UseVertex && backendParams.UseContextCaching && !string.IsNullOrWhiteSpace(systemInstructionText)) {
            string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
            var savedState = ContextCacheStateManager.LoadState(cacheStateFileName);
            bool match = ContextCacheStateManager.MatchesConfig(
                savedState,
                backendParams.Model,
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
                Console.WriteLine($"  [INFO] Bestehender Google Kontext-Cache geladen: {cacheName}");
            }
            else {
                if (!string.IsNullOrEmpty(savedState.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
                }
                Console.WriteLine("  [INFO] Erstelle neuen Google Kontext-Cache...");
                try {
                    var cacheConfig = new CreateCachedContentConfig {
                        SystemInstruction = new() { Role = "system", Parts = [new() { Text = systemInstructionText }] },
                        DisplayName = $"latex-ref-{Path.GetFileNameWithoutExtension(outputFileName)}",
                        Ttl = $"{backendParams.ContextCachingMinutes * 60}s"
                    };
                    var created = await _client.Caches.CreateAsync(backendParams.Model, cacheConfig);
                    if (created != null && !string.IsNullOrEmpty(created.Name)) {
                        cacheName = created.Name;
                        savedState.CacheName = cacheName;
                        savedState.Model = backendParams.Model;
                        savedState.Temperature = backendParams.Temperature;
                        savedState.TopP = backendParams.TopP;
                        savedState.TopK = backendParams.TopK;
                        savedState.MaxOutputTokens = backendParams.MaxOutputTokens;
                        savedState.ThinkingBudget = backendParams.ThinkingBudget;
                        savedState.ThinkingLevel = backendParams.ThinkingLevel;
                        savedState.SystemInstructionChecksum = checksum;
                        savedState.ExpireTimeUtc = DateTime.UtcNow.AddMinutes(backendParams.ContextCachingMinutes);
                        if (created != null && created.ExpireTime.HasValue) {
                            savedState.ExpireTimeUtc = created.ExpireTime.Value.ToUniversalTime();
                        }
                        ContextCacheStateManager.SaveState(savedState, cacheStateFileName);
                        Console.WriteLine($"  [INFO] Google Kontext-Cache erfolgreich erstellt: {cacheName}");
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"  [FEHLER] Kontext-Caching fehlgeschlagen: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }
        else if (_config.UseVertex && !backendParams.UseContextCaching) {
            // [AI Context] If caching was previously active but is now disabled, clean up the remote cache.
            var sState = ContextCacheStateManager.LoadState(cacheStateFileName);
            if (!string.IsNullOrEmpty(sState.CacheName)) {
                await ContextCacheStateManager.DeleteRemoteAsync(_client, sState.CacheName);
                ContextCacheStateManager.ClearState(cacheStateFileName);
            }
        }

        // [AI Context] Validate context cache and auto-extend or re-create if expired or missing before this refinement step's API call.
        if (!string.IsNullOrEmpty(cacheName) && backendParams.UseContextCaching && !string.IsNullOrWhiteSpace(systemInstructionText)) {
            var cacheState = ContextCacheStateManager.LoadState(cacheStateFileName);
            double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
            bool cacheValid = false;

            if (remainingMin > 0) {
                if (remainingMin < backendParams.ContextCachingMinimumRemainingMinutes) {
                    Console.WriteLine($"  [Cache] TTL knapp ({remainingMin:F1} min). Verlängere automatisch um {backendParams.ContextCachingIncrementMinutes} min...");
                    var updatedState = await ContextCacheStateManager.ExtendCacheAsync(_client, cacheState, backendParams.ContextCachingIncrementMinutes, cacheStateFileName);
                    if (updatedState != null) {
                        Console.WriteLine($"  [Cache] Cache erfolgreich verlängert bis: {updatedState.ExpireTimeUtc.ToLocalTime():t}");
                        cacheValid = true;
                    }
                }
                else {
                    cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(_client, cacheName);
                }
            }

            if (!cacheValid) {
                Console.WriteLine("  [Cache] Cache abgelaufen oder ungültig. Erstelle neuen Google Kontext-Cache...");
                ContextCacheStateManager.ClearState(cacheStateFileName);
                cacheName = null;
                try {
                    string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
                    var cacheConfig = new CreateCachedContentConfig {
                        SystemInstruction = new() { Role = "system", Parts = [new() { Text = systemInstructionText }] },
                        DisplayName = $"latex-ref-{Path.GetFileNameWithoutExtension(outputFileName)}",
                        Ttl = $"{backendParams.ContextCachingMinutes * 60}s"
                    };
                    var created = await _client.Caches.CreateAsync(backendParams.Model, cacheConfig);
                    if (created != null && !string.IsNullOrEmpty(created.Name)) {
                        cacheName = created.Name;
                        var newState = new ContextCacheState {
                            CacheName = cacheName,
                            Model = backendParams.Model,
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
                        Console.WriteLine($"  [INFO] Google Kontext-Cache erfolgreich neu erstellt: {cacheName}");
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"  [FEHLER] Kontext-Caching fehlgeschlagen: {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

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

        if (backendParams.Model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) || backendParams.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
            bool isGemini3 = backendParams.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase);
            bool hasLevel = !string.IsNullOrEmpty(backendParams.ThinkingLevel) && isGemini3;
            bool hasBudget = backendParams.ThinkingBudget.HasValue;

            if (hasLevel || hasBudget) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (hasLevel) {
                    requestConfig.ThinkingConfig.ThinkingLevel = backendParams.ThinkingLevel!;
                }
                else if (hasBudget) {
                    int budget = backendParams.ThinkingBudget!.Value;
                    if (budget > 32768) budget = 32768;
                    requestConfig.ThinkingConfig.ThinkingBudget = budget;
                }
            }
        }

        // Inject completion marker constraint into the last user message
        var lastUserMsg = history.LastOrDefault(c => c.Role == "user");
        if (lastUserMsg != null && lastUserMsg.Parts != null) {
            lastUserMsg.Parts.Add(new Part { Text = "\n\nCRITICAL INSTRUCTION: When you have completely finished writing your response and there is nothing left to output, you MUST append the exact text '% [SYSTEM] Refinement complete' on a new line at the very end of your response. This is mandatory for the system to know you are done." });
        }

        // Dump the full conversation history that Gemini will read into a log file
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
            Console.WriteLine($"  [INFO] Gemini-Prompt-Log gespeichert unter: {promptDumpPath}");
        }
        catch (Exception ex) {
            Console.WriteLine($"  [WARNUNG] Konnte Prompt-Log nicht speichern: {ex.Message}");
        }

        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int totalCachedTokens = 0;

        string fullResponseText = "";
        int currentRequest = 1;
        int maxRequests = 5;

        using var cts = new CancellationTokenSource();
        void CancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
        Console.CancelKeyPress += CancelHandler;

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

            if (string.IsNullOrWhiteSpace(chunkResp)) {
                Console.WriteLine("\n[FEHLER] Das Modell hat eine komplett leere Antwort zurückgegeben (z.B. wegen MALFORMED_RESPONSE oder Safety-Filtern).");
                Console.WriteLine("Der Vorgang wird abgebrochen, um eine Endlosschleife (Continue-Prompt für leeren Text) zu vermeiden.");
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

            // [AI Context] Note on C# 8 Range Operator: 
            // 'chunkResp[^300..]' is modern C# syntax equivalent to 'chunkResp.Substring(chunkResp.Length - 300)'.
            // The '^' operator means "from the end", so '^300..' means "start 300 characters from the end, and go to the very end".
            string continuePrompt = $"[IMPORTANT] Your response was cut short due to token limits. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Do not open a new ```latex block, do not open a new environment, and do not open new math delimiters if you were already inside one. Just print the very next character.";

            Console.WriteLine("\n  [Refinement] Unerwartetes Ende der Antwort (Max Tokens?). Bereite automatisierten 'Continue'-Prompt vor...");
            Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

            // [AI Context] Note on C# 12 Collection Expressions:
            // The '[...]' syntax is a shorthand for 'new List<Part> { ... }' or 'new[] { ... }'.
            // It allows the compiler to infer the type and generate the most efficient allocation strategy.
            history.Add(new Content { Role = "model", Parts = [new Part { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new Part { Text = continuePrompt }] });

            // [AI Context] A 70-second delay is enforced here to accommodate strictly-enforced tokens-per-minute (TPM) and requests-per-minute (RPM) quotas by the API provider. 1m10s ensures a full quota refresh.
            // [Human] Wir warten hier 1 Minute und 10 Sekunden (70s), da wir ein hartes Limit von Tokens pro Minute haben. Das stellt sicher, dass das Limit vor dem nächsten Aufruf wieder zurückgesetzt ist.
            Console.WriteLine($"\n  [Timer] Warte 70 Sekunden vor der Fortsetzung, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
            if (!await ExtractionHelpers.SmartDelayAsync(70, "Warte auf Rate-Limits (Token Refill)...")) {
                Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= CancelHandler;

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
                                $"% Prompt Tokens: {totalInputTokens:N0}\n" +
                                $"% Candidates Tokens: {totalOutputTokens:N0} (inkl. Thinking Tokens)\n" +
                                $"% Cached Tokens: {totalCachedTokens:N0}\n" +
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

    /// <summary>
    /// [AI Context] Financial Guardrail: Ensures the GCS bucket is purged after processing to prevent long-term storage costs.
    /// [Human] Löscht temporäre Dateien im Google Cloud Storage Bucket, damit am Ende des Monats keine überraschenden Kosten entstehen.
    /// </summary>
    private async Task CleanupBucketAsync() {
        if (string.IsNullOrWhiteSpace(_config.VertexGcsBucketName)) return;
        try {
            Console.WriteLine($"\n  [GCS] Starte Cleanup: Lösche temporäre Dateien im Bucket '{_config.VertexGcsBucketName}'...");
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

    /// <summary>
    /// [AI Context] Fallback routine when initial PDF compilation fails. Sends the compile error log, preamble reference, and document body back to Gemini in a clean session (no system instructions, no context cache) to fix LaTeX syntax errors without outputting the preamble to save tokens.
    /// [Human] Neuer Versuch bei PDF-Fehlern: Schickt das Fehlerlog und den LaTeX-Body an Gemini zurück, um die Fehler zu korrigieren (ohne Preamble-Output zum Token-Sparen).
    /// </summary>
    private async Task ExecutePdfFixAttemptAsync(string preambleText, string failedBodyTex, string compileLog, string baseName, string targetFolder) {
        Console.WriteLine("\n--- [Schritt 4 Retry: PDF LaTeX Fix (-final-attempt)] ---");
        BackendParameters backendParams = _config.UseVertex ? _config.Step3LastRefinement.Vertex : _config.Step3LastRefinement.AiStudio;

        string promptText = $"=== PDF LATEX COMPILE LOG (WITH ERRORS) ===\n{compileLog}\n=== END COMPILE LOG ===\n\n" +
                            $"=== CURRENT LATEX PREAMBLE (FOR REFERENCE ONLY) ===\n{preambleText}\n=== END PREAMBLE ===\n\n" +
                            $"=== LATEX DOCUMENT BODY (TO FIX) ===\n{failedBodyTex}\n=== END DOCUMENT BODY ===\n\n" +
                            "Please fix the LaTeX compilation errors in the DOCUMENT BODY above. Note that the compile log error output might be incomplete or truncated, so carefully inspect the entire body for any potential LaTeX syntax errors, unescaped characters, or broken math environments.\n\n" +
                            "CRITICAL INSTRUCTION: DO NOT output the preamble! Even if certain LaTeX packages or libraries seem to be missing or undefined, DO NOT output any preamble, \\documentclass, or \\usepackage declarations! You MUST ONLY output the corrected LaTeX code that belongs between \\begin{document} and \\end{document} (do NOT include \\begin{document} and \\end{document} tags themselves). To save tokens, output ONLY the fixed document body content.";

        var promptParts = new List<Part> {
            new() { Text = promptText },
            new() { Text = "\n\nCRITICAL INSTRUCTION: When you have completely finished writing your response and there is nothing left to output, you MUST append the exact text '% [SYSTEM] Refinement complete' on a new line at the very end of your response. This is mandatory for the system to know you are done." }
        };

        var history = new List<Content> { new() { Role = "user", Parts = promptParts } };

        var requestConfig = new GenerateContentConfig {
            Temperature = backendParams.Temperature,
            TopP = backendParams.TopP,
            TopK = backendParams.TopK,
            MaxOutputTokens = backendParams.MaxOutputTokens
        };

        if (backendParams.Model.Contains("gemini-2", StringComparison.OrdinalIgnoreCase) || backendParams.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase)) {
            bool isGemini3 = backendParams.Model.Contains("gemini-3", StringComparison.OrdinalIgnoreCase);
            bool hasLevel = !string.IsNullOrEmpty(backendParams.ThinkingLevel) && isGemini3;
            bool hasBudget = backendParams.ThinkingBudget.HasValue;

            if (hasLevel || hasBudget) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (hasLevel) {
                    requestConfig.ThinkingConfig.ThinkingLevel = backendParams.ThinkingLevel!;
                }
                else if (hasBudget) {
                    int budget = backendParams.ThinkingBudget!.Value;
                    if (budget > 32768) budget = 32768;
                    requestConfig.ThinkingConfig.ThinkingBudget = budget;
                }
            }
        }

        string noPreambleFileName = $"step5-{baseName}-offset-last_try.tex";
        string standaloneFileName = $"step5-{baseName}-offset-last_try-main.tex";
        string outputFileName = standaloneFileName;
        string fullResponseText = "";
        int currentRequest = 1;
        int maxRequests = 5;

        using var cts = new CancellationTokenSource();
        void CancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
        Console.CancelKeyPress += CancelHandler;

        while (true) {
            Console.WriteLine($"\n  [API] Sende PDF-Fix-Anfrage an Gemini ({backendParams.Model}) (Request {currentRequest}/{maxRequests})...");
            string chunkResp = "";
            bool callSuccess = false;

            try {
                callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
                  streamFactory: () => _client.Models.GenerateContentStreamAsync(backendParams.Model, history, requestConfig),
                  onChunkReceived: async (chunk) => {
                      string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                      Console.Write(text);
                      chunkResp += text;
                      await Task.CompletedTask;
                  },
                  cancellationToken: cts.Token,
                  retryContext: outputFileName
                );
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                break;
            }

            if (!callSuccess) {
                Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
                break;
            }

            if (string.IsNullOrWhiteSpace(chunkResp)) {
                Console.WriteLine("\n[FEHLER] Das Modell hat eine leere Antwort zurückgegeben.");
                break;
            }

            fullResponseText += chunkResp;
            bool isComplete = chunkResp.Contains("% [SYSTEM] Refinement complete", StringComparison.OrdinalIgnoreCase);
            if (isComplete) break;

            if (currentRequest >= maxRequests) {
                Console.WriteLine($"\n\n[WARNUNG] Maximale Anzahl an Requests ({maxRequests}) für PDF-Fix erreicht. Breche ab.");
                break;
            }

            string continuePrompt = $"[IMPORTANT] Your response was cut short due to token limits. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Just print the very next character.";

            Console.WriteLine("\n  [PDF-Fix] Unerwartetes Ende der Antwort. Bereite Continue-Prompt vor...");
            history.Add(new Content { Role = "model", Parts = [new() { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new() { Text = continuePrompt }] });

            Console.WriteLine($"\n  [Timer] Warte 70 Sekunden vor der Fortsetzung...");
            if (!await ExtractionHelpers.SmartDelayAsync(70, "Warte auf Rate-Limits (Token Refill)...")) {
                break;
            }
            currentRequest++;
        }

        Console.CancelKeyPress -= CancelHandler;

        if (!string.IsNullOrEmpty(fullResponseText)) {
            string cleanedText = ExtractionHelpers.CleanLatexResponse(fullResponseText);

            // Version ohne Preamble
            string bodyOnlyText = cleanedText;
            int beginDocIdx = cleanedText.IndexOf("\\begin{document}", StringComparison.OrdinalIgnoreCase);
            int endDocIdx = cleanedText.IndexOf("\\end{document}", StringComparison.OrdinalIgnoreCase);
            if (beginDocIdx >= 0 && endDocIdx > beginDocIdx) {
                beginDocIdx += "\\begin{document}".Length;
                bodyOnlyText = cleanedText[beginDocIdx..endDocIdx].Trim();
            }
            string noPreamblePath = Path.Combine(targetFolder, noPreambleFileName);
            await System.IO.File.WriteAllTextAsync(noPreamblePath, bodyOnlyText);
            Console.WriteLine($"\n\n[INFO] Gefixte LaTeX-Datei (ohne Preamble) gespeichert unter: {noPreamblePath}");

            // Version mit Preamble (kompilierbar)
            string standaloneContent = preambleText + "\n\\begin{document}\n\n" + bodyOnlyText + "\n\n\\end{document}\n";
            string standalonePath = Path.Combine(targetFolder, standaloneFileName);
            await System.IO.File.WriteAllTextAsync(standalonePath, standaloneContent);
            Console.WriteLine($"[INFO] Gefixte LaTeX-Datei (mit Preamble) gespeichert unter: {standalonePath}");

            Console.WriteLine("  [INFO] Starte PDF-Kompilierung für step5 (last try)...");
            var (retrySuccess, retryLog) = await LatexToolkit.CompilePdfAsync(standalonePath);
            string retryLogContent = FormatLatexLog(retryLog, retrySuccess);
            string retryLogPath = Path.Combine(targetFolder, "compile-log-step5-last_try.txt");
            await System.IO.File.WriteAllTextAsync(retryLogPath, retryLogContent);

            if (retrySuccess) {
                Console.WriteLine($"  [INFO] PDF erfolgreich im finalen Versuch (step5) erstellt: {targetFolder}");
            }
            else {
                Console.WriteLine($"  [FEHLER] Auch step5 konnte das PDF nicht fehlerfrei kompilieren. Log in: {retryLogPath}");
            }
        }
    }

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
}
