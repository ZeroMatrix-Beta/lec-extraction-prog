using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Latex;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Everything the AI Studio session does for a single video segment once FFmpeg has
/// produced it: uploading the segment and building its prompt, assembling the generation request
/// (system instruction, history, reference context, thinking config), the optional per-part token
/// diagnostic, and streaming the response back while accumulating usage.
///
/// <para>Split out of AiStudioAutoExtractionSession (Phase 11), mirroring the same cut made in
/// LatexRefinementSession. The boundary is per-segment model interaction versus batch
/// orchestration - the pipeline half decides which videos and parts get processed and in what
/// order, this half decides what one request looks like.</para>
/// [Human] Alles, was für einen einzelnen Video-Teil passiert: Upload, Prompt- und Request-Aufbau,
/// Token-Diagnose und Streaming. Vom Batch-Ablauf getrennt.
/// </summary>
public partial class AiStudioAutoExtractionSession {

    private async Task<SegmentUpload> UploadSegmentAndBuildPromptAsync(string partFile, int partNumber, int totalParts, string originalFileName, double fullOriginalVideoDuration) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        double partDurationSeconds = await FfmpegToolkit.GetVideoDurationAsync(partFile);
        TimeSpan partDuration = TimeSpan.FromSeconds(partDurationSeconds);
        string durationString = string.Format("{0:D2} minutes and {1:D2} seconds", partDuration.Minutes, partDuration.Seconds);

        TimeSpan fullVideoTime = TimeSpan.FromSeconds(fullOriginalVideoDuration);
        string fullDurationString = string.Format("{0:D2} minutes and {1:D2} seconds", fullVideoTime.Minutes, fullVideoTime.Seconds);

        string weekday = dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown";
        string weekInfo = dateInfo.WeekInfo ?? "N/A";
        string dateMetadata = partNumber == 1
            ? $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({weekday}), and week number ({weekInfo}) are important metadata since this is part 1 of the lecture."
            : $"The lecture took place on {dateContext} (Day of the week: {weekday}).";

        string prompt =
            $"<parameter name=\"lecture_metadata\">{dateMetadata}</parameter>\n" +
            $"<parameter name=\"source_video\">You must transcribe the video attachment named `{Path.GetFileName(partFile)}` verbatim according to the system instructions. Ensure you transcribe every single spoken word up to the very last second of the video, even if it cuts off mid-sentence.</parameter>\n" +
            $"<parameter name=\"segment_info\">You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long. The duration of the entire lecture video is {fullDurationString}.</parameter>\n" +
            $"<parameter name=\"duration_and_timestamps\">Do NOT calculate any time offset for the 'spoken-clean' environment. Start at 00:00:00 and ensure the final timestamp in your very last 'spoken-clean' block perfectly matches the segment length ({durationString}).</parameter>\n" +
            "</context_and_parameters>";

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
        if (!uploadSuccess || attachmentParts.Count == 0) return new SegmentUpload(false, null, []);

