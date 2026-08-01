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
/// [AI Context] Orchestrates the fully automated transcription pipeline. 
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// Split into partial classes:
/// - AiStudioAutoExtractionSession.cs (core pipeline, file batching, YouTube transcription)
/// - AiStudioAutoExtractionSession.PrefixCache.cs (implicit prefix cache warming & history loading)
/// Member Index:
/// - StartAsync: Validates folders, prompts mode selection (batch, single, youtube), and begins execution.
/// - SetupContextAndProcessAsync: Ensures system instructions/preamble are loaded then processes files.
/// - ProcessFilesAsync: Producer/consumer loop for MP4 video segment extraction.
/// - ProcessYouTubeTasksAsync: YouTube video download and transcription pipeline.
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos.
/// </summary>
public partial class AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentUploader attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) : IYouTubeTranscriptionHost {
    public static readonly string[] AvailableModels = [
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview",
        "gemini-2.5-flash"
    ];


    private readonly Client _client = client;
    private readonly AiStudioAutoExtractionConfig _config = config;
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
    private int _sessionMaxFreshTokens = 0;
    private int _lastWarmupInputTokens = 0;

    /// <summary>
    /// [AI Context] Read-only preamble text placed before reference_context blocks in the user-turn.
    /// Instructs the model to treat previous .tex outputs as read-only reference, not as content to rewrite.
    /// Used identically in warm-up handshakes and real video extraction requests.
    /// [Human] Einleitungstext für die Referenz-Kontextblöcke – wird sowohl im Warm-up als auch in der echten Extraktion verwendet.
    /// </summary>
    private static readonly string ReferenceContextPreamble =
        "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
        "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
        "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
        "CRITICAL RULES:\n" +
        "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
        "2. Your SOLE task is to transcribe the new attached video segment verbatim.\n" +
        "3. Treat these context files as read-only and focus entirely on the new video fragment.\n\n";

