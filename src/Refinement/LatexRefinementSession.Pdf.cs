using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Latex;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] The PDF half of the refinement session: compiling the merged LaTeX with local
/// pdflatex, formatting its log, cleaning up the wrapper/helper files it leaves behind, and the two
/// repair paths taken when compilation fails - one that asks the model to fix the LaTeX, one that
/// delegates to an external Antigravity agent.
///
/// <para>Split out of LatexRefinementSession (Phase 11), which was 1461 lines. This is a coherent,
/// self-contained concern: it runs after the AI refinement steps are finished and touches none of
/// their logic, so work on the refinement steps never needs to read it and vice versa. Kept as a
/// `partial` rather than a standalone type because these methods read the session's configuration
/// and client fields directly; turning that into constructor injection is a separate design
/// question from getting the file down to a readable size.</para>
/// [Human] Die PDF-Hälfte der Refinement-Session: Kompilierung, Log-Aufbereitung, Aufräumen und die
/// beiden Reparaturwege bei Fehlern. Aus der 1461-Zeilen-Datei herausgelöst.
/// </summary>
public partial class LatexRefinementSession {

    /// <summary>
    /// [AI Context] Compiles the final merged LaTeX file into a PDF using local pdflatex. Uses a wrapper file to inject the preamble.
    /// [Human] Baut das fertige LaTeX-Skript mithilfe einer Preamble (Design-Vorlage) zu einem PDF zusammen.
    /// </summary>
    private async Task<bool> CompilePdfAsync(string finalTexFile, string baseName, string targetFolder, string stepPrefix = "step4", bool allowRetryOnFailure = true) {
        if (!System.IO.File.Exists(finalTexFile)) {
            Ui.Error($"Kann PDF nicht generieren: {finalTexFile} existiert nicht.", "LaTeX Refinement");
            return false;
        }

        string preamblePath = _config.PdfCompilation?.PreamblePath ?? "pdf-preamble.tex";
        if (!System.IO.File.Exists(preamblePath)) {
            Ui.Warn($"Preamble-Datei ({preamblePath}) nicht gefunden. Überspringe PDF-Generierung.", "LaTeX Refinement");
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
                    Ui.Info($"Verbleibende \\begin{{document}} / \\end{{document}} Tags aus {Path.GetFileName(finalTexFile)} entfernt.");
                }
            }

            string preambleText = await System.IO.File.ReadAllTextAsync(preamblePath);
            string finalFileName = Path.GetFileName(finalTexFile);

            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperFileName = $"{inputBaseName}-main.tex";
            string wrapperPath = Path.Combine(targetFolder, wrapperFileName);

            string wrapperContent = preambleText + "\n\\begin{document}\n\n" +
                                    $"\\input{{{finalFileName}}}\n\n" +
                                    "\\end{document}\n";

            await System.IO.File.WriteAllTextAsync(wrapperPath, wrapperContent);
            Ui.Info($"Wrapper-Datei erstellt: {wrapperPath}");

            var (success, log) = await LatexToolkit.CompilePdfAsync(wrapperPath);

            string logContent = FormatLatexLog(log, success);
            string logPath = Path.Combine(targetFolder, $"{stepPrefix}-compile-log.txt");
            await System.IO.File.WriteAllTextAsync(logPath, logContent);

