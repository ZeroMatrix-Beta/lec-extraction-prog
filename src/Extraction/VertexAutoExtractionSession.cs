using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Globalization;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;
using LectureExtraction.Refinement;
using Spectre.Console;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline for Vertex AI.
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// Split into partial classes:
/// - VertexAutoExtractionSession.cs (core pipeline, file batching, YouTube transcription)
/// - VertexAutoExtractionSession.PrefixCache.cs (implicit prefix cache warming & history loading)
/// Member Index:
/// - StartAsync: Validates folders, prompts mode selection (batch, single, youtube), and begins execution.
/// - SetupContextAndProcessAsync: Ensures system instructions/preamble are loaded then processes files.
/// - ProcessPreparedVideoAsync: Producer/consumer loop for MP4 video segment extraction.
/// - ProcessYouTubeTasksAsync: YouTube video download and transcription pipeline.
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos.
/// </summary>
public partial class VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentUploader attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) {
    public static readonly string[] AvailableModels = [
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview"
    ];

    private readonly Client _client = client;
    private readonly VertexAutoExtractionConfig _config = config;
    private readonly AttachmentUploader _attachmentHandler = attachmentHandler;
    private readonly SessionLogger _sessionLogger = sessionLogger;
    private readonly LatexRefinementSessionConfig _latexRefinementConfig = latexRefinementConfig;
    private string _systemInstructionText = "";
    // [AI Context] Cached payloads to avoid redundant uploads and API calls across multiple video chunks.
    private readonly List<Part> _historyParts = [];
    // [AI Context] Stores the acknowledged history prompt and the model's confirmation, statically prepended to all subsequent API calls.
    private readonly List<Content> _sessionPreamble = [];
    private bool _historyWasLoaded = false;
    private int _sessionTotalInputTokens = 0;
    private int _sessionTotalOutputTokens = 0;
    private int _sessionTotalCachedTokens = 0;
    // [AI Context] Active Google Cloud Context Cache resource name if caching is active.
    private string? _cachedContentName = null;

    /// <summary>
    /// [AI Context] Entry point that validates the source/target directories and checks filename formats.
    /// [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
    /// </summary>
    public async Task StartAsync() {
        if (!Directory.Exists(_config.SourceFolder)) {
            Ui.Error($"Quellordner nicht gefunden: {_config.SourceFolder}");
            return;
        }

        // If no specific target folder is provided in config, create one inside the source folder.
        if (string.IsNullOrWhiteSpace(_config.TargetFolder)) {
            _config.TargetFolder = Path.Combine(_config.SourceFolder, "extracted_output");
        }

        if (!Directory.Exists(_config.TargetFolder)) {
            Directory.CreateDirectory(_config.TargetFolder);
        }

        Ui.Step("Automatisierte Extraktion (Vertex AI)");
        Ui.Detail($"Quelle (Source): {_config.SourceFolder}");
        Ui.Detail($"Ziel (Target):   {_config.TargetFolder}");
        if (!string.IsNullOrWhiteSpace(_config.ProjectId)) {
            Ui.Detail($"API-Projekt:     {_config.ProjectId} ({_config.Location})");
        }

        string[] videoFilesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
        foreach (var videoFile in videoFilesToProcess) {
            var dateInfo = VideoDateParser.Parse(videoFile);
            if (!dateInfo.IsValid) {
                Ui.Warn($"Video entspricht nicht dem Datums-/Wochen-Namensschema: {Path.GetFileName(videoFile)}", "AutoExtraction");
                Ui.Detail("Erwartetes Format z.B.: 02-16-2026-monday-week1-Analysis_II.mp4 oder week1-02-16-2026-montag.mp4");
            }
        }

        // Same loop as the AI Studio twin: backing out of a branch returns to this menu, and only
        // "Zurück" leaves the session.
        while (true) {
            var choice = Ui.Select("Modus auswählen:", [
                ("1) 🚀 Alle Videos im Quellordner konvertieren (Standard)", ExtractionMode.AllVideos),
                ("2) 🎬 Einzelnes Video auswählen und konvertieren", ExtractionMode.SingleVideo),
                ("3) 📺 YouTube-Video transkribieren", ExtractionMode.YouTube)
            ], backLabel: "4) 🚪 Abbrechen / Zurück");

            if (!choice.IsValue) return;

            switch (choice.Value) {
                case ExtractionMode.SingleVideo: {
                    var files = FileSelectionPrompt.SelectSingleFile(_config.SourceFolder);
                    if (files.Length > 0) {
                        await SetupContextAndProcessAsync(files);
                        return;
                    }
                    break;
                }

                case ExtractionMode.YouTube:
                    await ProcessYouTubeTasksAsync();
                    return;

                default: {
                    var files = VideoBatchSelector.SelectAndFilterVideosForBatch(_config.SourceFolder);
                    if (files.Length > 0) {
                        await SetupContextAndProcessAsync(files);
                        return;
                    }
                    break;
                }
            }
        }
    }

    private enum ExtractionMode { AllVideos, SingleVideo, YouTube }