    /// <summary>
    /// [AI Context] Entry point that validates the source/target directories and checks filename formats.
    /// [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
    /// </summary>
    /// <summary>
    /// [AI Context] How recently a part's .tex must have been written for it to be reused instead
    /// of re-requested. Was a hardcoded 2 hours on <c>VideoProcessingState</c> with no way to reach
    /// it, which made it the most expensive invisible constant here: a batch retried after the
    /// window lapsed silently re-bought every part it had already paid for. Still 2 hours by
    /// default, so the interactive run is unchanged.
    /// [Human] Wie alt eine bereits erzeugte .tex-Datei sein darf, um wiederverwendet zu werden.
    /// </summary>
    public TimeSpan ResumeWindow { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// [AI Context] Non-interactive entry point: process exactly these files and report whether
    /// every one succeeded. This is what <see cref="StartAsync"/> reaches after its mode menu has
    /// chosen a file list, exposed directly so the CLI can supply that list as arguments instead.
    /// Both paths run identical code from here down; the menu is the only difference.
    /// [Human] Einstiegspunkt ohne Menü: verarbeitet genau die übergebenen Dateien.
    /// </summary>
    /// <returns>false if any file failed, so a caller can report partial success.</returns>
    public async Task<bool> RunAsync(IReadOnlyList<string> files) {
        if (!PrepareWorkspace()) return false;
        if (files.Count == 0) {
            Ui.Info("Keine Dateien ausgewählt.");
            return true;
        }

        if (!await EnsureSessionSetupAsync()) return false;
        if (_config.OnlyDoWarmUp) {
            Ui.Success("WarmUp & Context Setup abgeschlossen (OnlyDoWarmUp = true). Beende Session ohne Videoextraktion.", "Cache-Warming");
            return true;
        }
        return await ProcessFilesAsync([.. files]);
    }

    /// <summary>
    /// [AI Context] Validates the source folder and settles the target folder, which defaults to a
    /// subfolder of the source. Shared by the interactive and headless entry points so they cannot
    /// disagree about where output lands.
    /// [Human] Prüft den Quellordner und legt den Zielordner fest.
    /// </summary>
    private bool PrepareWorkspace() {
        if (!Directory.Exists(_config.SourceFolder)) {
            Ui.Error($"Quellordner nicht gefunden: {_config.SourceFolder}");
            return false;
        }

        // If no specific target folder is provided in config, create one inside the source folder.
        if (string.IsNullOrWhiteSpace(_config.TargetFolder)) {
            _config.TargetFolder = Path.Combine(_config.SourceFolder, "extracted_output");
        }

        if (!Directory.Exists(_config.TargetFolder)) {
            Directory.CreateDirectory(_config.TargetFolder);
        }

        return true;
    }

    public async Task StartAsync() {
        if (!PrepareWorkspace()) {
            return;
        }

        Ui.Step("Automatisierte Extraktion (AI Studio)");
        Ui.Detail($"Quelle (Source): {_config.SourceFolder}");
        Ui.Detail($"Ziel (Target):   {_config.TargetFolder}");
        if (_config.ActiveApiProfile == 0) {
            Ui.Detail("API-Key:         Dedizierter Key für automatisierte Extraktion");
        }
        else {
            Ui.Detail($"API-Key:         Profil {_config.ActiveApiProfile} (API_KEY-ai-studio-test-project-{_config.ActiveApiProfile})");
        }

        string[] videoFilesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
        foreach (var videoFile in videoFilesToProcess) {
            var dateInfo = VideoDateParser.Parse(videoFile);
            if (!dateInfo.IsValid) {
                Ui.Warn($"Video entspricht nicht dem Datums-/Wochen-Namensschema: {Path.GetFileName(videoFile)}", "AutoExtraction");
                Ui.Detail("Erwartetes Format z.B.: 02-16-2026-monday-week1-Analysis_II.mp4 oder week1-02-16-2026-montag.mp4");
            }
        }

        // The mode menu loops: every branch below can be backed out of, and landing here again is
        // what "back" from the branch means. Only "Zurück" leaves the session.
        while (true) {
            var choice = Ui.Select("Modus auswählen:", [
                ("1) 🎬 Einzelnes Video auswählen und konvertieren", ExtractionMode.SingleVideo),
                ("2) 🚀 Alle Videos im Quellordner konvertieren (Standard)", ExtractionMode.AllVideos),
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
                    await new YouTubeTaskRunner(_config, this).RunAsync();
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
    private async Task SetupContextAndProcessAsync(string[] files) => await RunAsync(files);

    public async Task<bool> EnsureSessionSetupAsync() {
        // --- Phase 1: Load System Instruction text from disk (if not already loaded) ---
        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (!await TryLoadSystemInstructionWithHistoryAsync()) return false;
        }

        // --- Phase 2: Load history as multi-turn preamble (if not handled via System Instruction above) ---
        if (!_historyWasLoaded) {
            await LoadHistoryAsMultiTurnPreambleAsync();
        }

        // --- Phase 3: Finalize session setup (logging, debug roundtrip) ---
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

        if (_config.DebugHelloRoundtrip) {
            var roundtrip = await DebugRoundtripRunner.RunAsync(_client, _config, GetValidSystemInstructionParts());
            if (!roundtrip.Succeeded) return false;

            // The runner reports usage rather than mutating these itself, so the session stays the
            // single writer of its own counters and preamble.
            _sessionTotalInputTokens += roundtrip.Usage.Input;
            _sessionTotalOutputTokens += roundtrip.Usage.Output;
            _sessionTotalCachedTokens += roundtrip.Usage.Cached;
            _sessionMaxFreshTokens = Math.Max(_sessionMaxFreshTokens, roundtrip.Usage.Fresh);
            _sessionPreamble.Add(roundtrip.UserTurn!);
            _sessionPreamble.Add(roundtrip.ModelTurn!);

            int delay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 60;
            Ui.Detail($"Warte {delay}s (Token Refill) nach Debug 'Hello' Roundtrip...");
            await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Debug Roundtrip...");
            Ui.Success("'Hello' Roundtrip erfolgreich.");
        }

        return true;
    }

    /// <summary>
    /// [AI Context] Resolves system instruction files from disk, builds the instruction text,
    /// and optionally loads history files into the system instruction (with batched or bulk warm-up).
    /// Returns false only if the user declines to load, which is not an error.
    /// [Human] Lädt die System-Instruction-Dateien und (optional) History-Dateien ein.
    /// </summary>
    private async Task<bool> TryLoadSystemInstructionWithHistoryAsync() {
        if (_config.SystemInstructionPaths == null || _config.SystemInstructionPaths.Length == 0) return true;

        Ui.Info("Folgende System Instruction-Dateien sind konfiguriert:");
        var resolvedInstructionFiles = HistoryFileResolver.ResolveHistoryFiles(_config.SystemInstructionPaths);

        if (resolvedInstructionFiles.Count == 0) {
            Ui.Warn("Keine System Instruction-Dateien gefunden oder konfiguriert.");
            return true;
        }

        FileTreeRenderer.PrintFileTree(resolvedInstructionFiles, _config.VerboseConsoleOutput);

        // Optionally resolve history files that will be merged into the system instruction
        List<string> historyFilesForSystemInstruction = [];
        bool shouldMergeHistory = _config.LoadHistoryIntoSystemInstruction && !_historyWasLoaded;
        if (shouldMergeHistory) {
            historyFilesForSystemInstruction = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (historyFilesForSystemInstruction.Count > 0) {
                Ui.Info("Folgende Dateien sind als History konfiguriert (werden aber direkt in die System Instruction geladen):");
                FileTreeRenderer.PrintFileTree(historyFilesForSystemInstruction, _config.VerboseConsoleOutput);
            }
        }

        // Ask user for confirmation
        string confirmPrompt = shouldMergeHistory && historyFilesForSystemInstruction.Count > 0
            ? "System Instructions und History laden?"
            : "System Instructions laden?";
        if (!Ui.Confirm(confirmPrompt, true)) {
            Ui.Warn("System Instructions wurden vom Benutzer nicht geladen.");
            return true;
        }

        // Determine common base for relative path display
        var allPathsForBaseResolution = new List<string>(resolvedInstructionFiles);
        if (shouldMergeHistory && historyFilesForSystemInstruction.Count > 0) {
            allPathsForBaseResolution.AddRange(historyFilesForSystemInstruction);
        }
        string? commonBase = FileTreeRenderer.FindCommonBaseDirectory(allPathsForBaseResolution);

        // Build the system instruction text from the instruction files
        string instructionText = await SystemInstructionTextBuilder.BuildAsync(resolvedInstructionFiles, historyFilesForSystemInstruction, commonBase, _config.VerboseConsoleOutput);

        // If history should be merged into system instruction, do so now
        if (shouldMergeHistory && historyFilesForSystemInstruction.Count > 0) {
            _systemInstructionText = instructionText;

            if (_config.HistoryBatchCount > 0) {
                if (_config.EnableImplicitPrefixCacheWarmup && !await WarmUpWithBatchedHistoryAsync(historyFilesForSystemInstruction, commonBase)) return false;
            } else {
                // Load all history files into the system instruction at once (non-batched)
                Ui.Info("Lade History-Textdateien direkt in den System-Instruction-Text ein (einmaliges Paket)...");
                var instructionBuilder = new System.Text.StringBuilder(instructionText);
                _historyParts.AddRange(await SystemInstructionTextBuilder.AppendHistoryFilesAsync(
                    historyFilesForSystemInstruction, instructionBuilder, commonBase, _attachmentHandler, _config.VerboseConsoleOutput));
                _systemInstructionText = instructionBuilder.ToString();
                if (_config.EnableImplicitPrefixCacheWarmup && !await PrimePrefixCacheAsync(includeDummyPart0: true)) return false;
            }

            _historyWasLoaded = true;
        } else {
            _systemInstructionText = instructionText;
            if (_config.EnableImplicitPrefixCacheWarmup && !await PrimePrefixCacheAsync(includeDummyPart0: true)) return false;
        }

        Ui.Success("System Instructions erfolgreich geladen.");
        return true;
    }


    /// <summary>
    /// [AI Context] Loads history files as multi-turn preamble entries (not into system instruction).
    /// This is the alternative path used when LoadHistoryIntoSystemInstruction is false.
    /// Files are uploaded via AttachmentUploader and stored as acknowledged turns in _sessionPreamble.
    /// [Human] Lädt History-Dateien als Multi-Turn-Preamble (nicht in die System Instruction).
    /// </summary>
    private async Task LoadHistoryAsMultiTurnPreambleAsync() {
        var resolvedHistoryFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
        if (resolvedHistoryFiles.Count == 0) return;

        Ui.Info("Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
        FileTreeRenderer.PrintFileTree(resolvedHistoryFiles, _config.VerboseConsoleOutput);

        string confirmPrompt = _config.LoadHistoryIntoSystemInstruction
            ? "Sollen diese Dateien als System Instructions hochgeladen werden?"
            : "Sollen diese Dateien als History geladen und für die Session hochgeladen werden?";
        if (!Ui.Confirm(confirmPrompt, true)) {
            Ui.Warn("History-Dateien wurden vom Benutzer nicht geladen.");
            return;
        }

        if (_config.LoadHistoryIntoSystemInstruction) {
            Ui.Info("Lade Dateien als System Instructions hoch (dies kann einen Moment dauern)...");
            string quotedFileList = string.Join(", ", resolvedHistoryFiles.Select(p => $"\"{p}\""));
            var (uploadSuccess, _, uploadedParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{quotedFileList}\"", _config.LoadHistoryIntoSystemInstruction);
            if (uploadSuccess && uploadedParts.Count > 0) {
                _historyParts.AddRange(uploadedParts);
                _historyWasLoaded = true;
                Ui.Info("Dateien erfolgreich hochgeladen und werden in die System Instruction eingebunden.");
                if (_config.EnableImplicitPrefixCacheWarmup) {
                    await PrimePrefixCacheAsync(includeDummyPart0: true);
                }
            } else {
                Ui.Error("Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
            }
            return;
        }

        // Non-system-instruction path: upload as multi-turn batches
        List<(string Label, List<string> Files)> historyBatches = _config.HistoryBatchCount > 1
            ? HistoryFileResolver.GroupHistoryFilesByTopLevelSubfolder(resolvedHistoryFiles, _config.HistoryPreloadPaths, _config.HistoryBatchCount)
            : [("(alle)", resolvedHistoryFiles)];

        if (_config.HistoryBatchCount > 1) {
            Ui.Detail($"History-Batching: Aufgeteilt in {historyBatches.Count} Batch(es) (konfiguriert: {_config.HistoryBatchCount}).");
        }

        bool allBatchesSucceeded = true;
        for (int batchIndex = 0; batchIndex < historyBatches.Count; batchIndex++) {
            var (batchLabel, batchFiles) = historyBatches[batchIndex];
            bool isLastBatch = batchIndex == historyBatches.Count - 1;

            Ui.Info($"History-Batch {batchIndex + 1}/{historyBatches.Count}: '{batchLabel}' ({batchFiles.Count} Datei(en)) wird hochgeladen...");

            string quotedBatchFiles = string.Join(", ", batchFiles.Select(p => $"\"{p}\""));
            var (uploadSuccess, _, uploadedParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {quotedBatchFiles}", false);

            if (uploadSuccess && uploadedParts.Count > 0) {
                _historyParts.AddRange(uploadedParts);
                _historyWasLoaded = true;
                Ui.Info($"History-Batch {batchIndex + 1}/{historyBatches.Count} erfolgreich geladen ({uploadedParts.Count} Part(s)).");

                if (!isLastBatch) {
                    int interBatchDelay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 120;
                    Ui.Detail($"Inter-Batch-Pause: {interBatchDelay}s vor nächstem Batch...");
                    await InteractiveDelay.SmartDelayAsync(interBatchDelay, $"Warte zwischen Batch {batchIndex + 1} und {batchIndex + 2}...");
                }
            } else {
                Ui.Error($"Batch {batchIndex + 1}/{historyBatches.Count} konnte nicht hochgeladen werden.");
                allBatchesSucceeded = false;
                break;
            }
        }

        if (allBatchesSucceeded && _historyWasLoaded) {
            int tokenRefillDelay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130;
            Ui.Detail($"Warte {tokenRefillDelay} Sekunden (Token Refill) nach History-Upload...");
            await InteractiveDelay.SmartDelayAsync(tokenRefillDelay, "Warte auf Token-Refill nach History-Acknowledgment...");
        }
    }



    private List<Part> GetValidSystemInstructionParts() {
        var sysParts = new List<Part>();
        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
        if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) {
            var validParts = _historyParts.Where(p => p.FileData == null);
            sysParts.AddRange(validParts);
        }
        return sysParts;
    }

    /// <summary>
    /// [AI Context] Returns the static, per-partNumber prefix of the user-turn prompt.
    /// This text is deterministic and placed BEFORE the video payload in every request, so it forms a
    /// stable, growing cache prefix that the warm-up can pre-activate in the same token order.
    /// partNumber == 1  → no segment_start parameter (matches the warm-up dummy turn exactly).
    /// partNumber  > 1  → adds the segment_start parameter for mid-lecture continuity.
    /// [Human] Gibt den immer gleichen statischen Anfang des Prompts zurück. Steht VOR dem Video, damit Google einen Cache-Hit erkennt.
    /// </summary>
    private static string GetStaticPromptBeginning(int partNumber) {
        string s = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n" +
                   "<context_and_parameters>\n" +
                   "IMPORTANT: The System Instructions (System Prompt) contain the absolute rules, syntax specifications, and constraints for the lecture transcription and MUST be followed strictly. The parameters below specify details for this video fragment:\n\n" +
                   "<parameter name=\"merging_and_scope\">Do NOT attempt to merge the current part with the previous parts (i.e. do not try to fix the cut). Focus solely on transcribing this fragment as it is. As specified in the System Instructions, keep mathematical derivations and explanations self-contained and grouped within 'math-stroke' environments to preserve logical flow.</parameter>\n";
        if (partNumber != 1) {
            s += "<parameter name=\"segment_start\">\n" +
                 "1. Start the transcription EXACTLY where the audio begins in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction.\n" +
                 "2. If the previous part ended in the middle of an environment (like a `proof`, `short-proof`, or `math-stroke`), you MUST logically continue that environment in this part (e.g., start with `\\begin{proof}` or `\\begin{math-stroke}` if the professor is still doing the proof/derivation). However, you must still transcribe the spoken words exactly from where this new video segment begins.\n" +
                 "</parameter>\n";
        }
        return s;
    }


    /// <summary>
    /// [AI Context] Executes the batch processing workflow.
    /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
    /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
    /// </summary>
    /// <returns>
    /// false if any file failed. The interactive path only prints this, but an unattended caller
    /// needs it in the exit status - a batch that transcribed four videos and failed the fifth is
    /// neither success nor failure.
    /// </returns>
    private async Task<bool> ProcessFilesAsync(string[] videoFilesToProcess) {
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

        await foreach (var (file, fileSpecificOutputFolder, _, partsWithTimes, _, fullOriginalVideoDuration) in preparedVideoQueue.Reader.ReadAllAsync()) {
            bool success = await ProcessPreparedVideoAsync(file, fileSpecificOutputFolder, partsWithTimes, fullOriginalVideoDuration);
            if (!success) anyVideoFailed = true;
        }

        // Warten, bis der Producer-Task sauber beendet wurde (fängt Fehler ab)
        await videoPreparationTask;

        if (anyVideoFailed) {
            Ui.Warn("Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).", "AutoExtraction");
        }
        else {
            Ui.Success("Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!", "AutoExtraction");
        }

        return !anyVideoFailed;
    }

    /// <summary>
    /// [AI Context] Processes one already-split video end to end: sequentially extracts LaTeX from
    /// each part via the Gemini API (with resume-from-disk caching, parallel pre-uploads, and rate-limit
    /// pacing), writes the combined document, and launches LatexRefinementSession. Extracted from the
    /// former single ~300-line ProcessFilesAsync consumer-loop body — one call per video. Further split
    /// (Phase 4.5) into TranscribeSegmentsAsync (the per-part loop) and FinalizeVideoOutputAsync (combined
    /// document + refinement launch), sharing state via VideoProcessingState since the loop mutates several
    /// values across iterations (pending upload/rate-limit tasks, accumulated text and tokens).
    /// [Human] Verarbeitet ein bereits gesplittetes Video vollständig: extrahiert LaTeX Teil für Teil
    /// über die Gemini-API, schreibt das Gesamtdokument und startet das Refinement.
    /// </summary>
    /// <returns>false if any part failed and the file's processing was aborted (an error condition the caller reports as "hasErrors").</returns>
    private async Task<bool> ProcessPreparedVideoAsync(string file, string fileSpecificOutputFolder, IReadOnlyList<VideoSegment> partsWithTimes, double fullOriginalVideoDuration) {
        // Ensure the file-specific output folder exists before starting processing
        if (!Directory.Exists(fileSpecificOutputFolder)) {
            Directory.CreateDirectory(fileSpecificOutputFolder);
        }

        Ui.Step($"[Gemini Consumer] Starte API-Extraktion für {Path.GetFileName(file)}");

        // Handle Audio File Generation
        var state = new VideoProcessingState(ComputeBaseName(file), new AudioTrackExtractor(file, fileSpecificOutputFolder)) {
            RefinementClient = ResolveRefinementClientAndConfigureParams()
        };

        await TranscribeSegmentsAsync(state, file, fileSpecificOutputFolder, partsWithTimes, fullOriginalVideoDuration);

        if (state.FileProcessingSuccess) {
            await FinalizeVideoOutputAsync(state, file, fileSpecificOutputFolder, partsWithTimes.Count);
        }

        return state.FileProcessingSuccess;
    }

    /// <summary>
    /// [AI Context] Strips the -speed-N-compressed / -compressed suffixes FFmpeg preprocessing adds and
    /// ensures the step1- prefix, giving the stable base name used for every part/combined output file.
    /// [Human] Baut den Basisnamen für die Ausgabedateien aus dem Videodateinamen.
    /// </summary>
    private static string ComputeBaseName(string file) => ExtractionHelpers.ComputeTexBaseName(file);

    /// <summary>
    /// [AI Context] Initializes refinementClient early because the parallel audio upload task
    /// (pendingAudioUploadTask) needs to upload the audio to the EXACT SAME Google Cloud Project / API Key
    /// that LatexRefinementSession will use. Otherwise, LatexRefinementSession gets a ClientError: "You do
    /// not have permission to access the File". Also applies UseChosenModelForRestOfPipeline's parameter
    /// override onto the refinement steps as a side effect, same as before the Phase 4.5 split.
    /// [Human] Löst den Client für das Refinement auf und übernimmt bei Bedarf die gewählten Modell-Parameter.
    /// </summary>
    private Client? ResolveRefinementClientAndConfigureParams() {
        if (!_config.GoIntoLatexRefinement) return null;

        if (_latexRefinementConfig != null) {
            _latexRefinementConfig.UseVertex = false;
            if (_config.NumberOfParts <= 1 && _latexRefinementConfig.Step1MergeAndTimestamp != null) {
                Ui.Info($"NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.", "AutoExtraction");
                _latexRefinementConfig.Step1MergeAndTimestamp.Enabled = false;
            }
            if (_config.UseChosenModelForRestOfPipeline) {
                Ui.Info($"UseChosenModelForRestOfPipeline = true. Übernehme Modell '{_config.CurrentModel}' und Parameter im Arbeitsspeicher für das Refinement...", "AutoExtraction");
                void ApplyModelParametersTo(BackendParameters target) {
                    target.CurrentModel = _config.CurrentModel;
                    target.Temperature = _config.Temperature;
                    target.TopP = _config.TopP;
                    target.TopK = _config.TopK;
                    target.MaxOutputTokens = _config.MaxOutputTokens;
                    target.ThinkingBudget = _config.ThinkingBudget;
                    target.ThinkingLevel = _config.ThinkingLevel;
                }
                if (_latexRefinementConfig.Step1MergeAndTimestamp?.AiStudio != null) ApplyModelParametersTo(_latexRefinementConfig.Step1MergeAndTimestamp.AiStudio);
                if (_latexRefinementConfig.Step2SpeechRefinement?.AiStudio != null) ApplyModelParametersTo(_latexRefinementConfig.Step2SpeechRefinement.AiStudio);
                if (_latexRefinementConfig.Step3LastRefinement?.AiStudio != null) ApplyModelParametersTo(_latexRefinementConfig.Step3LastRefinement.AiStudio);
            }
        }
        string? extractedRefinementEnvName = (_latexRefinementConfig?.AiStudioApiKeyEnvNames != null && _latexRefinementConfig.AiStudioApiKeyEnvNames.Length > _latexRefinementConfig.AiStudioActiveApiProfile)
            ? _latexRefinementConfig.AiStudioApiKeyEnvNames[_latexRefinementConfig.AiStudioActiveApiProfile]
            : null;
        string envName = !string.IsNullOrEmpty(extractedRefinementEnvName)
            ? extractedRefinementEnvName
            : "API_KEY-latex-refinement";
        string refinementApiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
        return GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);
    }

    /// <summary>
    /// [AI Context] Holds the mutable state that ProcessPreparedVideoAsync's former single loop body
    /// threads across iterations (pending pre-upload/rate-limit tasks, accumulated output text and token
    /// totals) plus the per-video values TranscribeSegmentsAsync and FinalizeVideoOutputAsync both need.
    /// Extracted (Phase 4.5) purely so the loop and the finalization step could become separate methods
    /// without a 6+ parameter/ref-parameter list.
    /// [Human] Der pro Video gemeinsam genutzte, veränderliche Zustand für Transkription und Abschluss.
    /// </summary>
    private sealed class VideoProcessingState(string baseName, AudioTrackExtractor audioTrackExtractor) {
        public readonly string BaseName = baseName;
        public readonly AudioTrackExtractor AudioTrackExtractor = audioTrackExtractor;
        public readonly List<string> GeneratedTexFiles = [];
        public string FullOutputTextRaw = ""; // Stores text as is, no timestamp adjustment
        public string FullOutputTextOffsetted = ""; // Stores text with timestamps adjusted by partStartTimeSeconds
        public TokenUsage FileTotalTokens;
        public bool FileProcessingSuccess = true;
        public Task<SegmentUpload>? PendingVideoUploadTask;
        public Task<List<Part>>? PendingAudioUploadTask;
        public Task? RateLimitDelayTask;
        public Client? RefinementClient;
    }

    /// <summary>
    /// [AI Context] Sequentially extracts LaTeX for every part of one video (resume-from-disk caching,
    /// parallel pre-uploads, rate-limit pacing), mutating VideoProcessingState as it goes. Sets
    /// state.FileProcessingSuccess = false and returns early on the first unrecoverable part failure,
    /// mirroring the former inline loop's break semantics exactly.
    /// [Human] Extrahiert LaTeX Teil für Teil für ein Video.
    /// </summary>
    private async Task TranscribeSegmentsAsync(VideoProcessingState state, string file, string fileSpecificOutputFolder, IReadOnlyList<VideoSegment> partsWithTimes, double fullOriginalVideoDuration) {
        for (int i = 0; i < partsWithTimes.Count; i++) {
            string safePartPath = partsWithTimes[i].FilePath;
            double partStartTimeSeconds = partsWithTimes[i].StartTimeSeconds;
            string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-part{i + 1}.tex");

            Ui.Step($"Verarbeite Teil {i + 1}/{partsWithTimes.Count}: {Path.GetFileName(safePartPath)}");
            // Check if the .tex file already exists and is not older than 2 hours
            if (System.IO.File.Exists(targetPartPath) && (DateTime.Now - System.IO.File.GetLastWriteTime(targetPartPath)) <= ResumeWindow) {
                Ui.Info($"Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(targetPartPath)}. Überspringe API-Extraktion für diesen Teil.", "Resume");
                string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
                state.GeneratedTexFiles.Add(targetPartPath);
                state.FullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampAdjuster.ExtractContentWithoutTimestampHeader(existingTex); // For raw output
                if (_config.GenerateOffsetFiles) {
                    state.FullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampAdjuster.AdjustTimestamps(LatexTimestampAdjuster.ExtractContentWithoutTimestampHeader(existingTex), partStartTimeSeconds); // For offsetted output
                }
                state.AudioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);
                continue;
            }

            SegmentTranscript segmentTranscript;

            bool uploadSuccess;
            string? parsedPrompt;
            List<Part> attachmentParts;

            Task<SegmentUpload> uploadTask;

            if (_config.EnableParallelFileUploads && state.PendingVideoUploadTask != null) {
                Ui.Info($"Nutze im Hintergrund bereits hochgeladenes Video für Teil {i + 1}...", "Pre-Upload");
                uploadTask = state.PendingVideoUploadTask;
            }
            else {
                uploadTask = UploadSegmentAndBuildPromptAsync(safePartPath, i + 1, partsWithTimes.Count, file, fullOriginalVideoDuration);
            }

            SegmentUpload upload = await uploadTask;
            (uploadSuccess, parsedPrompt, attachmentParts) = (upload.Succeeded, upload.Prompt, upload.Attachments);
            if (!uploadSuccess) {
                Ui.Error($"Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                state.FileProcessingSuccess = false;
                break;
            }

            state.AudioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);

            if (state.RateLimitDelayTask != null) {
                Ui.Detail("Warte auf Freigabe des vorherigen Timers...", "Rate-Limit");
                await state.RateLimitDelayTask;
                state.RateLimitDelayTask = null;
            }

            // If EnableParallelFileUploads is enabled, start pre-uploading the next part (or the audio file if this is the last part) while Gemini processes the current part.
            if (_config.EnableParallelFileUploads) {
                if (i + 1 < partsWithTimes.Count) {
                    string nextTexPath = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-part{i + 2}.tex");
                    if (!System.IO.File.Exists(nextTexPath)) {
                        Ui.Info($"Starte parallelen Video-Upload für nächsten Teil ({i + 2}/{partsWithTimes.Count}) im Hintergrund...", "Pre-Upload");
                        state.PendingVideoUploadTask = UploadSegmentAndBuildPromptAsync(partsWithTimes[i + 1].FilePath, i + 2, partsWithTimes.Count, file, fullOriginalVideoDuration);
                    }
                    else {
                        state.PendingVideoUploadTask = null;
                    }
                }
                else if (i == partsWithTimes.Count - 1 && _config.GenerateAudioFile && _config.GoIntoLatexRefinement) {
                    state.PendingAudioUploadTask = Task.Run(async () => {
                        if (state.AudioTrackExtractor.PendingTask != null) {
                            await state.AudioTrackExtractor.PendingTask;
                        }
                        var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                        string audioPath = aacFiles.OrderByDescending(aacFile => System.IO.File.GetLastWriteTime(aacFile)).FirstOrDefault()
                                           ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");
                        if (System.IO.File.Exists(audioPath)) {
                            Ui.Info($"Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...", "Pre-Upload");
                            var handler = new AttachmentUploader(state.RefinementClient ?? _client, fileSpecificOutputFolder, [fileSpecificOutputFolder], true, "", null, false, _config.FileActivationDelaySeconds, _config.VideoUploadTimeoutSeconds, _config.VideoUploadMaxRetries);
                            var (audioUploadOk, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioPath}\"");
                            if (audioUploadOk) return attached;
                        }
                        return [];
                    });
                }
            }

            segmentTranscript = await TranscribeSegmentToLatexAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, state.GeneratedTexFiles);

            state.FileTotalTokens += segmentTranscript.Usage;

            if (i + 1 < partsWithTimes.Count) {
                state.RateLimitDelayTask = Task.Run(async () => {
                    int delay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130;
                    Ui.Detail($"Warte {delay} Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen...", "Timer");
                    await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Rate-Limits (Token Refill)...");
                });
            }
            int partFreshTokens = segmentTranscript.Usage.Fresh;

            if (!string.IsNullOrWhiteSpace(segmentTranscript.LatexBody)) {
                string cleanTex = LatexResponseCleaner.CleanLatexResponse(segmentTranscript.LatexBody);

                state.FullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {segmentTranscript.Usage.Input:N0}, Gecacht {segmentTranscript.Usage.Cached:N0}, Frisch/Video {partFreshTokens:N0}, Output {segmentTranscript.Usage.Output:N0}) ---\n" + cleanTex;
                if (_config.GenerateOffsetFiles) {
                    state.FullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {segmentTranscript.Usage.Input:N0}, Gecacht {segmentTranscript.Usage.Cached:N0}, Frisch/Video {partFreshTokens:N0}, Output {segmentTranscript.Usage.Output:N0}) ---\n" + LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds);
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
                    string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-part{i + 1}-offset.tex");
                    string uniqueTargetPartPathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPathOffset, partHeader + offsettedPartContent);
                    Ui.Success($"Offset-korrigierter Teil gespeichert unter: {Path.GetFileName(uniqueTargetPartPathOffset)}");
                }
                state.GeneratedTexFiles.Add(uniqueTargetPartPath);
            }
            else {
                Ui.Error($"Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
                state.FileProcessingSuccess = false;
                foreach (var failedTexFile in state.GeneratedTexFiles) {
                    try { System.IO.File.Delete(failedTexFile); } catch { /* Ignore */ }
                }
                if (Directory.Exists(fileSpecificOutputFolder) && !Directory.EnumerateFileSystemEntries(fileSpecificOutputFolder).Any()) {
                    Directory.Delete(fileSpecificOutputFolder);
                }
                break;
            }
        }
    }