        return new SegmentUpload(true, parsedPrompt, attachmentParts);
    }

    public async Task<SegmentTranscript> TranscribeSegmentToLatexAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var (requestConfig, history) = await BuildGenerationRequestAsync(partNumber, parsedPrompt, attachmentParts, previousTexFiles);

        await LogTokenCountsAsync(attachmentParts, history, previousTexFiles);

        string logContext = $"[Part {partNumber}] {Path.GetFileName(originalFileName)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
        if (previousTexFiles.Count > 0) {
            logContext += $"\n[Kontext-Dateien]: {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
        }
        logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";

        return await StreamAndCollectAsync(requestConfig, history, partNumber, originalFileName, partFile, logContext);
    }

    private async Task<(GenerateContentConfig RequestConfig, List<Content> History)> BuildGenerationRequestAsync(int partNumber, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        if (ModelCapabilities.SupportsThinking(_config.CurrentModel)) {
            bool isGemini25 = _config.CurrentModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
            }
            else if (_config.ThinkingBudget.HasValue) {
                int budget = _config.ThinkingBudget.Value;
                if (budget > 32768) budget = 32768;
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = budget };
            }
        }

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        var userPromptParts = new List<Part>();

        string staticBeginning = GetStaticPromptBeginning(partNumber);
        if (_config.DebugSendReferenceFile) {
            string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n";

            var referenceContextBuilder = new System.Text.StringBuilder(ReferenceContextPreamble);
            referenceContextBuilder.Append(dummyReferenceBlock);

            if (previousTexFiles.Count > 0) {
                Ui.Info("Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:", "Kontext");
                foreach (var previousTexFile in previousTexFiles) {
                    string previousTexFileName = Path.GetFileName(previousTexFile);
                    Ui.Detail($"- {previousTexFileName}");
                    string previousTexContent = await System.IO.File.ReadAllTextAsync(previousTexFile);
                    referenceContextBuilder.Append($"<reference_context file=\"{previousTexFileName}\">\n{previousTexContent}\n</reference_context>\n\n");
                }
            }

            userPromptParts.Add(new Part { Text = referenceContextBuilder.ToString() + staticBeginning });
        } else {
            userPromptParts.Add(new Part { Text = staticBeginning });
        }

        userPromptParts.AddRange(attachmentParts);

        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        var history = new List<Content>();
        history.AddRange(_sessionPreamble);
        history.Add(new Content { Role = "user", Parts = userPromptParts });

        return (requestConfig, history);
    }

    private async Task LogTokenCountsAsync(List<Part> attachmentParts, List<Content> history, List<string> previousTexFiles) {
        if (!_config.VerboseConsoleOutput) return;
        try {
            Ui.Detail("Berechne Token-Anzahl für die einzelnen Bestandteile...", "Token-Analyse");
            var videoContents = new List<Content> { new() { Role = "user", Parts = attachmentParts } };
            var videoCount = await _client.Models.CountTokensAsync(_config.CurrentModel, videoContents);
            Ui.Detail($"- Video-Token: {videoCount.TotalTokens}");

            var userPromptParts = history[^1].Parts;
            if (_config.DebugSendReferenceFile && userPromptParts != null && userPromptParts.Count > 0 && !string.IsNullOrEmpty(userPromptParts[0].Text)) {
                var texContents = new List<Content> { new() { Role = "user", Parts = [userPromptParts[0]] } };
                var texCount = await _client.Models.CountTokensAsync(_config.CurrentModel, texContents);
                string fileInfo = previousTexFiles.Count > 0
                    ? $"dummy-part0.tex + {previousTexFiles.Count} Datei(en): {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}"
                    : "dummy-part0.tex";
                Ui.Detail($"- Inlined Kontext ({fileInfo}) Token: {texCount.TotalTokens}");
            }

            var totalCount = await _client.Models.CountTokensAsync(_config.CurrentModel, history);
            Ui.Detail($"-> Gesamt-Token in History (Video + Kontext + Prompt): {totalCount.TotalTokens}");
        }
        catch (Exception ex) {
            Ui.Warn($"Fehler beim Zählen der Token: {ex.Message}", "Token-Analyse");
        }
    }

    private async Task<SegmentTranscript> StreamAndCollectAsync(GenerateContentConfig requestConfig, List<Content> history, int partNumber, string originalFileName, string partFile, string logContext) {
        string fullResponse = "";
        int currentRequest = 1;
        int maxRequestsPerPart = 6;
        int interactionInputTokens = 0;
        int interactionOutputTokens = 0;
        int interactionCachedTokens = 0;
        string currentLogPrompt = logContext;

        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
        Console.CancelKeyPress += cancelHandler;

        while (true) {
            Ui.Step($"Sende Anfrage für Part {partNumber} an Google AI Studio ({_config.CurrentModel}) (Request {currentRequest}/{maxRequestsPerPart})...");
            GroundingMetadata? accumulatedGrounding = null;
            string chunkResp = "";
            int requestInputTokens = 0;
            int requestOutputTokens = 0;
            int requestCachedTokens = 0;
            bool callSuccess = false;

            try {
                callSuccess = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                    streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, history, requestConfig),
                    onChunkReceived: async (chunk) => {
                        string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                        Ui.Raw(txt);
                        chunkResp += txt;

                        var metadata = chunk.Candidates?[0]?.GroundingMetadata;
                        if (metadata != null) {
                            accumulatedGrounding = metadata;
                        }

                        if (chunk.UsageMetadata != null) {
                            if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                            if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                            if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) requestCachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                        }
                        await Task.CompletedTask;
                    },
                    cancellationToken: cts.Token,
                    retryContext: $"Teil {partNumber} von {Path.GetFileName(originalFileName)}",
                    onRetry: () => {
                        chunkResp = "";
                        accumulatedGrounding = null;
                        requestInputTokens = 0;
                        requestOutputTokens = 0;
                        requestCachedTokens = 0;
                    }
                );
            }
            catch (Exception ex) {
                Ui.Error($"Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Fahre mit nächstem Teil fort. Finaler Fehler: {ex.Message}", "Abbruch");
                break;
            }

            if (accumulatedGrounding != null) {
                Ui.Info("Quellen (Google Search Grounding):");
                if (accumulatedGrounding.WebSearchQueries != null && accumulatedGrounding.WebSearchQueries.Count > 0) {
                    Ui.Detail($"Suchanfragen: {string.Join(", ", accumulatedGrounding.WebSearchQueries.Select(q => $"\"{q}\""))}");
                }
                if (accumulatedGrounding.GroundingChunks != null) {
                    int refIdx = 1;
                    foreach (var chunkRef in accumulatedGrounding.GroundingChunks) {
                        if (chunkRef.Web != null) {
                            Ui.Detail($"[{refIdx}] {chunkRef.Web.Title} - {chunkRef.Web.Uri}");
                            refIdx++;
                        }
                    }
                }
            }

            if (!callSuccess) {
                Ui.Info("Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
                break;
            }

            interactionInputTokens += requestInputTokens;
            interactionOutputTokens += requestOutputTokens;
            interactionCachedTokens += requestCachedTokens;
            _sessionTotalInputTokens += requestInputTokens;
            _sessionTotalOutputTokens += requestOutputTokens;
            _sessionTotalCachedTokens += requestCachedTokens;

            int freshReqTokens = Math.Max(0, requestInputTokens - requestCachedTokens);
            int freshPartTokens = Math.Max(0, interactionInputTokens - interactionCachedTokens);
            int freshSessTokens = Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens);

            if (_config.VerboseConsoleOutput) {
                Ui.Detail($"[Request Tokens]       Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {freshReqTokens:N0} | Output: {requestOutputTokens:N0}");
                Ui.Detail($"[Part Total Tokens]    Total Prompt: {interactionInputTokens:N0} | Gecacht: {interactionCachedTokens:N0} | Frisch: {freshPartTokens:N0} | Output: {interactionOutputTokens:N0}");
                Ui.Detail($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {freshSessTokens:N0} | Output: {_sessionTotalOutputTokens:N0}");
            } else {
                Ui.Detail($"[Tokens] Request: {requestInputTokens:N0} in ({requestCachedTokens:N0} gecacht) / {requestOutputTokens:N0} out | Session: {_sessionTotalInputTokens:N0} in / {_sessionTotalOutputTokens:N0} out");
            }

            fullResponse += chunkResp;
            await _sessionLogger.LogChatAsync(currentLogPrompt, currentLogPrompt, _config.CurrentModel, chunkResp, "AutoExtraction", requestInputTokens, requestOutputTokens, requestCachedTokens);

            bool segmentComplete = SegmentCompleteRegex().IsMatch(chunkResp);
            bool videoComplete = VideoCompleteRegex().IsMatch(chunkResp);

            if (videoComplete) break;

            if (currentRequest >= maxRequestsPerPart) {
                Ui.Warn($"Maximale Anzahl an Requests ({maxRequestsPerPart}) für diesen Teil erreicht ({partFile}). Breche ab.");
                break;
            }

            string continuePrompt = segmentComplete ? "Continue" :
                $"[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Do not open a new ```latex block if you were already inside one, just continue the text directly.";

            if (segmentComplete) Ui.Info("Segment-Limit erreicht. Sende 'Continue'...", "AutoExtraction");
            else Ui.Info("Unerwartetes Ende der Antwort. Bereite automatisierten 'Continue'-Prompt vor...", "AutoExtraction");

            Ui.Detail($"[Sende folgenden Continue-Prompt:]\n{continuePrompt}");

            history.Add(new Content { Role = "model", Parts = [new() { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new() { Text = continuePrompt }] });
            currentLogPrompt = $"[Continue Prompt für Part {partNumber}]:\n{continuePrompt}";

            int delay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130;
            Ui.Detail($"Warte {delay} Sekunden vor der Fortsetzung, um API-Limits zu schonen...", "Timer");
            if (!await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Rate-Limits (Token Refill)...")) {
                Ui.Info("Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= cancelHandler;
        AttachmentUploader.HasJustUploaded = false;
        return new SegmentTranscript(fullResponse, new TokenUsage(interactionInputTokens, interactionOutputTokens, interactionCachedTokens));
    }

}
