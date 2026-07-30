using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] The per-segment generation half of the Vertex session, split out to mirror
/// AiStudioAutoExtractionSession.Generation.cs member-for-member - the two are twins and drift
/// between them is the single most expensive defect class in this codebase.
/// Member Index:
/// - UploadSegmentAndBuildPromptAsync: Uploads one video segment and builds its dynamic prompt.
/// - TranscribeSegmentToLatexAsync: Runs one segment through the model to LaTeX.
/// - BuildGenerationRequestAsync: Assembles the request config and history for one segment.
/// - BuildReferenceContextPreamble: The read-only warning introducing the preceding parts' LaTeX.
/// - BuildPreviousTexReferenceBlockAsync: Inlines preceding .tex parts as reference context.
/// - StreamAndCollectAsync: Streams the response, handles continuations and token accounting.
/// [Human] Der Generierungs-Teil der Vertex-Session, aufgeteilt wie beim AI-Studio-Zwilling.
/// </summary>
public partial class VertexAutoExtractionSession {
    private async Task<SegmentUpload> UploadSegmentAndBuildPromptAsync(string partFile, int partNumber, int totalParts, string originalFileName, double fullOriginalVideoDuration) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        string weekday = dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown";

        double partDurationSeconds = await FfmpegToolkit.GetVideoDurationAsync(partFile);
        TimeSpan t = TimeSpan.FromSeconds(partDurationSeconds);
        string durationString = string.Format("{0:D2} minutes and {1:D2} seconds", t.Minutes, t.Seconds);

        TimeSpan fullVideoTime = TimeSpan.FromSeconds(fullOriginalVideoDuration);
        string fullDurationString = string.Format("{0:D2} minutes and {1:D2} seconds", fullVideoTime.Minutes, fullVideoTime.Seconds);

        string dateMetadata = partNumber == 1
            ? $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({weekday}), and week number ({dateInfo.WeekInfo ?? "N/A"}) are important metadata since this is part 1 of the lecture."
            : $"The lecture took place on {dateContext} (Day of the week: {weekday}). This is not so important since this is part {partNumber} of the lecture.";

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

    private async Task<SegmentTranscript> TranscribeSegmentToLatexAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var (requestConfig, history) = await BuildGenerationRequestAsync(partFile, partNumber, parsedPrompt, attachmentParts, previousTexFiles);

        string logContext = $"[Part {partNumber}] {Path.GetFileName(originalFileName)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
        if (previousTexFiles.Count > 0) {
            logContext += $"\n[Kontext-Dateien]: {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
        }
        logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";