    private async Task FinalizeVideoOutputAsync(VideoProcessingState state, string file, string fileSpecificOutputFolder, int totalParts) {
        string targetFilePath = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-all.tex");
        string targetFilePathOffset = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-all-offset.tex");

        string uniqueTargetFilePath = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePath);
        string header = TexDocumentWriter.BuildCombinedHeader(
            sourceFileName: Path.GetFileName(file),
            totalParts: totalParts,
            totalUsage: state.FileTotalTokens,
            model: _config.CurrentModel, temperature: _config.Temperature, topP: _config.TopP, topK: _config.TopK,
            maxOutputTokens: _config.MaxOutputTokens, thinkingBudget: _config.ThinkingBudget, thinkingLevel: _config.ThinkingLevel);
        await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, header + state.FullOutputTextRaw);
        Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}", "AutoExtraction");

        string refinementTargetFile = uniqueTargetFilePath;

        if (_config.GenerateOffsetFiles) {
            string uniqueTargetFilePathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePathOffset);
            await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, header + state.FullOutputTextOffsetted);
            Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}", "AutoExtraction");
            refinementTargetFile = uniqueTargetFilePathOffset;
        }

        if (state.AudioTrackExtractor.PendingTask != null) {
            Ui.Info($"Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...", "AutoExtraction");
            await state.AudioTrackExtractor.PendingTask;
        }

        List<Part>? preUploadedAudioParts = null;
        if (_config.EnableParallelFileUploads && state.PendingAudioUploadTask != null) {
            Ui.Info("Warte auf Abschluss des parallelen Audio-Uploads...", "AutoExtraction");
            preUploadedAudioParts = await state.PendingAudioUploadTask;
        }

        var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
        string audioFilePath = aacFiles.OrderByDescending(aacFile => System.IO.File.GetLastWriteTime(aacFile)).FirstOrDefault()
                               ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");

        Ui.Step($"Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
        var refinementSession = new LatexRefinementSession(
            state.RefinementClient ?? _client,
            RefinementOptions.ForFile(_latexRefinementConfig!, refinementTargetFile, _config, audioFilePath, preUploadedAudioParts));

        AttachmentUploader.HasJustUploaded = false;
        await refinementSession.StartAsync();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"-speed-[\d\.]+-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SpeedCompressedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CompressedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SegmentCompleteRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VideoCompleteRegex();
}