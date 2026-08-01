using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] The model-interaction half of the refinement session: resolving each step's system
/// instruction from disk, creating and reusing the Vertex context cache, assembling the request
/// config, streaming the response back, dumping the prompt log, and computing the expected
/// structural counts used to sanity-check the result.
///
/// <para>Split out of LatexRefinementSession (Phase 11), leaving that file with the pipeline and
/// the three refinement steps. The boundary is "what one step does when it talks to Gemini" versus
/// "which steps run, in what order, over which files" - the former changes when the API or caching
/// strategy changes, the latter when the pipeline shape does.</para>
/// [Human] Die Modell-Hälfte der Refinement-Session: System Instruction laden, Context-Cache,
/// Request-Konfiguration, Streaming und Prompt-Log. Aus der Hauptdatei herausgelöst.
/// </summary>
public partial class LatexRefinementSession {

    private static async Task<string> ResolveSystemInstructionTextAsync(RefinementStepConfig stepConfig) {
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

    private static (int ExpectedSpokenClean, int ExpectedMathStroke) ComputeExpectedStructuralCounts(List<Content> history) {
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

}