        return await StreamAndCollectAsync(requestConfig, history, partNumber, originalFileName, partFile, logContext);
    }

    private async Task<(GenerateContentConfig RequestConfig, List<Content> History)> BuildGenerationRequestAsync(string partFile, int partNumber, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var userPromptParts = new List<Part>();

        var preVideoBuilder = new System.Text.StringBuilder();
        if (_config.EnableImplicitPrefixCacheWarmup) {
            preVideoBuilder.Append($"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n");
        }
        var uploadedTexParts = new List<Part>();
        if (_config.DebugSendReferenceFile && previousTexFiles.Count > 0) {
            if (_config.InlinePrecedingLecTexParts) {
                Ui.Info("Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:", "Kontext");
                preVideoBuilder.Append(await BuildPreviousTexReferenceBlockAsync(partFile, previousTexFiles));
            }
            else {
                // [AI Context] Upload mode replaces the append-at-the-end branch this used to have: the
                // reference text now sits in the same pre-video Part as the anchor (as on AI Studio), and
                // only the file references move, so the request has one stable shape either way.
                // [Human] Im Upload-Modus steht der Referenztext vor dem Video, nicht mehr am Ende.
                preVideoBuilder.Append(BuildReferenceContextPreamble(partFile));
                var uploaded = await PrecedingTexReferences.UploadAsync(previousTexFiles, _attachmentHandler);
                preVideoBuilder.Append(uploaded.ReferenceText);
                uploadedTexParts.AddRange(uploaded.Parts);
            }
        }
        preVideoBuilder.Append(GetStaticPromptBeginning(partNumber));
        userPromptParts.Add(new Part { Text = preVideoBuilder.ToString() });

        userPromptParts.AddRange(uploadedTexParts);

        userPromptParts.AddRange(attachmentParts);

        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        var history = new List<Content>();
        history.AddRange(_sessionPreamble);
        history.Add(new Content { Role = "user", Parts = userPromptParts });

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        if (!string.IsNullOrEmpty(_cachedContentName)) {
            requestConfig.CachedContent = _cachedContentName;
        }
        else if (!string.IsNullOrWhiteSpace(_systemInstructionText) || (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0)) {
            var sysParts = new List<Part>();
            if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
            if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) {
                var textOnly = _historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)).ToList();
                var nonText = _historyParts.Where(p => p.FileData != null || p.InlineData != null).ToList();
                sysParts.AddRange(textOnly);
                if (nonText.Count > 0 && history.Count == 0) {
                    history.Add(new Content { Role = "user", Parts = nonText });
                }
            }
            if (sysParts.Count > 0) {
                requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
            }
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

        return (requestConfig, history);
    }

    /// <summary>
    /// [AI Context] The read-only warning that introduces the preceding parts' LaTeX, whether that
    /// LaTeX is inlined below it or attached as uploaded file references. Split out of
    /// BuildPreviousTexReferenceBlockAsync so the upload path can state the same rules without
    /// inlining anything; the wording is Vertex's own and stays byte-identical either way.
    /// [Human] Die Read-only-Warnung vor den Referenzdateien - gilt für eingebettete und hochgeladene.
    /// </summary>
    private static string BuildReferenceContextPreamble(string partFile) =>
        "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
        "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
        "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
        "CRITICAL RULES:\n" +
        "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
        $"2. Your SOLE task is to transcribe the NEW attached video segment: `{Path.GetFileName(partFile)}`.\n" +
        "3. Treat these context files as read-only and focus entirely on the new video fragment.\n\n";

    private static async Task<string> BuildPreviousTexReferenceBlockAsync(string partFile, List<string> previousTexFiles) {
        var builder = new System.Text.StringBuilder(BuildReferenceContextPreamble(partFile));
        foreach (var texFile in previousTexFiles) {
            Ui.Detail($"- {Path.GetFileName(texFile)}");
            string content = await System.IO.File.ReadAllTextAsync(texFile);
            builder.Append($"<reference_context file=\"{Path.GetFileName(texFile)}\">\n{content}\n</reference_context>\n\n");
        }
        return builder.ToString();
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
            Ui.Step($"Sende Anfrage für Part {partNumber} an Vertex AI ({_config.CurrentModel}) (Request {currentRequest}/{maxRequestsPerPart})...");
            GroundingMetadata? accumulatedGrounding = null;
            string chunkResp = "";
            int requestInputTokens = 0;
            int requestOutputTokens = 0;
            int requestCachedTokens = 0;
            bool callSuccess = false;
            var usage = new UsageReport();

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

                        usage.Absorb(chunk.UsageMetadata);
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
                        usage = new UsageReport();
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
                Ui.Detail(usage.Describe($"Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {freshReqTokens:N0} | Output: {requestOutputTokens:N0}", "[Request Tokens]      "));
                Ui.Detail($"[Part Total Tokens]    Total Prompt: {interactionInputTokens:N0} | Gecacht: {interactionCachedTokens:N0} | Frisch: {freshPartTokens:N0} | Output: {interactionOutputTokens:N0}");
                Ui.Detail($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {freshSessTokens:N0} | Output: {_sessionTotalOutputTokens:N0}");
            } else {
                Ui.Detail(usage.Describe($"Request: {requestInputTokens:N0} in ({requestCachedTokens:N0} gecacht) / {requestOutputTokens:N0} out | Session: {_sessionTotalInputTokens:N0} in / {_sessionTotalOutputTokens:N0} out"));
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

            Ui.Detail("Warte 150 Sekunden vor der Fortsetzung...", "Timer");
            if (!await InteractiveDelay.SmartDelayAsync(150, "Warte auf Fortsetzung (Sicherheits-Puffer)...")) {
                Ui.Info("Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= cancelHandler;
        return new SegmentTranscript(fullResponse, new TokenUsage(interactionInputTokens, interactionOutputTokens, interactionCachedTokens));
    }
}