            if (success) {
                Ui.Success($"PDF erfolgreich erstellt im Zielordner: {targetFolder}");

                string compiledPdfPath = wrapperPath.Replace(".tex", ".pdf");
                if (System.IO.File.Exists(compiledPdfPath)) {
                    string cleanPdfPath = Path.Combine(targetFolder, inputBaseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, cleanPdfPath, true);
                    Ui.Info($"PDF kopiert zu: {Path.GetFileName(cleanPdfPath)}");

                    if (stepPrefix == "step4") {
                        string finalCleanPdfPath = Path.Combine(targetFolder, baseName + ".pdf");
                        System.IO.File.Copy(compiledPdfPath, finalCleanPdfPath, true);
                        Ui.Info($"Finales PDF kopiert zu: {Path.GetFileName(finalCleanPdfPath)}");
                    }
                }

                CleanupHelperFiles(targetFolder, finalTexFile, true);

                if (logContent.Contains("⚠️ WARNING:")) {
                    Ui.Warn($"Es gab LaTeX-Warnungen während der Kompilation. Details in: {stepPrefix}-compile-log.txt");
                }
                return true;
            }
            else {
                Ui.Error($"Fehler bei der PDF-Generierung. Protokoll gespeichert in: {logPath}");
                CleanupHelperFiles(targetFolder, finalTexFile, false);
                if (allowRetryOnFailure) {
                    if (_config.PdfCompilation?.UseAntiGravityAgent == true) {
                        Ui.Warn("PDF-Kompilierung fehlgeschlagen. Starte sofort interaktive Reparatur über AntiGravity...", "AntiGravity Agent Mode");
                        return await RunExternalAgentRepairLoopAsync(finalTexFile, baseName, targetFolder, preambleText);
                    }

                    int maxRounds = _config.PdfCompilation?.MaxFixRounds ?? 3;
                    if (maxRounds <= 0) maxRounds = 1;

                    string currentBodyTex = await System.IO.File.ReadAllTextAsync(finalTexFile);
                    string currentLog = logContent;
                    bool anyRoundSucceeded = false;

                    for (int round = 1; round <= maxRounds; round++) {
                        Ui.Step($"Starte Reparatur-Runde {round} von {maxRounds}...", "AI PDF Fix Loop");

                        bool roundSuccess = await TryRepairFailedPdfBuildAsync(preambleText, currentBodyTex, currentLog, baseName, targetFolder, round);

                        for (int prev = 1; prev < round; prev++) {
                            string prevNoPreamble = Path.Combine(targetFolder, $"step5-{baseName}-offset-last_try{prev}.tex");
                            string prevStandalone = Path.Combine(targetFolder, $"step5-{baseName}-offset-last_try{prev}-main.tex");
                            string prevLog = Path.Combine(targetFolder, $"compile-log-step5-last_try{prev}.txt");
                            try { if (System.IO.File.Exists(prevNoPreamble)) System.IO.File.Delete(prevNoPreamble); } catch (Exception ex) { Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}"); }
                            try { if (System.IO.File.Exists(prevStandalone)) System.IO.File.Delete(prevStandalone); } catch (Exception ex) { Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}"); }
                            try { if (System.IO.File.Exists(prevLog)) System.IO.File.Delete(prevLog); } catch (Exception ex) { Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}"); }
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
            Ui.Error($"Unerwarteter Fehler bei der PDF-Generierung: {ex.GetType().Name} - {ex.Message}", "LaTeX Refinement");
            return false;
        }
    }

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
            Ui.Warn($"Konnte temporäre Precheck-Dateien nicht vollständig bereinigen: {ex.GetType().Name} - {ex.Message}", "LaTeX Refinement");
        }
    }

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
            Ui.Warn($"Hilfsdateien für {Path.GetFileName(finalTexFile)} konnten nicht vollständig bereinigt werden: {ex.GetType().Name} - {ex.Message}");
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