    /// <summary>
    /// [AI Context] Core initialization routine before batch processing. Loads system instructions and pre-warms the model context with attachments.
    /// [Human] Lädt die System-Instruktionen und die Historie hoch, bevor die eigentliche Video-Verarbeitung startet.
    /// </summary>
    private async Task SetupContextAndProcessAsync(string[] files) {
        if (files == null || files.Length == 0) {
            Ui.Warn("Keine Dateien ausgewählt.");
            return;
        }

        // [AI Context] Clean up the bucket at the very beginning to remove any leftovers from previous crashes.
        await CleanupBucketAsync();

        try {
            if (!await EnsureSessionSetupAsync()) return;
            await ProcessFilesAsync(files);
        }
        finally {
            // [AI Context] Guarantee that the bucket is cleaned up even if an exception occurs during history upload or processing.
            await CleanupBucketAsync();
        }
    }

    private async Task<bool> EnsureSessionSetupAsync() {
        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (_config.SystemInstructionPaths != null && _config.SystemInstructionPaths.Length != 0) {
                Ui.Detail("Folgende System Instruction-Dateien sind konfiguriert:");

                // Resolve all files from configured paths, handling directories
                var resolvedInstructionFiles = HistoryFileResolver.ResolveHistoryFiles(_config.SystemInstructionPaths);

                if (resolvedInstructionFiles.Count > 0) {
                    FileTreeRenderer.PrintFileTree(resolvedInstructionFiles, _config.VerboseConsoleOutput);
                    List<string> distinctHistoryFiles = [];
                    if (_config.LoadHistoryIntoSystemInstruction && !_historyWasLoaded) {
                        distinctHistoryFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
                        if (distinctHistoryFiles.Count > 0) {
                            Ui.Detail("Folgende Dateien sind als History konfiguriert (werden aber direkt in die System Instruction geladen):");
                            FileTreeRenderer.PrintFileTree(distinctHistoryFiles, _config.VerboseConsoleOutput);
                        }
                    }

                    string promptText = _config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0
                        ? "System Instructions und History laden?"
                        : "System Instructions laden?";

                    if (Ui.Confirm(promptText, true)) {
                        var allPathsForIndex = new List<string>(resolvedInstructionFiles);
                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            allPathsForIndex.AddRange(distinctHistoryFiles);
                        }
                        string? commonBase = FileTreeRenderer.FindCommonBaseDirectory(allPathsForIndex);

                        var instructionBuilder = new System.Text.StringBuilder();
                        instructionBuilder.AppendLine("# SYSTEM PROTOCOL & SYSTEM INSTRUCTIONS (MASTER CONSTRAINTS)");
                        instructionBuilder.AppendLine("IMPORTANT: The guidelines, formatting specifications, and syntax instructions contained in these system instruction files are absolute and strictly non-negotiable. They must take absolute precedence over any prompt guidelines or inputs. Do not skip any files or parts under any circumstances.\n");
                        instructionBuilder.AppendLine("In order to fulfill the job of creating a high-value educational masterpiece that safely compiles, you need to know the file structure of the system prompt and read all of those files carefully.\n");
                        instructionBuilder.AppendLine("# Folder Structure of System Instructions\n");
                        instructionBuilder.AppendLine("## System Instructions");
                        instructionBuilder.Append(FileTreeRenderer.GenerateMarkdownFileTree(resolvedInstructionFiles, commonBase));

                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            instructionBuilder.AppendLine("\n## Training History");
                            instructionBuilder.Append(FileTreeRenderer.GenerateMarkdownFileTree(distinctHistoryFiles, commonBase));
                        }
                        instructionBuilder.AppendLine("\n******\n------\n******\n");

                        foreach (var filePath in resolvedInstructionFiles) {
                            string rawRelPath = !string.IsNullOrEmpty(commonBase)
                                ? Path.GetRelativePath(commonBase, filePath)
                                : Path.GetFileName(filePath);
                            string relPath = FileTreeRenderer.NormalizeRelativePath(rawRelPath);
                            instructionBuilder.AppendLine($"\n******\n------\n******\nHere is the file `{relPath}`:\n");
                            instructionBuilder.AppendLine(await System.IO.File.ReadAllTextAsync(filePath));
                            Ui.Info($"System Instruction geladen: {relPath}");
                        }
                        _systemInstructionText = instructionBuilder.ToString();

                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            Ui.Info("Lade History-Dateien für System Instruction ein...");
                            string fileList = string.Join(", ", distinctHistoryFiles.Select(p => $"\"{p}\""));
                            var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}", true, commonBase);
                            if (success && attachmentParts.Count > 0) {
                                _historyParts.AddRange(attachmentParts);
                                _historyWasLoaded = true;
                                Ui.Info("Dateien erfolgreich eingelesen und in die System Instruction eingebunden.");
                            }
                            else {
                                Ui.Error("Einige oder alle History-Dateien konnten nicht eingelesen werden.");
                            }
                        }
                    }
                }
                else {
                    Ui.Warn("Keine System Instruction-Dateien gefunden oder konfiguriert.");
                }
            }
        }

        if (!_historyWasLoaded) {
            var distinctFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (distinctFiles.Count > 0) {
                Ui.Detail("Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
                FileTreeRenderer.PrintFileTree(distinctFiles, _config.VerboseConsoleOutput);
                string promptText = _config.LoadHistoryIntoSystemInstruction
                    ? "Sollen diese Dateien als System Instructions hochgeladen werden? (LoadHistoryIntoSystemInstruction = true)"
                    : "Sollen diese Dateien als History geladen und für die Session hochgeladen werden?";

                if (Ui.Confirm(promptText, true)) {
                    if (_config.LoadHistoryIntoSystemInstruction) {
                        Ui.Info("Lade Dateien als System Instructions hoch (dies kann einen Moment dauern)...");
                    }
                    else {
                        Ui.Info("Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
                    }
                    string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
                    var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}", _config.LoadHistoryIntoSystemInstruction);
                    if (success && attachmentParts.Count > 0) {
                        _historyParts.AddRange(attachmentParts);
                        _historyWasLoaded = true;
                        if (_config.LoadHistoryIntoSystemInstruction) {
                            Ui.Info("Dateien erfolgreich hochgeladen und werden in die System Instruction eingebunden (Acknowledge wird übersprungen).");
                        }
                        else {
                            Ui.Info("History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
                            if (!await SendHistoryHandshakeAsync(fileList)) return false;
                        }
                    }
                    else {
                        Ui.Error("Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
                    }
                }
            }
        }

        // [AI Context] Reset the rate-limit timer to now: session setup (loading system instructions and history)
        // can take significant time; the 150s guard will count from here and enforce a proper gap before the first API call.
        InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

        _sessionLogger.SetSessionMetadata(!string.IsNullOrEmpty(_systemInstructionText), _historyWasLoaded);
        _sessionLogger.InitializeSession();

        if (_config.CreateLogFiles) {
            string logDest = !string.IsNullOrWhiteSpace(_sessionLogger.CurrentSessionLogPath)
                ? _sessionLogger.CurrentSessionLogPath
                : _config.LogFolder;
            await ExtractionHelpers.LogSystemInstructionDumpAsync(logDest, _systemInstructionText, _historyParts);
        }

        await _sessionLogger.LogSessionSetupAsync();

        // [AI Context] Implicit prefix-cache warm-up, ported from AiStudioAutoExtractionSession. Runs once
        // here, before InitializeContextCachingAsync creates Vertex's explicit CachedContent -- the two
        // mechanisms are independent and can both be active.
        if (_config.EnableImplicitPrefixCacheWarmup) {
            if (!await PrimePrefixCacheAsync()) return false;
        }

        return true;
    }

    private async Task ProcessYouTubeTasksAsync() {
        List<YouTubeTranscriptionTask> tasksToProcess = [];

        if (_config.YouTubeTasks != null && _config.YouTubeTasks.Length > 0) {
            Ui.Info($"[YouTube Mode] Es wurden {_config.YouTubeTasks.Length} Aufgabe(n) in der Konfiguration gefunden.");
            if (!Ui.Confirm("Möchtest du diese Aufgaben ausführen?", true)) {
                var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
                if (interactiveTask != null) {
                    tasksToProcess.Add(interactiveTask);
                }
            }
            else {
                tasksToProcess.AddRange(_config.YouTubeTasks);
            }
        }
        else {
            Ui.Info("[YouTube Mode] Keine vorgegebenen YouTube-Aufgaben in der Konfiguration gefunden.");
            var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
            if (interactiveTask != null) {
                tasksToProcess.Add(interactiveTask);
            }
        }

        if (tasksToProcess.Count == 0) {
            Ui.Info("Keine YouTube-Aufgaben zum Verarbeiten.");
            return;
        }

        Ui.Step($"[YouTube Mode] Starte Transkription für {tasksToProcess.Count} YouTube-Video(s)...");

        await CleanupBucketAsync();
        try {
            if (!await EnsureSessionSetupAsync()) return;

            if (_config.UseContextCaching) {
                await InitializeContextCachingAsync();
            }

            foreach (var task in tasksToProcess) {
                if (string.IsNullOrWhiteSpace(task.VideoUrl)) continue;

                string baseName = string.IsNullOrWhiteSpace(task.OutputName) ? "youtube-lecture" : task.OutputName;
                if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
                    baseName = "step1-" + baseName;
                }

                string fileSpecificOutputFolder = Path.Combine(_config.TargetFolder, baseName);
                if (!Directory.Exists(fileSpecificOutputFolder)) {
                    Directory.CreateDirectory(fileSpecificOutputFolder);
                }

                Ui.Step($"[YouTube Consumer] Starte API-Extraktion für URL: {task.VideoUrl} ({baseName})");
                List<string> generatedTexFiles = [];
                string fullOutputTextRaw = "";

                for (int i = 0; i < task.Fragments.Count; i++) {
                    var frag = task.Fragments[i];
                    int partNum = i + 1;
                    Ui.Step($"Verarbeite Fragment {partNum}/{task.Fragments.Count}: {frag.StartTime} bis {frag.EndTime} ({frag.PartTitle})");

                    string dateNotice = (partNum == 1)
                        ? "Please note that since this is part 1 of the lecture, the date of the transcription is important."
                        : $"The lecture took place... Please note that since this is part {partNum} of the lecture, the date is not so important (but tell it anyway).";

                    string parsedPrompt = $"Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n[IMPORTANT INSTRUCTION FOR YOUTUBE VIDEO]:\nThis is part {partNum} ('{frag.PartTitle}') of the lecture. Please focus ONLY on transcribing and extracting the chosen video fragment starting at timestamp {frag.StartTime} and ending at timestamp {frag.EndTime}.\n{dateNotice}";

                    var attachmentParts = new List<Part> {
                        Part.FromUri(task.VideoUrl, "video/mp4")
                    };

                    string texOutput = (await TranscribeSegmentToLatexAsync(
                        task.VideoUrl, partNum, baseName, parsedPrompt, attachmentParts, generatedTexFiles
                    )).LatexBody;

                    if (!string.IsNullOrWhiteSpace(texOutput)) {
                        string cleanTex = LatexResponseCleaner.CleanLatexResponse(texOutput);
                        fullOutputTextRaw += $"\n\n% --- TEIL {partNum}: {frag.StartTime}-{frag.EndTime} ({frag.PartTitle}) ---\n" + cleanTex;

                        string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{partNum}.tex");
                        string partContent = cleanTex;
                        if (!partContent.StartsWith("% Startzeit:") && !partContent.StartsWith("% Zeitstempel:")) {
                            partContent = $"% Startzeit: {frag.StartTime} | Ende: {frag.EndTime}\n\n" + partContent;
                        }
                        await System.IO.File.WriteAllTextAsync(targetPartPath, partContent);
                        generatedTexFiles.Add(targetPartPath);
                        Ui.Success($"Teildatei gespeichert unter: {targetPartPath}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(fullOutputTextRaw)) {
                    string combinedPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}.tex");
                    await System.IO.File.WriteAllTextAsync(combinedPath, fullOutputTextRaw.Trim());
                    Ui.Success($"Zusammengeführte YouTube-Transkription gespeichert unter: {combinedPath}");
                }
            }
        }
        finally {
            await CleanupBucketAsync();
        }
    }

    /// <summary>
    /// [AI Context] Initializes or validates the remote Google Cloud Context Cache for system instructions.
    /// [Human] Prüft beim Start, ob der Google-Kontext-Cache noch gültig ist oder neu angelegt werden muss.
    /// </summary>
    private async Task InitializeContextCachingAsync() {
        if (!_config.UseContextCaching) {
            var state = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
            if (!string.IsNullOrEmpty(state.CacheName)) {
                Ui.Info("Context Caching wurde in Konfiguration deaktiviert. Lösche aktiven Cache bei Google...", "ContextCache");
                await ContextCacheStateManager.DeleteRemoteAsync(_client, state.CacheName);
                ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
            }
            return;
        }

        bool hasSys = !string.IsNullOrWhiteSpace(_systemInstructionText);
        bool hasHist = _config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0;
        if (!hasSys && !hasHist) return;

        var sysParts = new List<Part>();
        if (hasSys) sysParts.Add(new() { Text = _systemInstructionText });
        var cacheContents = new List<Content>();
        if (hasHist) {
            var textOnly = _historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)).ToList();
            var nonText = _historyParts.Where(p => p.FileData != null || p.InlineData != null).ToList();
            sysParts.AddRange(textOnly);
            if (nonText.Count > 0) {
                cacheContents.Add(new() { Role = "user", Parts = nonText });
            }
        }

        string combinedChecksum = ContextCacheStateManager.ComputeChecksum(_systemInstructionText + (hasHist ? $"_hist_{_historyParts.Count}" : ""));
        var savedState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);

        bool match = ContextCacheStateManager.MatchesConfig(
            savedState,
            _config.CurrentModel,
            _config.Temperature,
            _config.TopP,
            _config.TopK,
            _config.MaxOutputTokens,
            _config.ThinkingBudget,
            _config.ThinkingLevel,
            combinedChecksum
        );

        if (match && await ContextCacheStateManager.IsValidRemoteAsync(_client, savedState.CacheName!)) {
            _cachedContentName = savedState.CacheName;
            Ui.Info($"Nutze bestehenden Google Kontext-Cache: {_cachedContentName} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})", "ContextCache");
            return;
        }

        if (!string.IsNullOrEmpty(savedState.CacheName)) {
            await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
        }

        Ui.Info("Erstelle neuen Kontext-Cache bei Google (dies kann einen Moment dauern)...", "ContextCache");
        try {
            var cacheConfig = new CreateCachedContentConfig {
                SystemInstruction = sysParts.Count > 0 ? new Content { Role = "system", Parts = sysParts } : null,
                Contents = cacheContents.Count > 0 ? cacheContents : null,
                DisplayName = "vertex-sys-cache",
                Ttl = $"{_config.ContextCachingMinutes * 60}s"
            };
            var created = await _client.Caches.CreateAsync(_config.CurrentModel, cacheConfig);
            if (created != null && !string.IsNullOrEmpty(created.Name)) {
                _cachedContentName = created.Name;
                savedState.CacheName = _cachedContentName;
                savedState.Model = _config.CurrentModel;
                savedState.Temperature = _config.Temperature;
                savedState.TopP = _config.TopP;
                savedState.TopK = _config.TopK;
                savedState.MaxOutputTokens = _config.MaxOutputTokens;
                savedState.ThinkingBudget = _config.ThinkingBudget;
                savedState.ThinkingLevel = _config.ThinkingLevel;
                savedState.SystemInstructionChecksum = combinedChecksum;
                savedState.ExpireTimeUtc = DateTime.UtcNow.AddMinutes(_config.ContextCachingMinutes);
                if (created != null && created.ExpireTime.HasValue) {
                    savedState.ExpireTimeUtc = created.ExpireTime.Value.ToUniversalTime();
                }
                ContextCacheStateManager.SaveState(savedState, ContextCacheStateManager.StateFileVertex);
                Ui.Success($"Google Kontext-Cache erfolgreich angelegt: {_cachedContentName} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})", "ContextCache");
            }
        }
        catch (Exception ex) {
            Ui.Error($"Konnte Kontext-Cache nicht erstellen: {ex.GetType().Name} - {ex.Message}. Falle auf normalen Upload zurück.", "ContextCache");
            _cachedContentName = null;
        }
    }

    private void ConfigureCachingSettings() {
        Ui.Step("Context Caching Einstellungen");
        Ui.Detail($"UseContextCaching: {_config.UseContextCaching}");
        Ui.Detail($"ContextCachingMinutes: {_config.ContextCachingMinutes} min");
        Ui.Detail($"ContextCachingIncrementMinutes: {_config.ContextCachingIncrementMinutes} min");

        _config.UseContextCaching = Ui.Confirm("Context Caching aktivieren?", _config.UseContextCaching);
        _config.ContextCachingMinutes = Ui.Ask("Neue Standarddauer in Minuten:", _config.ContextCachingMinutes);
        _config.ContextCachingIncrementMinutes = Ui.Ask("Neues Verlängerungsintervall in Minuten:", _config.ContextCachingIncrementMinutes);

        ConfigLoader<VertexAutoExtractionConfig>.Save(_config);
        Ui.Success("Einstellungen in VertexAutoExtractionConfig.json gespeichert.");
    }

    private async Task<bool> SendHistoryHandshakeAsync(string loadedFiles = "") {
        var historyPromptParts = new List<Part>(_historyParts) {
            new() { Text = $"Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {_config.CurrentModel}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards." }
        };
        var userContent = new Content { Role = "user", Parts = historyPromptParts };

        _sessionPreamble.Add(userContent);

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
                if (nonText.Count > 0 && _sessionPreamble.Count == 0) {
                    _sessionPreamble.Add(new Content { Role = "user", Parts = nonText });
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

        Ui.Step($"Warte auf Bestätigung der History von {_config.CurrentModel}...");
        int backoff = 45;
        int maxRetries = 10;
        bool success = false;
        string fullResponse = "";
        int finalInputTokens = 0;
        int finalOutputTokens = 0;
        int finalCachedTokens = 0;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            fullResponse = "";
            using var cts = new CancellationTokenSource();
            void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
            Console.CancelKeyPress += cancelHandler;

            try {
                if (attempt > 1) Ui.Step($"[Versuch {attempt}/{maxRetries}] Sende Anfrage...");

                int requestInputTokens = 0;
                int requestOutputTokens = 0;
                int requestCachedTokens = 0;

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.CurrentModel, _sessionPreamble, requestConfig);
                await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
                    if (cts.IsCancellationRequested) break;
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Ui.Raw(txt);
                    fullResponse += txt;
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) requestInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) requestOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) requestCachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                }

                _sessionTotalInputTokens += requestInputTokens;
                _sessionTotalOutputTokens += requestOutputTokens;
                _sessionTotalCachedTokens += requestCachedTokens;
                finalInputTokens = requestInputTokens;
                finalOutputTokens = requestOutputTokens;
                finalCachedTokens = requestCachedTokens;
                Ui.Detail($"[Request Tokens]       Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {(Math.Max(0, requestInputTokens - requestCachedTokens)):N0} | Output: {requestOutputTokens:N0}");
                Ui.Detail($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {(Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens)):N0} | Output: {_sessionTotalOutputTokens:N0}");

                success = true;
                break;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
                Ui.Info("Bestätigung durch Benutzer abgebrochen.");
                break;
            }
            catch (Exception ex) {
                Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}");
                bool isOverloaded = ApiRetryPolicy.IsTransientError(ex);
                if (isOverloaded && attempt < maxRetries) {
                    int waitTime;
                    string contextMsg = " [History Bestätigung]";
                    string delayMessage = "Still waiting for the acknowledgment / processing...";

                    if (ApiRetryPolicy.IsNetworkConnectionError(ex)) {
                        waitTime = 300;
                        Ui.Warn($"[Netzwerk-Fehler]{contextMsg} Verbindung unterbrochen ({ex.GetType().Name}: {ex.Message}).");
                        Ui.Info("Keine Panik! Du hast jetzt 300 Sekunden Zeit, um deine Verbindung zu reparieren...");
                        delayMessage = "Warte auf Wiederherstellung der Internetverbindung...";
                    }
                    else if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
                        waitTime = 180;
                        Ui.Warn($"[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte 3 Minuten...");
                        backoff = waitTime;
                    }
                    else if (attempt == 1) {
                        var retryMatch = MyRegex().Match(ex.Message);
                        if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                            waitTime = serverSuggestedDelay + 20;
                            Ui.Warn($"[Rate Limit]{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime}s...");
                        }
                        else {
                            waitTime = backoff;
                            Ui.Warn($"[Rate Limit / Überlastung]{contextMsg} Initiale Wartezeit: {waitTime}s...");
                        }
                        backoff = waitTime;
                    }
                    else {
                        backoff += 30;
                        waitTime = backoff;
                        Ui.Warn($"[Rate Limit]{contextMsg} Inkrementiere Wartezeit. Warte {waitTime}s...");
                    }
                    if (!await InteractiveDelay.SmartDelayAsync(waitTime, delayMessage)) { break; }
                }
                else {
                    Ui.Error("Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
                    break;
                }
            }
            finally {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        if (success && !string.IsNullOrWhiteSpace(fullResponse)) {
            _sessionPreamble.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
            string logMsg = $"[History Acknowledgment] Angehängte Dateien: {loadedFiles}\n\nPrompt:\n{historyPromptParts.Last().Text}";
            await _sessionLogger.LogChatAsync(logMsg, logMsg, _config.CurrentModel, fullResponse, "AutoExtractionSetup", finalInputTokens, finalOutputTokens, finalCachedTokens);
            return true;
        }
        else {
            Ui.Error("Konnte Bestätigung für History nicht erhalten. Breche Extraktion ab.");
            _sessionPreamble.Clear();
            _historyWasLoaded = false;
            return false;
        }
    }

    /// <summary>
    /// [AI Context] Executes the batch processing workflow.
    /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
    /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
    /// </summary>
    private async Task ProcessFilesAsync(string[] videoFilesToProcess) {
        // Chronologisch aufsteigend sortieren anhand des Dateinamens und der Woche
        videoFilesToProcess = [.. videoFilesToProcess.OrderBy(videoFile => VideoDateParser.Parse(videoFile).Date).ThenBy(videoFile => VideoDateParser.Parse(videoFile).WeekNumber ?? int.MaxValue).ThenBy(videoFile => videoFile)];

        // [AI Context] We use a bounded channel (capacity 1) to synchronize the FFmpeg Producer task and the Gemini Consumer task.
        // This allows FFmpeg to prepare the *next* video while Gemini is waiting for the API to process the *current* video, maximizing throughput.
        // [Human] Wir nutzen einen 'Kanal' (Channel), um FFmpeg (Videobearbeitung) und Gemini (KI-Analyse) parallel laufen zu lassen.
        // Während die KI das erste Video analysiert, schneidet FFmpeg im Hintergrund schon das zweite. Das spart enorm Zeit!
        var preparedVideoQueue = Channel.CreateBounded<PreparedVideo>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
        var videoPreparationTask = Task.Run(() => VideoSegmentProducer.RunAsync(videoFilesToProcess, preparedVideoQueue.Writer, _config));

        // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
        // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
        bool anyVideoFailed = false;
        bool isFirstVideo = true;

        await foreach (var (file, fileSpecificOutputFolder, _, partsWithTimes, _, fullOriginalVideoDuration) in preparedVideoQueue.Reader.ReadAllAsync()) {
            if (isFirstVideo) {
                isFirstVideo = false;
                Ui.Info("Erstes Video wurde gesplittet. Erstelle jetzt Google Cloud Context Cache...", "Optimierung");
                await InitializeContextCachingAsync();
            }

            bool success = await ProcessPreparedVideoAsync(file, fileSpecificOutputFolder, partsWithTimes, fullOriginalVideoDuration);
            if (!success) anyVideoFailed = true;
        }

        await videoPreparationTask;

        if (anyVideoFailed) {
            Ui.Warn("Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).", "AutoExtraction");
        }
        else {
            Ui.Success("Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!", "AutoExtraction");
        }
    }

    private async Task<bool> ProcessPreparedVideoAsync(string file, string fileSpecificOutputFolder, IReadOnlyList<VideoSegment> partsWithTimes, double fullOriginalVideoDuration) {
        if (!Directory.Exists(fileSpecificOutputFolder)) {
            Directory.CreateDirectory(fileSpecificOutputFolder);
        }

        Ui.Step($"[Gemini Consumer] Starte API-Extraktion für {Path.GetFileName(file)}");
        List<string> generatedTexFiles = [];
        string baseName = Path.GetFileNameWithoutExtension(file);
        baseName = SpeedCompressedSuffixRegex().Replace(baseName, "");
        baseName = CompressedSuffixRegex().Replace(baseName, "");
        if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
            baseName = "step1-" + baseName;
        }
        string fullOutputTextRaw = "";
        string fullOutputTextOffsetted = "";
        TokenUsage fileTotalTokens = default;
        bool fileProcessingSuccess = true;
        TimeSpan cacheDuration = TimeSpan.FromHours(2);
        var audioTrackExtractor = new AudioTrackExtractor(file, fileSpecificOutputFolder);

        Task<SegmentUpload>? pendingVideoUploadTask = null;
        Task<List<Part>>? pendingAudioUploadTask = null;

        for (int i = 0; i < partsWithTimes.Count; i++) {
            string safePartPath = partsWithTimes[i].FilePath;
            double partStartTimeSeconds = partsWithTimes[i].StartTimeSeconds;
            string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}.tex");

            Ui.Step($"Verarbeite Teil {i + 1}/{partsWithTimes.Count}: {Path.GetFileName(safePartPath)}");
            if (System.IO.File.Exists(targetPartPath) && (DateTime.Now - System.IO.File.GetLastWriteTime(targetPartPath)) <= cacheDuration) {
                Ui.Info($"Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(targetPartPath)}. Überspringe API-Extraktion für diesen Teil.", "Resume");
                string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
                generatedTexFiles.Add(targetPartPath);
                fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampAdjuster.ExtractContentWithoutTimestampHeader(existingTex);
                if (_config.GenerateOffsetFiles) {
                    fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampAdjuster.AdjustTimestamps(LatexTimestampAdjuster.ExtractContentWithoutTimestampHeader(existingTex), partStartTimeSeconds);
                }
                audioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);
                continue;
            }

            SegmentTranscript segmentTranscript;
            bool uploadSuccess;
            string? parsedPrompt;
            List<Part> attachmentParts;

            Task<SegmentUpload> uploadTask;

            if (_config.EnableParallelFileUploads && pendingVideoUploadTask != null) {
                Ui.Info($"Nutze im Hintergrund bereits hochgeladenes Video für Teil {i + 1}...", "Pre-Upload");
                uploadTask = pendingVideoUploadTask;
            }
            else {
                uploadTask = UploadSegmentAndBuildPromptAsync(safePartPath, i + 1, partsWithTimes.Count, file, fullOriginalVideoDuration);
            }

            SegmentUpload upload = await uploadTask;
            (uploadSuccess, parsedPrompt, attachmentParts) = (upload.Succeeded, upload.Prompt, upload.Attachments);
            if (!uploadSuccess) {
                Ui.Error($"Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                fileProcessingSuccess = false;
                break;
            }

            audioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);

            if (_config.UseContextCaching) {
                var cacheState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
                double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
                bool cacheValid = false;

                if (!string.IsNullOrEmpty(_cachedContentName) && remainingMin > 0) {
                    if (remainingMin < _config.ContextCachingMinimumRemainingMinutes) {
                        Ui.Info($"Nur noch {remainingMin:F1} min verbleibend (Schwellenwert: {_config.ContextCachingMinimumRemainingMinutes} min). Verlängere automatisch um {_config.ContextCachingIncrementMinutes} min...", "Cache");
                        var updatedState = await ContextCacheStateManager.ExtendCacheAsync(_client, cacheState, _config.ContextCachingIncrementMinutes, ContextCacheStateManager.StateFileVertex);
                        if (updatedState != null) {
                            Ui.Info($"Cache verlängert. Gültig bis {updatedState.ExpireTimeUtc.ToLocalTime():t}.", "Cache");
                            cacheValid = true;
                        }
                    }
                    else {
                        cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(_client, _cachedContentName);
                    }
                }

                if (!cacheValid) {
                    Ui.Info("Kein gültiger Cache aktiv oder Cache abgelaufen. Erstelle neuen Google Kontext-Cache für diesen Teil...", "Cache");
                    ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
                    _cachedContentName = null;
                    await InitializeContextCachingAsync();
                }
            }

            if (_config.EnableParallelFileUploads) {
                if (i + 1 < partsWithTimes.Count) {
                    string nextTexPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 2}.tex");
                    if (!System.IO.File.Exists(nextTexPath)) {
                        Ui.Info($"Starte parallelen Video-Upload für nächsten Teil ({i + 2}/{partsWithTimes.Count}) im Hintergrund...", "Pre-Upload");
                        pendingVideoUploadTask = UploadSegmentAndBuildPromptAsync(partsWithTimes[i + 1].FilePath, i + 2, partsWithTimes.Count, file, fullOriginalVideoDuration);
                    }
                    else {
                        pendingVideoUploadTask = null;
                    }
                }
                else if (i == partsWithTimes.Count - 1 && _config.GenerateAudioFile && _config.GoIntoLatexRefinement) {
                    pendingAudioUploadTask = Task.Run(async () => {
                        if (audioTrackExtractor.PendingTask != null) {
                            await audioTrackExtractor.PendingTask;
                        }
                        var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                        string audioPath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                           ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");
                        if (System.IO.File.Exists(audioPath)) {
                            Ui.Info($"Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...", "Pre-Upload");
                            var handler = new AttachmentUploader(_client, fileSpecificOutputFolder, [fileSpecificOutputFolder], false, _config.GcsBucketName);
                            var (s, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioPath}\"");
                            if (s) return attached;
                        }
                        return [];
                    });
                }
            }

            segmentTranscript = await TranscribeSegmentToLatexAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles);

            fileTotalTokens += segmentTranscript.Usage;
            int partFreshTokens = segmentTranscript.Usage.Fresh;

            if (!string.IsNullOrWhiteSpace(segmentTranscript.LatexBody)) {
                string cleanTex = LatexResponseCleaner.CleanLatexResponse(segmentTranscript.LatexBody);

                fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {segmentTranscript.Usage.Input:N0}, Gecacht {segmentTranscript.Usage.Cached:N0}, Frisch/Video {partFreshTokens:N0}, Output {segmentTranscript.Usage.Output:N0}) ---\n" + cleanTex;
                if (_config.GenerateOffsetFiles) {
                    fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {segmentTranscript.Usage.Input:N0}, Gecacht {segmentTranscript.Usage.Cached:N0}, Frisch/Video {partFreshTokens:N0}, Output {segmentTranscript.Usage.Output:N0}) ---\n" + LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                }

                string partHeader = TexDocumentWriter.BuildPartHeader(
                    sourcePartFileName: Path.GetFileName(safePartPath),
                    partStartTimeSeconds: partStartTimeSeconds,
                    usage: segmentTranscript.Usage,
                    model: _config.CurrentModel, temperature: _config.Temperature, topP: _config.TopP, topK: _config.TopK,
                    maxOutputTokens: _config.MaxOutputTokens, thinkingBudget: _config.ThinkingBudget, thinkingLevel: _config.ThinkingLevel);
                string uniqueTargetPartPath = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPath);
                await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + cleanTex);

                if (_config.GenerateOffsetFiles) {
                    string offsettedPartContent = LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                    string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}-offset.tex");
                    string uniqueTargetPartPathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPathOffset, partHeader + offsettedPartContent);
                    Ui.Success($"Offset-korrigierter Teil gespeichert unter: {Path.GetFileName(uniqueTargetPartPathOffset)}");
                }
                generatedTexFiles.Add(uniqueTargetPartPath);
            }
            else {
                Ui.Error($"Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
                fileProcessingSuccess = false;
                foreach (var f in generatedTexFiles) {
                    try { System.IO.File.Delete(f); } catch { /* Ignore */ }
                }
                if (Directory.Exists(fileSpecificOutputFolder) && !Directory.EnumerateFileSystemEntries(fileSpecificOutputFolder).Any()) {
                    Directory.Delete(fileSpecificOutputFolder);
                }
                break;
            }
        }

        if (fileProcessingSuccess) {
            string targetFilePath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-all.tex");
            string targetFilePathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-all-offset.tex");

            string uniqueTargetFilePath = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePath);
            string header = TexDocumentWriter.BuildCombinedHeader(
                sourceFileName: Path.GetFileName(file),
                totalParts: partsWithTimes.Count,
                totalUsage: fileTotalTokens,
                model: _config.CurrentModel, temperature: _config.Temperature, topP: _config.TopP, topK: _config.TopK,
                maxOutputTokens: _config.MaxOutputTokens, thinkingBudget: _config.ThinkingBudget, thinkingLevel: _config.ThinkingLevel);
            await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, header + fullOutputTextRaw);
            Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}", "AutoExtraction");

            string refinementTargetFile = uniqueTargetFilePath;

            if (_config.GenerateOffsetFiles) {
                string uniqueTargetFilePathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePathOffset);
                await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, header + fullOutputTextOffsetted);
                Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}", "AutoExtraction");
                refinementTargetFile = uniqueTargetFilePathOffset;
            }

            if (audioTrackExtractor.PendingTask != null) {
                Ui.Info($"Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...", "AutoExtraction");
                await audioTrackExtractor.PendingTask;
            }

            if (_latexRefinementConfig != null) {
                _latexRefinementConfig.UseVertex = AppConfig.IsVertexAiEnabled;
                if (_config.NumberOfParts <= 1) {
                    Ui.Info($"NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.", "AutoExtraction");
                    _latexRefinementConfig.Step1MergeAndTimestamp.Enabled = false;
                }
            }
            Client refinementClient = GoogleAiClientBuilder.BuildVertexClient(_latexRefinementConfig?.VertexProjectId ?? "", _latexRefinementConfig?.VertexLocation ?? "");

            var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
            string audioFilePath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                   ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");

            List<Part>? preUploadedAudioParts = null;
            if (_config.EnableParallelFileUploads && pendingAudioUploadTask != null) {
                Ui.Info("Warte auf Abschluss des parallelen Audio-Uploads...", "AutoExtraction");
                preUploadedAudioParts = await pendingAudioUploadTask;
            }

            Ui.Step($"Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
            var refinementSession = new LatexRefinementSession(
                refinementClient,
                RefinementOptions.ForFile(_latexRefinementConfig!, refinementTargetFile, _config, audioFilePath, preUploadedAudioParts));

            await refinementSession.StartAsync();
        }

        return fileProcessingSuccess;
    }

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
        if (_config.InlinePrecedingLecTexParts && _config.DebugSendReferenceFile && previousTexFiles.Count > 0) {
            Ui.Info("Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:", "Kontext");
            preVideoBuilder.Append(await BuildPreviousTexReferenceBlockAsync(partFile, previousTexFiles));
        }
        preVideoBuilder.Append(GetStaticPromptBeginning(partNumber));
        userPromptParts.Add(new Part { Text = preVideoBuilder.ToString() });

        userPromptParts.AddRange(attachmentParts);

        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        if (!_config.InlinePrecedingLecTexParts && _config.DebugSendReferenceFile && previousTexFiles.Count > 0) {
            Ui.Info("Sende folgende bereits generierte .tex-Dateien als Referenzkontext mit (am Ende angehängt):", "Kontext");
            string contextText = await BuildPreviousTexReferenceBlockAsync(partFile, previousTexFiles);
            userPromptParts.Add(new Part { Text = contextText.TrimEnd() });
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

    private static async Task<string> BuildPreviousTexReferenceBlockAsync(string partFile, List<string> previousTexFiles) {
        var builder = new System.Text.StringBuilder(
            "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
            "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
            "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
            "CRITICAL RULES:\n" +
            "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
            $"2. Your SOLE task is to transcribe the NEW attached video segment: `{Path.GetFileName(partFile)}`.\n" +
            "3. Treat these context files as read-only and focus entirely on the new video fragment.\n\n");
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

    /// <summary>
    /// [AI Context] Financial Guardrail: Ensures the cloud storage bucket is purged immediately after processing to prevent accumulating storage costs for massive temporary video files.
    /// </summary>
    private Task CleanupBucketAsync() => GcsWorkspace.PurgeAsync(_config.GcsBucketName);

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SegmentCompleteRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VideoCompleteRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)s""")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{2,4}-)?\d{2}-\d{2}-(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)?\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FilenamePatternRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-speed-[\d\.]+-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SpeedCompressedSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CompressedSuffixRegex();

}
