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

        // [AI Context] Reset HasJustUploaded when starting the pipeline so that any background audio upload
        // or prior extraction steps don't suppress the initial 130-second token refill timer.
        AttachmentHandler.HasJustUploaded = false;

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
            CleanupPrecheckFiles(targetFolder, currentFiles[0], "step3-precheck", alreadyCompiles);

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
        if (_config.PdfCompilation?.Enabled == true || _config.PdfCompilation?.UseAntiGravityAgent == true) {
            Console.WriteLine("\n--- [LaTeX Refinement - Schritt 4: PDF Generierung & Validierung] ---");
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
            if (System.IO.File.Exists(finalTexFile)) {
                string bodyContent = await System.IO.File.ReadAllTextAsync(finalTexFile);
                if (bodyContent.Contains("\\begin{document}", StringComparison.OrdinalIgnoreCase) || bodyContent.Contains("\\end{document}", StringComparison.OrdinalIgnoreCase)) {
                    int beginDocIdx = bodyContent.IndexOf("\\begin{document}", StringComparison.OrdinalIgnoreCase);
                    int endDocIdx = bodyContent.IndexOf("\\end{document}", StringComparison.OrdinalIgnoreCase);
                    if (beginDocIdx >= 0 && endDocIdx > beginDocIdx) {
                        beginDocIdx += "\\begin{document}".Length;
                        bodyContent = bodyContent[beginDocIdx..endDocIdx].Trim();
                    }
                    else if (beginDocIdx >= 0 && endDocIdx < 0) {
                        beginDocIdx += "\\begin{document}".Length;
                        bodyContent = bodyContent[beginDocIdx..].Trim();
                    }
                    else if (endDocIdx >= 0 && beginDocIdx < 0) {
                        bodyContent = bodyContent[..endDocIdx].Trim();
                    }
                    bodyContent = DocumentTagsRegex().Replace(bodyContent, "").Trim();
                    await System.IO.File.WriteAllTextAsync(finalTexFile, bodyContent);
                    Console.WriteLine($"  [INFO] Verbleibende \\begin{{document}} / \\end{{document}} Tags aus {Path.GetFileName(finalTexFile)} entfernt.");
                }
            }

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

                string compiledPdfPath = wrapperPath.Replace(".tex", ".pdf");
                if (System.IO.File.Exists(compiledPdfPath)) {
                    // 1. Copy to clean prefix name (e.g. step3-refined_output-final.pdf)
                    string cleanPdfPath = Path.Combine(targetFolder, inputBaseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, cleanPdfPath, true);
                    Console.WriteLine($"  [INFO] PDF kopiert zu: {Path.GetFileName(cleanPdfPath)}");

                    // 2. If this is step4, copy it to the clean baseName.pdf (e.g. refined_output.pdf)
                    if (stepPrefix == "step4") {
                        string finalCleanPdfPath = Path.Combine(targetFolder, baseName + ".pdf");
                        System.IO.File.Copy(compiledPdfPath, finalCleanPdfPath, true);
                        Console.WriteLine($"  [INFO] Finales PDF kopiert zu: {Path.GetFileName(finalCleanPdfPath)}");
                    }
                }

                CleanupHelperFiles(targetFolder, finalTexFile, true);

                if (logContent.Contains("⚠️ WARNING:")) {
                    Console.WriteLine($"  [INFO] Es gab LaTeX-Warnungen während der Kompilation. Details in: {stepPrefix}-compile-log.txt");
                }
                return true;
            }
            else {
                Console.WriteLine($"  [FEHLER] Fehler bei der PDF-Generierung. Protokoll gespeichert in: {logPath}");
                CleanupHelperFiles(targetFolder, finalTexFile, false);
                if (allowRetryOnFailure) {
                    if (_config.PdfCompilation?.UseAntiGravityAgent == true) {
                        Console.WriteLine("\n[AntiGravity Agent Mode] PDF-Kompilierung fehlgeschlagen. Starte sofort interaktive Reparatur über AntiGravity (keine automatischen Gemini-Fix-Versuche)...");
                        return await RunAntiGravityAgentFixLoopAsync(finalTexFile, baseName, targetFolder, preambleText);
                    }

                    int maxRounds = _config.PdfCompilation?.MaxFixRounds ?? 3;
                    if (maxRounds <= 0) maxRounds = 1;

                    string currentBodyTex = await System.IO.File.ReadAllTextAsync(finalTexFile);
                    string currentLog = logContent;
                    bool anyRoundSucceeded = false;

                    for (int round = 1; round <= maxRounds; round++) {
                        Console.WriteLine($"\n==================================================================");
                        Console.WriteLine($"🤖 [AI PDF Fix Loop] Starte Reparatur-Runde {round} von {maxRounds}...");
                        Console.WriteLine($"==================================================================");

                        bool roundSuccess = await ExecutePdfFixAttemptAsync(preambleText, currentBodyTex, currentLog, baseName, targetFolder, round);

                        // Clean up previous failed round files so only the latest try remains
                        for (int prev = 1; prev < round; prev++) {
                            string prevNoPreamble = Path.Combine(targetFolder, $"step5-{baseName}-offset-last_try{prev}.tex");
                            string prevStandalone = Path.Combine(targetFolder, $"step5-{baseName}-offset-last_try{prev}-main.tex");
                            string prevLog = Path.Combine(targetFolder, $"compile-log-step5-last_try{prev}.txt");
                            try { if (System.IO.File.Exists(prevNoPreamble)) System.IO.File.Delete(prevNoPreamble); } catch (Exception ex) { Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}"); Console.WriteLine($"Originaler Fehlertext: {ex.Message}"); }
                            try { if (System.IO.File.Exists(prevStandalone)) System.IO.File.Delete(prevStandalone); } catch (Exception ex) { Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}"); Console.WriteLine($"Originaler Fehlertext: {ex.Message}"); }
                            try { if (System.IO.File.Exists(prevLog)) System.IO.File.Delete(prevLog); } catch (Exception ex) { Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}"); Console.WriteLine($"Originaler Fehlertext: {ex.Message}"); }
                        }

                        if (roundSuccess) {
                            anyRoundSucceeded = true;
                            break;
                        }

                        if (round < maxRounds) {
                            string nextTryFile = Path.Combine(targetFolder, $"step5-{baseName}-offset-last_try{round}.tex");
                            string nextLogFile = Path.Combine(targetFolder, $"compile-log-step5-last_try{round}.txt");
                            if (System.IO.File.Exists(nextTryFile)) {
                                currentBodyTex = await System.IO.File.ReadAllTextAsync(nextTryFile);
                            }
                            if (System.IO.File.Exists(nextLogFile)) {
                                currentLog = await System.IO.File.ReadAllTextAsync(nextLogFile);
                            }
                        }
                    }

                    if (anyRoundSucceeded) {
                        return true;
                    }

                    return false;
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
    private static void CleanupPrecheckFiles(string targetFolder, string finalTexFile, string stepPrefix, bool compilationSuccess = true) {
        try {
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperBase = $"{inputBaseName}-main";
            string[] extensions = compilationSuccess 
                ? [".tex", ".pdf", ".log", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"]
                : [".pdf", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"];
            foreach (var ext in extensions) {
                string filePath = Path.Combine(targetFolder, wrapperBase + ext);
                if (System.IO.File.Exists(filePath)) {
                    System.IO.File.Delete(filePath);
                }
            }
            if (compilationSuccess) {
                string precheckLogPath = Path.Combine(targetFolder, $"{stepPrefix}-compile-log.txt");
                if (System.IO.File.Exists(precheckLogPath)) {
                    System.IO.File.Delete(precheckLogPath);
                }
            }
            string cleanPdfPath = Path.Combine(targetFolder, inputBaseName + ".pdf");
            if (System.IO.File.Exists(cleanPdfPath)) {
                System.IO.File.Delete(cleanPdfPath);
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[LaTeX Refinement] [Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            Console.WriteLine("  [WARNUNG] Konnte temporäre Precheck-Dateien nicht vollständig bereinigen.");
        }
    }

    /// <summary>
    /// [AI Context] Deletes intermediate LaTeX compiler files (.main.tex, .main.pdf, .aux, .log, etc.) to keep the output folder clean.
    /// [Human] Löscht Hilfs- und Wrapperdateien des LaTeX-Compilers im Zielordner, um diesen sauber zu halten. Bei Fehlern bleiben .tex und .log zur Fehlersuche erhalten.
    /// </summary>
    private static void CleanupHelperFiles(string targetFolder, string finalTexFile, bool compilationSuccess = true) {
        try {
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperBase = $"{inputBaseName}-main";
            string[] extensions = compilationSuccess 
                ? [".tex", ".pdf", ".log", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"]
                : [".pdf", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"];
            foreach (var ext in extensions) {
                string filePath = Path.Combine(targetFolder, wrapperBase + ext);
                if (System.IO.File.Exists(filePath)) {
                    System.IO.File.Delete(filePath);
                }
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            Console.WriteLine($"  [WARNUNG] Hilfsdateien für {Path.GetFileName(finalTexFile)} konnten nicht vollständig bereinigt werden.");
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
                    Console.WriteLine("  [INFO] Verwende parallel im Hintergrund hochgeladene Audio-Datei.");
                    audioParts.AddRange(_preUploadedAudioAttachments);
                    AttachmentHandler.HasJustUploaded = false;
                }
                else {
                    var handler = new AttachmentHandler(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
                    var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
                    if (success) {
                        audioParts.AddRange(attached);
                        Console.WriteLine($"  [INFO] Audio-Datei erfolgreich verarbeitet: {audioFilePath}");
                        _preUploadedAudioAttachments = attached;
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
                round1Parts.Add(new Part { Text = $"<input_file name=\"{Path.GetFileName(file)}\">\n{content}\n</input_file>" });
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
                parts.Add(new Part { Text = $"<input_file name=\"{Path.GetFileName(file)}\">\n{content}\n</input_file>" });
            }
            AttachmentHandler.HasJustUploaded = false;
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
        bool audioAttached = _config.Step2SpeechRefinement.AttachAudio && audioFilePath != null && System.IO.File.Exists(audioFilePath);
        var audioParts = new List<Part>();

        if (audioAttached) {
            if (_preUploadedAudioAttachments != null && _preUploadedAudioAttachments.Count > 0) {
                Console.WriteLine("  [INFO] Verwende parallel im Hintergrund hochgeladene Audio-Datei.");
                audioParts.AddRange(_preUploadedAudioAttachments);
                AttachmentHandler.HasJustUploaded = false;
            }
            else {
                var handler = new AttachmentHandler(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "");
                var (success, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioFilePath}\"");
                if (success) {
                    audioParts.AddRange(attached);
                    Console.WriteLine($"  [INFO] Audio-Datei erfolgreich verarbeitet: {audioFilePath}");
                    _preUploadedAudioAttachments = attached;
                }
                else {
                    audioAttached = false;
                }
            }
        }

        Console.WriteLine($"  [INFO] Lese Eingabedatei für Textkorrektur: {Path.GetFileName(inputFile)}");
        string content = await System.IO.File.ReadAllTextAsync(inputFile);
        string outputFileName = $"step3-{baseName}-offset-speech_refined.tex";
        string? result;

        if (audioAttached && audioParts.Count > 0) {
            // FAKE HISTORY (Prefilling) APPROACH for Speech Refinement
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

            Console.WriteLine("  [INFO] Verwende Multi-Turn-Struktur für Schritt 2 (Simulation von Text-Dokument + Audio-Refinement).");
            AttachmentHandler.HasJustUploaded = false;
            result = await ExecuteGenerativeStepAsync(_config.Step2SpeechRefinement, history, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2);
        }
        else {
            // SINGLE TURN APPROACH (Fallback without audio)
            var parts = new List<Part> {
                new() { Text = "Please refine the text strictly in between the `spoken-clean` environments according to the system instructions. Do not alter the math or the timestamps." },
                new() { Text = $"<input_tex>\n{content}\n</input_tex>" }
            };
            AttachmentHandler.HasJustUploaded = false;
            result = await ExecuteGenerativeStepAsync(_config.Step2SpeechRefinement, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2);
        }

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
            parts.Add(new Part { Text = "<compiler_status>\nThe input LaTeX document ALREADY COMPILES successfully without any LaTeX errors! Please preserve its valid syntax and structure while performing any final textual/typographical refinements according to the system instructions.\n</compiler_status>" });
        }
        else if (!string.IsNullOrWhiteSpace(compilerFeedbackLog)) {
            parts.Add(new Part { Text = $"<compiler_error_feedback>\nWhen attempting to compile the input LaTeX document with pdflatex, the following errors and log messages were produced:\n\n{compilerFeedbackLog}\n\nPlease analyze and fix these LaTeX syntax/compilation errors during this final refinement pass.\n</compiler_error_feedback>" });
        }

        Console.WriteLine($"  [INFO] Lese Eingabedatei für Formatierung: {Path.GetFileName(inputFile)}");
        string content = await System.IO.File.ReadAllTextAsync(inputFile);
        parts.Add(new Part { Text = $"<input_tex>\n{content}\n</input_tex>" });

        string outputFileName = $"step4-{baseName}-offset-final.tex";
        AttachmentHandler.HasJustUploaded = false;
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
            var resolved = HistoryFileResolver.ResolveHistoryFiles(stepConfig.SystemInstructionPaths);
            FileTreeRenderer.PrintFileTree(resolved);
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
            // [AI Context] Reset the rate-limit timer to now: loading system instructions takes time,
            // so the guard will count from here and enforce a proper gap before the first API call.
            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;
        }

        // [AI Context] Context caching is Vertex AI only. AiStudio (Google API key) does not support caching.
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
                Console.WriteLine($"  [INFO] Bestehender Google Kontext-Cache geladen: {cacheName}");
            }
            else {
                if (!string.IsNullOrEmpty(savedState.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
                }
                Console.WriteLine("  [INFO] Erstelle neuen Google Kontext-Cache...");
                cacheName = await CreateContextCacheAsync(backendParams, systemInstructionText, outputFileName, checksum, cacheStateFileName, isRecreate: false);
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
                string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
                cacheName = await CreateContextCacheAsync(backendParams, systemInstructionText, outputFileName, checksum, cacheStateFileName, isRecreate: true);
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
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            Console.WriteLine($"  [WARNUNG] Konnte Prompt-Log nicht speichern.");
        }

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
                Console.WriteLine($"  [INFO] Structural Integrity Tracker: Erwarte ca. {expectedSpokenClean}x spoken-clean und {expectedMathStroke}x math-stroke Blöcke im Output.");
            }
        }
        catch { }

        int totalInputTokens = 0;
        int totalOutputTokens = 0;
        int totalCachedTokens = 0;

        string fullResponseText = "";
        int currentRequest = 1;
        int maxRequests = 5;
        int emptyResponseRetries = 0;

        using var cts = new CancellationTokenSource();
        void CancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch (Exception ex) { Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}"); Console.WriteLine($"Originaler Fehlertext: {ex.Message}"); } }
        Console.CancelKeyPress += CancelHandler;

        while (true) {
            string providerName = _config.UseVertex ? "Vertex AI" : "Google AI Studio";

            int rateLimitDelay = stepConfig.RateLimitDelaySeconds > 0 ? stepConfig.RateLimitDelaySeconds : 130;
            double secondsSinceLastGen = (DateTime.UtcNow - InteractiveDelay.LastGenerationCompletionTimeUtc).TotalSeconds;
            if (secondsSinceLastGen < rateLimitDelay && !InteractiveDelay.IsInSmartDelay) {
                int waitRemaining = (int)Math.Ceiling(rateLimitDelay - secondsSinceLastGen);
                Console.WriteLine($"\n[Rate-Limit & Quota Schutz] Warte verbleibende {waitRemaining} Sekunden vor dem nächsten API-Aufruf...");
                if (!await InteractiveDelay.SmartDelayAsync(waitRemaining, "Warte auf Rate-Limits (Token-Refill Schutz vor API-Aufruf)...")) {
                    break;
                }
            }
            AttachmentHandler.HasJustUploaded = false;

            Console.WriteLine($"\n  [API] Sende Anfrage an {providerName} ({backendParams.CurrentModel}) (Request {currentRequest}/{maxRequests})...");

            string chunkResp = "";
            bool callSuccess = false;

            try {
                callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
                  streamFactory: () => _client.Models.GenerateContentStreamAsync(backendParams.CurrentModel, history, requestConfig),
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
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
                break;
            }

            if (!callSuccess) {
                Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
                break;
            }

            if (string.IsNullOrWhiteSpace(chunkResp)) {
                if (emptyResponseRetries < 3) {
                    emptyResponseRetries++;
                    Console.WriteLine("\n[FEHLER] Das Modell hat eine komplett leere Antwort zurückgegeben (z.B. wegen MALFORMED_RESPONSE oder Safety-Filtern).");
                    Console.WriteLine($"Warte 5 Sekunden vor Versuch {emptyResponseRetries}/3...");
                    await Task.Delay(5000, cts.Token);
                    continue;
                }
                else {
                    Console.WriteLine("\n[FEHLER] Das Modell hat nach 3 Versuchen weiterhin eine komplett leere Antwort zurückgegeben.");
                    Console.WriteLine("Der Vorgang wird abgebrochen, um eine Endlosschleife (Continue-Prompt für leeren Text) zu vermeiden.");
                    break;
                }
            }

            emptyResponseRetries = 0; // Reset retry counter on success
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

            bool closedBlock = chunkResp.TrimEnd().EndsWith("```");
            string continuePrompt = $"[IMPORTANT] Your response was cut short due to token limits. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Do not open a new ```latex block, do not open a new environment, and do not open new math delimiters if you were already inside one. Just print the very next character.";

            if (closedBlock) {
                continuePrompt += "\n\n[WARNING] It looks like you closed the ```latex markdown block, but you forgot the '% [SYSTEM] Refinement complete' marker. If you have not finished transcribing/refining the ENTIRE document, DO NOT just send the marker! You must continue transcribing the remaining content of the lecture. Open a new ```latex block and continue the transcription.";
            }

            Console.WriteLine("\n  [Refinement] Unerwartetes Ende der Antwort. Bereite automatisierten 'Continue'-Prompt vor...");
            Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

            // [AI Context] Note on C# 12 Collection Expressions:
            // The '[...]' syntax is a shorthand for 'new List<Part> { ... }' or 'new[] { ... }'.
            // It allows the compiler to infer the type and generate the most efficient allocation strategy.
            history.Add(new Content { Role = "model", Parts = [new Part { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new Part { Text = continuePrompt }] });

            // [AI Context] A delay is enforced here to accommodate strictly-enforced tokens-per-minute (TPM) and requests-per-minute (RPM) quotas by the API provider.
            // [Human] Wir warten hier, da wir ein hartes Limit von Tokens pro Minute haben. Das stellt sicher, dass das Limit vor dem nächsten Aufruf wieder zurückgesetzt ist.
            Console.WriteLine($"  [Rate-Limit] Warte {rateLimitDelay} Sekunden (Token Refill), damit die Quota vor den Batch-Teilen vollständig zurückgesetzt ist...");
            if (!await InteractiveDelay.SmartDelayAsync(rateLimitDelay, "Warte auf Rate-Limits (Token Refill)...")) {
                Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= CancelHandler;

        // --- STRUCTURAL INTEGRITY VERIFICATION ---
        if (!string.IsNullOrEmpty(fullResponseText) && (expectedSpokenClean > 0 || expectedMathStroke > 0)) {
            int actualSpokenClean = SpokenCleanRegex().Count(fullResponseText);
            int actualMathStroke = MathStrokeRegex().Count(fullResponseText);
            
            // Tolerance: LLM shouldn't drop more than 40% of the blocks (allows normal merging by Schritt 1).
            int minExpectedSpoken = (int)(expectedSpokenClean * 0.6);
            int minExpectedMath = (int)(expectedMathStroke * 0.6);

            if (actualSpokenClean < minExpectedSpoken || actualMathStroke < minExpectedMath) {
                Console.WriteLine($"\n[FATAL ERROR] SILENT TRUNCATION DETECTED!");
                Console.WriteLine($"[FATAL ERROR] Das Modell hat einen großen Teil des Textes übersprungen oder abgeschnitten.");
                Console.WriteLine($"[FATAL ERROR] Erwartet: ~{expectedSpokenClean} spoken-clean / ~{expectedMathStroke} math-stroke.");
                Console.WriteLine($"[FATAL ERROR] Erhalten: {actualSpokenClean} spoken-clean / {actualMathStroke} math-stroke.");
                Console.WriteLine($"[FATAL ERROR] Datei wird aus Sicherheitsgründen NICHT gespeichert, da massiver Datenverlust vorliegt.");
                return null;
            }
            else {
                Console.WriteLine($"  [INFO] Structural Integrity Verified: {actualSpokenClean}/{expectedSpokenClean} spoken-clean, {actualMathStroke}/{expectedMathStroke} math-stroke.");
            }
        }
        // -----------------------------------------

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
            Console.WriteLine($"\n\n[Erfolg] Ergebnis gespeichert unter: {outPath}");

            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            return outPath;
        }
        else {
            Console.WriteLine($"\n[Fehler] Beim Refinement ist ein Fehler aufgetreten oder der Vorgang wurde abgebrochen.");
            return null;
        }
    }

    /// <summary>
    /// [AI Context] Creates a new Vertex context cache for a refinement step's system instruction and
    /// persists its state. Shared by the two ExecuteGenerativeStepAsync creation paths (initial
    /// cache-miss and expired-cache recreation), which were previously two near-identical inline copies.
    /// [Human] Legt einen neuen Kontext-Cache für einen Refinement-Schritt an und speichert dessen Zustand.
    /// </summary>
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
                    Console.WriteLine($"  [INFO] Google Kontext-Cache erfolgreich neu erstellt: {cacheName}");
                }
                else {
                    Console.WriteLine($"  [INFO] Google Kontext-Cache erfolgreich erstellt: {cacheName}");
                }
                return cacheName;
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"  [FEHLER] Kontext-Caching fehlgeschlagen: {ex.GetType().Name} - {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// [AI Context] Financial Guardrail: Ensures the GCS bucket is purged after processing to prevent long-term storage costs.
    /// [Human] Löscht temporäre Dateien im Google Cloud Storage Bucket, damit am Ende des Monats keine überraschenden Kosten entstehen.
    /// </summary>
    private Task CleanupBucketAsync() => GcsWorkspace.PurgeAsync(_config.VertexGcsBucketName);

    /// <summary>
    /// [AI Context] Fallback routine when initial PDF compilation fails. Sends the compile error log, preamble reference, and document body back to Gemini in a clean session (no system instructions, no context cache) to fix LaTeX syntax errors without outputting the preamble to save tokens.
    /// [Human] Neuer Versuch bei PDF-Fehlern: Schickt das Fehlerlog und den LaTeX-Body an Gemini zurück, um die Fehler zu korrigieren (ohne Preamble-Output zum Token-Sparen).
    /// </summary>
    private async Task<bool> ExecutePdfFixAttemptAsync(string preambleText, string failedBodyTex, string compileLog, string baseName, string targetFolder, int roundNumber = 1) {
        Console.WriteLine($"\n--- [Schritt 4 Retry: PDF LaTeX Fix (-final-attempt, Runde #{roundNumber})] ---");
        BackendParameters backendParams = _config.UseVertex ? _config.Step3LastRefinement.Vertex : _config.Step3LastRefinement.AiStudio;

        string promptText = $"<pdf_latex_compile_log>\n{compileLog}\n</pdf_latex_compile_log>\n\n" +
                            $"<current_latex_preamble note=\"FOR REFERENCE ONLY - DO NOT OUTPUT THIS PREAMBLE OR INVENT YOUR OWN\">\n{preambleText}\n</current_latex_preamble>\n\n" +
                            $"<latex_document_body_to_fix>\n{failedBodyTex}\n</latex_document_body_to_fix>\n\n" +
                            "Please fix the LaTeX compilation errors in the DOCUMENT BODY above. Note that the compile log error output might be incomplete or truncated, so carefully inspect the entire body for any potential LaTeX syntax errors, unescaped characters, or broken math environments.\n\n" +
                            "CRITICAL INSTRUCTION: DO NOT output the preamble! Even if certain LaTeX packages or libraries seem to be missing or undefined, DO NOT output any preamble, \\documentclass, or \\usepackage declarations! You MUST ONLY output the corrected LaTeX code that belongs between \\begin{document} and \\end{document} (do NOT include \\begin{document} and \\end{document} tags themselves). To save tokens, output ONLY the fixed document body content. Use the preamble above strictly as reference so you do not invent your own preamble.";

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

        string noPreambleFileName = $"step5-{baseName}-offset-last_try{roundNumber}.tex";
        string standaloneFileName = $"step5-{baseName}-offset-last_try{roundNumber}-main.tex";
        string outputFileName = standaloneFileName;
        string fullResponseText = "";
        int currentRequest = 1;
        int maxRequests = 5;
        int emptyResponseRetries = 0;

        using var cts = new CancellationTokenSource();
        void CancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch (Exception ex) { Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}"); Console.WriteLine($"Originaler Fehlertext: {ex.Message}"); } }
        Console.CancelKeyPress += CancelHandler;

        while (true) {
            string providerName = _config.UseVertex ? "Vertex AI" : "Google AI Studio";

            int fixDelay = _config.Step3LastRefinement?.RateLimitDelaySeconds > 0 ? _config.Step3LastRefinement.RateLimitDelaySeconds : 130;
            double secondsSinceLastGen = (DateTime.UtcNow - InteractiveDelay.LastGenerationCompletionTimeUtc).TotalSeconds;
            if (secondsSinceLastGen < fixDelay && !InteractiveDelay.IsInSmartDelay) {
                int waitRemaining = (int)Math.Ceiling(fixDelay - secondsSinceLastGen);
                Console.WriteLine($"\n[Rate-Limit & Quota Schutz] Warte verbleibende {waitRemaining} Sekunden vor dem PDF-Fix-Request...");
                if (!await InteractiveDelay.SmartDelayAsync(waitRemaining, "Warte auf Rate-Limits (Token-Refill Schutz vor PDF-Fix-Request)...")) {
                    break;
                }
            }
            AttachmentHandler.HasJustUploaded = false;

            Console.WriteLine($"\n  [API] Sende PDF-Fix-Anfrage an {providerName} ({backendParams.CurrentModel}) (Request {currentRequest}/{maxRequests})...");

            string chunkResp = "";
            bool callSuccess = false;

            try {
                callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
                  streamFactory: () => _client.Models.GenerateContentStreamAsync(backendParams.CurrentModel, history, requestConfig),
                  onChunkReceived: async (chunk) => {
                      string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                      Console.Write(text);
                      chunkResp += text;
                      await Task.CompletedTask;
                  },
                  cancellationToken: cts.Token,
                  retryContext: outputFileName,
                  onRetry: () => { chunkResp = ""; }
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
                if (emptyResponseRetries < 3) {
                    emptyResponseRetries++;
                    Console.WriteLine("\n[FEHLER] Das Modell hat eine komplett leere Antwort zurückgegeben (z.B. wegen MALFORMED_RESPONSE oder Safety-Filtern).");
                    Console.WriteLine($"Warte 5 Sekunden vor Versuch {emptyResponseRetries}/3...");
                    await Task.Delay(5000, cts.Token);
                    continue;
                }
                else {
                    Console.WriteLine("\n[FEHLER] Das Modell hat nach 3 Versuchen weiterhin eine komplett leere Antwort zurückgegeben.");
                    Console.WriteLine("Der Vorgang wird abgebrochen, um eine Endlosschleife (Continue-Prompt für leeren Text) zu vermeiden.");
                    break;
                }
            }

            emptyResponseRetries = 0; // Reset retry counter on success
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

            Console.WriteLine($"\n  [Timer] Warte {fixDelay} Sekunden vor der Fortsetzung...");
            if (!await InteractiveDelay.SmartDelayAsync(fixDelay, "Warte auf Rate-Limits (Token Refill)...")) {
                break;
            }
            currentRequest++;
        }

        Console.CancelKeyPress -= CancelHandler;

        if (!string.IsNullOrEmpty(fullResponseText)) {
            string cleanedText = LatexResponseCleaner.CleanLatexResponse(fullResponseText);

            // Version ohne Preamble und komplett bereinigt von \begin{document} / \end{document}
            string bodyOnlyText = cleanedText;
            int beginDocIdx = cleanedText.IndexOf("\\begin{document}", StringComparison.OrdinalIgnoreCase);
            int endDocIdx = cleanedText.IndexOf("\\end{document}", StringComparison.OrdinalIgnoreCase);
            if (beginDocIdx >= 0 && endDocIdx > beginDocIdx) {
                beginDocIdx += "\\begin{document}".Length;
                bodyOnlyText = cleanedText[beginDocIdx..endDocIdx].Trim();
            }
            else if (beginDocIdx >= 0 && endDocIdx < 0) {
                beginDocIdx += "\\begin{document}".Length;
                bodyOnlyText = cleanedText[beginDocIdx..].Trim();
            }
            else if (endDocIdx >= 0 && beginDocIdx < 0) {
                bodyOnlyText = cleanedText[..endDocIdx].Trim();
            }
            bodyOnlyText = DocumentTagsRegex().Replace(bodyOnlyText, "").Trim();

            string noPreamblePath = Path.Combine(targetFolder, noPreambleFileName);
            await System.IO.File.WriteAllTextAsync(noPreamblePath, bodyOnlyText);
            Console.WriteLine($"\n\n[INFO] Gefixte LaTeX-Datei (Versuch #{roundNumber}, ohne Preamble/\\end{{document}}) gespeichert unter: {noPreamblePath}");

            // Version mit Preamble (kompilierbar via -main.tex)
            string standaloneContent = preambleText + "\n\\begin{document}\n\n" + bodyOnlyText + "\n\n\\end{document}\n";
            string standalonePath = Path.Combine(targetFolder, standaloneFileName);
            await System.IO.File.WriteAllTextAsync(standalonePath, standaloneContent);
            Console.WriteLine($"[INFO] Gefixte LaTeX-Datei (Versuch #{roundNumber}, mit Preamble) gespeichert unter: {standalonePath}");

            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            Console.WriteLine($"  [INFO] Starte PDF-Kompilierung für step5 (Fix-Versuch #{roundNumber})...");
            var (retrySuccess, retryLog) = await LatexToolkit.CompilePdfAsync(standalonePath);
            string retryLogContent = FormatLatexLog(retryLog, retrySuccess);
            string retryLogPath = Path.Combine(targetFolder, $"compile-log-step5-last_try{roundNumber}.txt");
            await System.IO.File.WriteAllTextAsync(retryLogPath, retryLogContent);

            if (retrySuccess) {
                Console.WriteLine($"  [INFO] 🎉 PDF erfolgreich im Fix-Versuch #{roundNumber} (step5) erstellt: {targetFolder}");
                string compiledPdfPath = Path.Combine(targetFolder, standaloneFileName.Replace(".tex", ".pdf"));
                if (System.IO.File.Exists(compiledPdfPath)) {
                    // 1. Copy to clean prefix name (e.g. step5-refined_output-offset-last_try1.pdf)
                    string cleanPdfPath = Path.Combine(targetFolder, noPreambleFileName.Replace(".tex", ".pdf"));
                    System.IO.File.Copy(compiledPdfPath, cleanPdfPath, true);
                    Console.WriteLine($"  [INFO] PDF kopiert zu: {Path.GetFileName(cleanPdfPath)}");

                    // 2. Copy to clean baseName.pdf (e.g. refined_output.pdf)
                    string finalCleanPdfPath = Path.Combine(targetFolder, baseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, finalCleanPdfPath, true);
                    Console.WriteLine($"  [INFO] Finales PDF kopiert zu: {Path.GetFileName(finalCleanPdfPath)}");
                }
                CleanupHelperFiles(targetFolder, Path.Combine(targetFolder, noPreambleFileName), true);
                return true;
            }
            else {
                Console.WriteLine($"  [FEHLER] Auch Fix-Versuch #{roundNumber} konnte das PDF nicht fehlerfrei kompilieren. Log in: {retryLogPath}");
                CleanupHelperFiles(targetFolder, Path.Combine(targetFolder, noPreambleFileName), false);
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// [AI Context] Automated loop that calls the Google Antigravity Agent via v1beta/interactions REST API to fix compilation errors in a secure remote sandbox.
    /// [Human] Ruft den echten Google Antigravity-Agenten über die REST-Schnittstelle auf, um LaTeX-Fehler vollautomatisch in einer Sandbox zu reparieren.
    /// </summary>
    private async Task<bool> RunAntiGravityAgentFixLoopAsync(string finalTexFile, string baseName, string targetFolder, string preambleText) {
        int maxRounds = _config.PdfCompilation?.MaxFixRounds ?? 3;
        if (maxRounds <= 0) maxRounds = 1;

        string? envVarName = (_config.AiStudioApiKeyEnvNames != null && _config.AiStudioApiKeyEnvNames.Length > _config.AiStudioActiveApiProfile)
            ? _config.AiStudioApiKeyEnvNames[_config.AiStudioActiveApiProfile]
            : "API_KEY";

        string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envVarName);
        if (string.IsNullOrEmpty(apiKey)) {
            Console.WriteLine($"\n[FEHLER] Antigravity Agent benötigt einen gültigen API-Key in der Umgebungsvariable '{envVarName}'.");
            return false;
        }

        using var httpClient = new System.Net.Http.HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(20);
        httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        for (int round = 1; round <= maxRounds; round++) {
            Console.WriteLine($"\n==================================================================================");
            Console.WriteLine($"🚀 [Antigravity Agent API] Starte Reparatur-Runde {round} von {maxRounds}...");
            Console.WriteLine($"==================================================================================");

            // Re-create the wrapper file since it was cleaned up
            string preamblePath = _config.PdfCompilation?.PreamblePath ?? "pdf-preamble.tex";
            string preamble = System.IO.File.Exists(preamblePath) ? await System.IO.File.ReadAllTextAsync(preamblePath) : preambleText;
            string finalFileName = Path.GetFileName(finalTexFile);
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperFileName = $"{inputBaseName}-main.tex";
            string wrapperPath = Path.Combine(targetFolder, wrapperFileName);
            string wrapperContent = preamble + "\n\\begin{document}\n\n" + $"\\input{{{finalFileName}}}\n\n" + "\\end{document}\n";
            await System.IO.File.WriteAllTextAsync(wrapperPath, wrapperContent);

            var (success, log) = await LatexToolkit.CompilePdfAsync(wrapperPath);
            string logContent = FormatLatexLog(log, success);
            string logPath = Path.Combine(targetFolder, "step4-compile-log.txt");
            await System.IO.File.WriteAllTextAsync(logPath, logContent);

            if (success) {
                Console.WriteLine($"\n[LatexToolkit] 🎉 PDF durch Antigravity Agent erfolgreich (Runde {round}/{maxRounds}) generiert!");
                string compiledPdfPath = wrapperPath.Replace(".tex", ".pdf");
                if (System.IO.File.Exists(compiledPdfPath)) {
                    string cleanPdfPath = Path.Combine(targetFolder, inputBaseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, cleanPdfPath, true);
                    Console.WriteLine($"  [INFO] PDF kopiert zu: {Path.GetFileName(cleanPdfPath)}");

                    string finalCleanPdfPath = Path.Combine(targetFolder, baseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, finalCleanPdfPath, true);
                    Console.WriteLine($"  [INFO] Finales PDF kopiert zu: {Path.GetFileName(finalCleanPdfPath)}");
                }
                CleanupHelperFiles(targetFolder, finalTexFile, true);
                return true;
            }
            else {
                CleanupHelperFiles(targetFolder, finalTexFile, false);
                Console.WriteLine("\n==================================================================================");
                Console.WriteLine($"🤖 [Antigravity Agent API] PDF-Generierung fehlgeschlagen (Runde {round} von {maxRounds})!");
                Console.WriteLine("Der LaTeX-Compiler meldet Fehler. Sende Log und Code an den Remote-Agenten...");
                
                string currentLatexContent = await System.IO.File.ReadAllTextAsync(finalTexFile);

                string prompt = $@"We are trying to compile a LaTeX document, but pdflatex encountered errors.
You are the Antigravity Agent. Please fix the LaTeX code.

The preamble is managed by a wrapper script. Do not write the preamble, only output the fixed content for the body file.

### Error Log
```text
{logContent}
```

### Current File Contents (`{finalFileName}`)
```latex
{currentLatexContent}
```

Please return the fully corrected contents of `{finalFileName}` inside a ```latex code block. DO NOT use \begin{{document}} or \end{{document}}.";

                var payload = new {
                    agent = "antigravity-preview-05-2026",
                    environment = "remote",
                    input = prompt
                };

                string jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
                var content = new System.Net.Http.StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                Console.WriteLine($"⏳ [Antigravity Agent API] Kontaktiere Google Cloud (v1beta/interactions) für automatische Korrektur...");
                
                try {
                    var response = await httpClient.PostAsync("https://generativelanguage.googleapis.com/v1beta/interactions", content);
                    string responseBody = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode) {
                        Console.WriteLine($"\n[FEHLER] Antigravity Agent API Aufruf fehlgeschlagen: {response.StatusCode}");
                        Console.WriteLine($"Response: {responseBody}");
                        return false;
                    }

                    using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
                    string agentOutput = "";
                    if (doc.RootElement.TryGetProperty("output_text", out var outputTextElement) && outputTextElement.ValueKind == System.Text.Json.JsonValueKind.String) {
                        agentOutput = outputTextElement.GetString() ?? "";
                    }

                    // Fallback: Wenn kein output_text da ist, extrahieren wir allen Text aus den "steps"
                    if (string.IsNullOrWhiteSpace(agentOutput) && doc.RootElement.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == System.Text.Json.JsonValueKind.Array) {
                        var sb = new System.Text.StringBuilder();
                        foreach (var step in stepsElement.EnumerateArray()) {
                            if (step.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == System.Text.Json.JsonValueKind.Array) {
                                foreach (var item in summaryElement.EnumerateArray()) {
                                    if (item.TryGetProperty("text", out var txtElement) && txtElement.ValueKind == System.Text.Json.JsonValueKind.String) {
                                        sb.AppendLine(txtElement.GetString());
                                    }
                                }
                            }
                        }
                        agentOutput = sb.ToString();
                    }

                    if (!string.IsNullOrWhiteSpace(agentOutput)) {
                        string cleanedText = LatexResponseCleaner.CleanLatexResponse(agentOutput);
                        
                        await System.IO.File.WriteAllTextAsync(finalTexFile, cleanedText);
                        Console.WriteLine($"\n✅ [Antigravity Agent API] Agent hat Korrekturen angewendet und in `{finalFileName}` gespeichert. Starte nächsten Kompilierungs-Versuch...");
                    }
                    else {
                        Console.WriteLine("\n[FEHLER] Antigravity Agent Response enthielt kein `output_text` Feld und in den `steps` wurde kein Text gefunden.");
                        Console.WriteLine($"Raw JSON (erste 1000 Zeichen): {(responseBody.Length > 1000 ? string.Concat(responseBody.AsSpan(0, 1000), "...") : responseBody)}");
                        return false;
                    }
                }
                catch (Exception ex) {
                    Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                    Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                    return false;
                }
            }
        }
        
        Console.WriteLine($"\n  [FEHLER] Maximale Anzahl an Antigravity-Reparaturrunden ({maxRounds}) erreicht. PDF konnte nicht generiert werden.");
        return false;
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

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{spoken-clean\}")]
    private static partial System.Text.RegularExpressions.Regex SpokenCleanRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{math-stroke\}")]
    private static partial System.Text.RegularExpressions.Regex MathStrokeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{document\}|\\end\{document\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DocumentTagsRegex();
}