    private async Task<bool> TryRepairFailedPdfBuildAsync(string preambleText, string failedBodyTex, string compileLog, string baseName, string targetFolder, int roundNumber = 1) {
        Ui.Step($"Schritt 4 Retry: PDF LaTeX Fix (-final-attempt, Runde #{roundNumber})", "LaTeX Fix");
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

        string fullResponseText = await StreamFixResponseAsync(history, requestConfig, backendParams, standaloneFileName);

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
            Ui.Info($"Gefixte LaTeX-Datei (Versuch #{roundNumber}) gespeichert unter: {noPreamblePath}");

            string standaloneContent = preambleText + "\n\\begin{document}\n\n" + bodyOnlyText + "\n\n\\end{document}\n";
            string standalonePath = Path.Combine(targetFolder, standaloneFileName);
            await System.IO.File.WriteAllTextAsync(standalonePath, standaloneContent);
            Ui.Info($"Gefixte LaTeX-Datei (Versuch #{roundNumber}, mit Preamble) gespeichert unter: {standalonePath}");

            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            Ui.Info($"Starte PDF-Kompilierung für step5 (Fix-Versuch #{roundNumber})...");
            var (retrySuccess, retryLog) = await LatexToolkit.CompilePdfAsync(standalonePath);
            string retryLogContent = FormatLatexLog(retryLog, retrySuccess);
            string retryLogPath = Path.Combine(targetFolder, $"compile-log-step5-last_try{roundNumber}.txt");
            await System.IO.File.WriteAllTextAsync(retryLogPath, retryLogContent);

            if (retrySuccess) {
                Ui.Success($"PDF erfolgreich im Fix-Versuch #{roundNumber} (step5) erstellt: {targetFolder}");
                string compiledPdfPath = Path.Combine(targetFolder, standaloneFileName.Replace(".tex", ".pdf"));
                if (System.IO.File.Exists(compiledPdfPath)) {
                    string cleanPdfPath = Path.Combine(targetFolder, noPreambleFileName.Replace(".tex", ".pdf"));
                    System.IO.File.Copy(compiledPdfPath, cleanPdfPath, true);
                    Ui.Info($"PDF kopiert zu: {Path.GetFileName(cleanPdfPath)}");

                    string finalCleanPdfPath = Path.Combine(targetFolder, baseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, finalCleanPdfPath, true);
                    Ui.Info($"Finales PDF kopiert zu: {Path.GetFileName(finalCleanPdfPath)}");
                }
                CleanupHelperFiles(targetFolder, Path.Combine(targetFolder, noPreambleFileName), true);
                return true;
            }
            else {
                Ui.Error($"Auch Fix-Versuch #{roundNumber} konnte das PDF nicht fehlerfrei kompilieren. Log in: {retryLogPath}");
                CleanupHelperFiles(targetFolder, Path.Combine(targetFolder, noPreambleFileName), false);
                return false;
            }
        }
        return false;
    }

    private async Task<string> StreamFixResponseAsync(List<Content> history, GenerateContentConfig requestConfig, BackendParameters backendParams, string outputFileName) {
        string fullResponseText = "";
        int currentRequest = 1;
        int maxRequests = 5;
        int emptyResponseRetries = 0;

        using var cts = new CancellationTokenSource();
        void CancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch (Exception ex) { Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}"); } }
        Console.CancelKeyPress += CancelHandler;

        while (true) {
            string providerName = _config.UseVertex ? "Vertex AI" : "Google AI Studio";

            int fixDelay = _config.Step3LastRefinement?.RateLimitDelaySeconds > 0 ? _config.Step3LastRefinement.RateLimitDelaySeconds : 130;
            double secondsSinceLastGen = (DateTime.UtcNow - InteractiveDelay.LastGenerationCompletionTimeUtc).TotalSeconds;
            if (secondsSinceLastGen < fixDelay && !InteractiveDelay.IsInSmartDelay) {
                int waitRemaining = (int)Math.Ceiling(fixDelay - secondsSinceLastGen);
                Ui.Detail($"Warte verbleibende {waitRemaining} Sekunden vor dem PDF-Fix-Request...", "Rate-Limit & Quota");
                if (!await InteractiveDelay.SmartDelayAsync(waitRemaining, "Warte auf Rate-Limits (Token-Refill Schutz vor PDF-Fix-Request)...")) {
                    break;
                }
            }
            AttachmentUploader.HasJustUploaded = false;

            Ui.Info($"Sende PDF-Fix-Anfrage an {providerName} ({backendParams.CurrentModel}) (Request {currentRequest}/{maxRequests})...", "API");

            string chunkResp = "";
            bool callSuccess = false;

            try {
                callSuccess = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                  streamFactory: () => _client.Models.GenerateContentStreamAsync(backendParams.CurrentModel, history, requestConfig),
                  onChunkReceived: async (chunk) => {
                      string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                      Ui.Raw(text);
                      chunkResp += text;
                      await Task.CompletedTask;
                  },
                  cancellationToken: cts.Token,
                  retryContext: outputFileName,
                  onRetry: () => { chunkResp = ""; }
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
            if (isComplete) break;

            if (currentRequest >= maxRequests) {
                Ui.Warn($"Maximale Anzahl an Requests ({maxRequests}) für PDF-Fix erreicht. Breche ab.");
                break;
            }

            string continuePrompt = $"[IMPORTANT] Your response was cut short due to token limits. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Just print the very next character.";

            Ui.Info("Unerwartetes Ende der Antwort. Bereite Continue-Prompt vor...", "PDF-Fix");
            history.Add(new Content { Role = "model", Parts = [new() { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new() { Text = continuePrompt }] });

            Ui.Detail($"Warte {fixDelay} Sekunden vor der Fortsetzung...", "Timer");
            if (!await InteractiveDelay.SmartDelayAsync(fixDelay, "Warte auf Rate-Limits (Token Refill)...")) {
                break;
            }
            currentRequest++;
        }

        Console.CancelKeyPress -= CancelHandler;
        return fullResponseText;
    }

    private async Task<bool> RunExternalAgentRepairLoopAsync(string finalTexFile, string baseName, string targetFolder, string preambleText) {
        int maxRounds = _config.PdfCompilation?.MaxFixRounds ?? 3;
        if (maxRounds <= 0) maxRounds = 1;

        string? envVarName = (_config.AiStudioApiKeyEnvNames != null && _config.AiStudioApiKeyEnvNames.Length > _config.AiStudioActiveApiProfile)
            ? _config.AiStudioApiKeyEnvNames[_config.AiStudioActiveApiProfile]
            : "API_KEY";

        string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envVarName);
        if (string.IsNullOrEmpty(apiKey)) {
            Ui.Error($"Antigravity Agent benötigt einen gültigen API-Key in der Umgebungsvariable '{envVarName}'.");
            return false;
        }

        using var httpClient = new System.Net.Http.HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(20);
        httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        for (int round = 1; round <= maxRounds; round++) {
            Ui.Step($"Starte Reparatur-Runde {round} von {maxRounds}...", "Antigravity Agent API");

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
                Ui.Success($"PDF durch Antigravity Agent erfolgreich (Runde {round}/{maxRounds}) generiert!", "LatexToolkit");
                string compiledPdfPath = wrapperPath.Replace(".tex", ".pdf");
                if (System.IO.File.Exists(compiledPdfPath)) {
                    string cleanPdfPath = Path.Combine(targetFolder, inputBaseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, cleanPdfPath, true);
                    Ui.Info($"PDF kopiert zu: {Path.GetFileName(cleanPdfPath)}");

                    string finalCleanPdfPath = Path.Combine(targetFolder, baseName + ".pdf");
                    System.IO.File.Copy(compiledPdfPath, finalCleanPdfPath, true);
                    Ui.Info($"Finales PDF kopiert zu: {Path.GetFileName(finalCleanPdfPath)}");
                }
                CleanupHelperFiles(targetFolder, finalTexFile, true);
                return true;
            }
            else {
                CleanupHelperFiles(targetFolder, finalTexFile, false);
                Ui.Warn($"PDF-Generierung fehlgeschlagen (Runde {round} von {maxRounds}). Sende Log und Code an Remote-Agenten...", "Antigravity Agent API");

                if (!await CallAntiGravityAgentAsync(httpClient, finalTexFile, logContent)) {
                    return false;
                }
            }
        }

        Ui.Error($"Maximale Anzahl an Antigravity-Reparaturrunden ({maxRounds}) erreicht. PDF konnte nicht generiert werden.");
        return false;
    }

    private static async Task<bool> CallAntiGravityAgentAsync(System.Net.Http.HttpClient httpClient, string finalTexFile, string logContent) {
        string finalFileName = Path.GetFileName(finalTexFile);
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

        Ui.Info("Kontaktiere Google Cloud (v1beta/interactions) für automatische Korrektur...", "Antigravity Agent API");

        try {
            var response = await httpClient.PostAsync("https://generativelanguage.googleapis.com/v1beta/interactions", content);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) {
                Ui.Error($"Antigravity Agent API Aufruf fehlgeschlagen: {response.StatusCode} - {responseBody}");
                return false;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(responseBody);
            string agentOutput = "";
            if (doc.RootElement.TryGetProperty("output_text", out var outputTextElement) && outputTextElement.ValueKind == System.Text.Json.JsonValueKind.String) {
                agentOutput = outputTextElement.GetString() ?? "";
            }

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
                Ui.Success($"Agent hat Korrekturen angewendet und in `{finalFileName}` gespeichert. Starte nächsten Kompilierungs-Versuch...", "Antigravity Agent API");
                return true;
            }
            else {
                Ui.Error("Antigravity Agent Response enthielt kein `output_text` Feld und in den `steps` wurde kein Text gefunden.");
                return false;
            }
        }
        catch (Exception ex) {
            Ui.Error($"Exception bei Antigravity Agent API: {ex.GetType().Name} - {ex.Message}");
            return false;
        }
    }


}
