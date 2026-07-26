using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Globalization;
using System.Threading.Tasks; // Removed DirectChatAiInteraction as SessionLogger is now in Infrastructure
using Infrastructure;
using Google.GenAI;
using Google.GenAI.Types;
using Config; // Added for LatexRefinementConfig
using DocumentUtilities; // Added for LatexTimestampHelper

namespace AutoExtraction;

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline. 
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos. 
/// Schau bitte auch das entsprechende .json-File an!
/// </summary>
public partial class AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) {
    public static readonly string[] AvailableModels = [
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview",
        "gemini-2.5-flash"
    ];

    /// <summary>
    /// [AI Context] Cached content of dummy-part0.tex – a large (~4500 token) Lorem-Ipsum placeholder used
    /// as the first reference_context block in every request. Being big and constant, it anchors
    /// Google's implicit prefix cache on a stable, bit-identical prefix before the video payload.
    /// [Human] Inhalt von dummy-part0.tex: grosses Platzhalterdokument für konsistentes Prefix-Caching.
    /// </summary>
    private string? _dummyPart0Content;
    private string GetDummyPart0Content() {
        if (_dummyPart0Content != null) return _dummyPart0Content;
        string[] candidates = [
            Path.Combine(Directory.GetCurrentDirectory(), "dummy-part0.tex"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dummy-part0.tex")
        ];
        foreach (string path in candidates) {
            if (System.IO.File.Exists(path)) {
                _dummyPart0Content = System.IO.File.ReadAllText(path);
                Console.WriteLine($"  [Cache-Prefix] dummy-part0.tex geladen ({_dummyPart0Content.Length:N0} Bytes) aus: {path}");
                return _dummyPart0Content;
            }
        }
        Console.WriteLine("  [WARNUNG] dummy-part0.tex nicht gefunden – Dummy-Prefix ist leer. Cache-Hit für User-Part möglicherweise nicht möglich.");
        _dummyPart0Content = "% dummy-part0.tex not found";
        return _dummyPart0Content;
    }

    private Client _client = client;
    private readonly AiStudioAutoExtractionConfig _config = config;
    private readonly AttachmentHandler _attachmentHandler = attachmentHandler;
    private readonly SessionLogger _sessionLogger = sessionLogger;
    private readonly LatexRefinementSessionConfig _latexRefinementConfig = latexRefinementConfig;
    private double _speed = 1.0;
    private string _systemInstructionText = "";
    // [AI Context] Cached payloads to avoid redundant uploads and API calls across multiple video chunks.
    private readonly List<Part> _historyParts = [];
    // [AI Context] Stores the acknowledged history prompt and the model's confirmation, statically prepended to all subsequent API calls.
    private readonly List<Content> _sessionPreamble = [];
    private bool _historyWasLoaded = false;
    // [AI Context] Stateful history exclusively for the REPL loop's debug chat.
    private readonly List<Content> _debugChatHistory = [];
    private int _sessionTotalInputTokens = 0;
    private int _sessionTotalOutputTokens = 0;
    private int _sessionTotalCachedTokens = 0;
    private int _sessionMaxFreshTokens = 0;

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
    public async Task StartAsync() {
        if (!Directory.Exists(_config.SourceFolder)) {
            Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
            return;
        }

        // If no specific target folder is provided in config, create one inside the source folder.
        if (string.IsNullOrWhiteSpace(_config.TargetFolder)) {
            _config.TargetFolder = Path.Combine(_config.SourceFolder, "extracted_output");
        }

        if (!Directory.Exists(_config.TargetFolder)) {
            Directory.CreateDirectory(_config.TargetFolder);
        }

        Console.WriteLine("\n🚀 [AutoExtraction] Starte AI Studio Extraction Session...");
        Console.WriteLine($"  📁 Quelle (Source): {_config.SourceFolder}");
        Console.WriteLine($"  📁 Ziel (Target):   {_config.TargetFolder}");
        if (_config.ActiveApiProfile == 0) {
            Console.WriteLine("  🔑 API-Key:         Dedizierter Key für automatisierte Extraktion");
        }
        else {
            Console.WriteLine($"  🔑 API-Key:         Profil {_config.ActiveApiProfile} (API_KEY-ai-studio-test-project-{_config.ActiveApiProfile})");
        }

        string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
        foreach (var f in filesToProcess) {
            var dateInfo = VideoDateParser.Parse(f);
            if (!dateInfo.IsValid) {
                Console.WriteLine($"\n[WARNUNG] Video entspricht nicht dem Datums-/Wochen-Namensschema: {Path.GetFileName(f)}");
                Console.WriteLine("Erwartetes Format z.B.: 02-16-2026-monday-week1-Analysis_II.mp4 oder week1-02-16-2026-montag.mp4");
            }
        }

        await ReplLoopAsync();
    }

    /// <summary>
    /// [AI Context] Core initialization routine before batch processing. Loads system instructions and pre-warms the model context with attachments.
    /// [Human] Lädt die System-Instruktionen und die Historie hoch, bevor die eigentliche Video-Verarbeitung startet.
    /// </summary>
    private async Task SetupContextAndProcessAsync(string[] files) {
        if (files == null || files.Length == 0) {
            Console.WriteLine("Keine Dateien ausgewählt.");
            return;
        }

        if (!await EnsureSessionSetupAsync()) return;
        await ProcessFilesAsync(files);
    }

    private async Task<bool> EnsureSessionSetupAsync() {
        // --- Phase 1: Load System Instruction text from disk (if not already loaded) ---
        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (!await TryLoadSystemInstructionWithHistoryAsync()) return false;
        }

        // --- Phase 2: Load history as multi-turn preamble (if not handled via System Instruction above) ---
        if (!_historyWasLoaded) {
            await LoadHistoryAsMultiTurnPreambleAsync();
        }

        // --- Phase 3: Warm-up handshake for System Instruction without history ---
        if (!_historyWasLoaded && !string.IsNullOrWhiteSpace(_systemInstructionText)) {
            if (!await WarmUpSystemInstructionCacheAsync(includeDummyPart0: true)) return false;
        }

        // --- Phase 4: Finalize session setup (logging, debug roundtrip) ---
        ExtractionHelpers.LastGenerationCompletionTimeUtc = DateTime.UtcNow;
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
            if (!await DebugHelloRoundtripAsync()) return false;
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

        Console.WriteLine("\nFolgende System Instruction-Dateien sind konfiguriert:");
        var resolvedInstructionFiles = ExtractionHelpers.ResolveHistoryFiles(_config.SystemInstructionPaths);

        if (resolvedInstructionFiles.Count == 0) {
            Console.WriteLine("  [WARNUNG] Keine System Instruction-Dateien gefunden oder konfiguriert.");
            return true;
        }

        ExtractionHelpers.PrintFileTree(resolvedInstructionFiles);

        // Optionally resolve history files that will be merged into the system instruction
        List<string> historyFilesForSystemInstruction = [];
        bool shouldMergeHistory = _config.LoadHistoryIntoSystemInstruction && !_historyWasLoaded;
        if (shouldMergeHistory) {
            historyFilesForSystemInstruction = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (historyFilesForSystemInstruction.Count > 0) {
                Console.WriteLine("\nFolgende Dateien sind als History konfiguriert (werden aber direkt in die System Instruction geladen):");
                ExtractionHelpers.PrintFileTree(historyFilesForSystemInstruction);
            }
        }

        // Ask user for confirmation
        string confirmPrompt = shouldMergeHistory && historyFilesForSystemInstruction.Count > 0
            ? "System Instructions und History laden? (j/n): "
            : "System Instructions laden? (j/n): ";
        Console.Write(confirmPrompt);
        if (Console.ReadLine()?.Trim().ToLower() != "j") return true;

        // Determine common base for relative path display
        var allPathsForBaseResolution = new List<string>(resolvedInstructionFiles);
        if (shouldMergeHistory && historyFilesForSystemInstruction.Count > 0) {
            allPathsForBaseResolution.AddRange(historyFilesForSystemInstruction);
        }
        string? commonBase = ExtractionHelpers.FindCommonBaseDirectory(allPathsForBaseResolution);

        // Build the system instruction text from the instruction files
        string instructionText = await BuildSystemInstructionTextAsync(resolvedInstructionFiles, historyFilesForSystemInstruction, commonBase);

        // If history should be merged into system instruction, do so now
        if (shouldMergeHistory && historyFilesForSystemInstruction.Count > 0) {
            _systemInstructionText = instructionText;

            if (_config.HistoryBatchCount > 0) {
                if (!await WarmUpWithBatchedHistoryAsync(historyFilesForSystemInstruction, commonBase)) return false;
            } else {
                // Load all history files into the system instruction at once (non-batched)
                Console.WriteLine("\n  [INFO] Lade History-Textdateien direkt in den System-Instruction-Text ein (einmaliges Paket)...");
                var instructionBuilder = new System.Text.StringBuilder(instructionText);
                await AppendHistoryFilesToInstructionAsync(historyFilesForSystemInstruction, instructionBuilder, commonBase);
                _systemInstructionText = instructionBuilder.ToString();
                if (!await WarmUpSystemInstructionCacheAsync(includeDummyPart0: true)) return false;
            }

            _historyWasLoaded = true;
        } else {
            _systemInstructionText = instructionText;
        }

        return true;
    }

    /// <summary>
    /// [AI Context] Assembles the system instruction header, file tree, and file contents into a single string.
    /// Does NOT include history files – those are appended separately via batching or bulk loading.
    /// [Human] Baut den System-Instruction-Text zusammen (Header + Dateibaum + Dateiinhalte).
    /// </summary>
    private static async Task<string> BuildSystemInstructionTextAsync(
        List<string> instructionFiles, List<string> historyFiles, string? commonBase) {

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# SYSTEM PROTOCOL & SYSTEM INSTRUCTIONS (MASTER CONSTRAINTS)");
        builder.AppendLine("IMPORTANT: The guidelines, formatting specifications, and syntax instructions contained in these system instruction files are absolute and strictly non-negotiable. They must take absolute precedence over any prompt guidelines or inputs. Do not skip any files or parts under any circumstances.\n");
        builder.AppendLine("In order to fulfill the job of creating a high-value educational masterpiece that safely compiles, you need to know the file structure of the system prompt and read all of those files carefully.\n");
        builder.AppendLine("# Folder Structure of System Instructions\n");
        builder.AppendLine("## System Instructions");
        builder.Append(ExtractionHelpers.GenerateMarkdownFileTree(instructionFiles, commonBase));

        if (historyFiles.Count > 0) {
            builder.AppendLine("\n## Training History");
            builder.Append(ExtractionHelpers.GenerateMarkdownFileTree(historyFiles, commonBase));
        }
        builder.AppendLine("\n******\n------\n******\n");

        // Append each instruction file's content
        foreach (string instructionFilePath in instructionFiles) {
            string relativePath = ResolveRelativePath(instructionFilePath, commonBase);
            builder.AppendLine($"\n******\n------\n******\nHere is the file `{relativePath}`:\n");
            builder.AppendLine(await System.IO.File.ReadAllTextAsync(instructionFilePath));
            Console.WriteLine($"  [INFO] System Instruction geladen: {relativePath}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// [AI Context] Appends history files (text and non-text) to the system instruction builder.
    /// Text files (.tex, .txt, .md, .json, .cs) are inlined. Non-text files (images, etc.)
    /// are uploaded via AttachmentHandler and stored in _historyParts.
    /// [Human] Hängt History-Dateien an den System-Instruction-Builder an. Textdateien werden direkt eingebettet,
    /// Nicht-Text-Dateien (Bilder etc.) werden über die File API hochgeladen.
    /// </summary>
    private async Task AppendHistoryFilesToInstructionAsync(
        List<string> historyFiles, System.Text.StringBuilder targetBuilder, string? commonBase) {

        List<string> nonTextFiles = [];
        foreach (string historyFilePath in historyFiles) {
            string extension = Path.GetExtension(historyFilePath).ToLowerInvariant();
            if (extension is ".tex" or ".txt" or ".md" or ".json" or ".cs") {
                string relativePath = ResolveRelativePath(historyFilePath, commonBase);
                targetBuilder.AppendLine($"\n******\n------\n******\nHere is history reference file `{relativePath}`:\n");
                targetBuilder.AppendLine(await System.IO.File.ReadAllTextAsync(historyFilePath));
                Console.WriteLine($"  [INFO] History-Textdatei in System Instruction eingebunden: {relativePath}");
            } else {
                nonTextFiles.Add(historyFilePath);
            }
        }

        if (nonTextFiles.Count > 0) {
            string quotedFileList = string.Join(", ", nonTextFiles.Select(p => $"\"{p}\""));
            var (uploadSuccess, _, uploadedParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {quotedFileList}", true, commonBase);
            if (uploadSuccess && uploadedParts.Count > 0) {
                _historyParts.AddRange(uploadedParts);
            }
        }
    }

    /// <summary>
    /// [AI Context] Performs staged cache warming: splits history files into batches and sends
    /// incremental warm-up handshakes between each batch to pre-fill Google's implicit prefix cache.
    /// Each batch appends its text files to _systemInstructionText and uploads non-text files.
    /// [Human] Gestaffeltes Cache-Warming: History wird in Batches aufgeteilt, nach jedem Batch
    /// wird ein Handshake gesendet, um den Google-Cache schrittweise aufzubauen.
    /// </summary>
    private async Task<bool> WarmUpWithBatchedHistoryAsync(List<string> historyFiles, string? commonBase) {
        var batches = ExtractionHelpers.GroupHistoryFilesByTopLevelSubfolder(
            historyFiles, _config.HistoryPreloadPaths, _config.HistoryBatchCount);

        int systemInstructionDelay = _config.SystemInstructionDelaySeconds > 0 ? _config.SystemInstructionDelaySeconds : 65;
        int historyBatchDelay = _config.HistoryRateLimitDelaySeconds > 0 ? _config.HistoryRateLimitDelaySeconds : 65;

        Console.WriteLine($"\n  [SystemInstruction-Warmup] Starte gestaffeltes Cache-Warming für System Instruction + History in {batches.Count} Batch(es) (BaseDelay: {systemInstructionDelay}s, HistoryDelay: {historyBatchDelay}s)...");

        // Step 0: Optionally warm up base system instruction before adding history
        if (!_config.MergeSystemInstructionAndFirstHistoryBatch) {
            Console.WriteLine("\n  [Cache-Warming Step 0] Warmup für Basis System Instruction...");
            if (!await WarmUpSystemInstructionCacheAsync(systemInstructionDelay, includeDummyPart0: false)) return false;
        } else {
            Console.WriteLine($"\n  [Cache-Warming] Überspringe separaten Warmup & Wartezeit ({systemInstructionDelay}s) für Basis System Instruction (wird mit erstem Batch vereint)...");
        }

        // Process each history batch: append files, then warm up
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++) {
            var (batchLabel, batchFiles) = batches[batchIndex];
            bool isLastBatch = batchIndex == batches.Count - 1;

            Console.WriteLine($"\n  [Cache-Warming Step {batchIndex + 1}/{batches.Count}] Lade History-Batch '{batchLabel}' ({batchFiles.Count} Datei(en)) in System Instruction...");

            // Append this batch's files to the growing system instruction text
            var batchBuilder = new System.Text.StringBuilder();
            await AppendHistoryFilesToInstructionAsync(batchFiles, batchBuilder, commonBase);
            _systemInstructionText += batchBuilder.ToString();

            // Decide whether to send a handshake for this batch
            bool shouldSendHandshake = true;
            if (_config.MergeAllConsecutiveHistoryBatches && !isLastBatch && batchIndex % 2 == 1) {
                shouldSendHandshake = false;
            }

            if (shouldSendHandshake) {
                if (!await WarmUpSystemInstructionCacheAsync(historyBatchDelay, includeDummyPart0: isLastBatch)) return false;
            } else {
                Console.WriteLine($"  [Cache-Warming] Überspringe Handshake & Wartezeit ({historyBatchDelay}s) für Batch '{batchLabel}' (wird mit dem nächsten Batch vereint)...");
            }
        }

        Console.WriteLine($"\n  [Tokens] History-Warming abgeschlossen. Max-Frisch-Tokens in einem Schritt: {_sessionMaxFreshTokens:N0}");
        return true;
    }

    /// <summary>
    /// [AI Context] Loads history files as multi-turn preamble entries (not into system instruction).
    /// This is the alternative path used when LoadHistoryIntoSystemInstruction is false.
    /// Files are uploaded via AttachmentHandler and stored as acknowledged turns in _sessionPreamble.
    /// [Human] Lädt History-Dateien als Multi-Turn-Preamble (nicht in die System Instruction).
    /// </summary>
    private async Task LoadHistoryAsMultiTurnPreambleAsync() {
        var resolvedHistoryFiles = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);
        if (resolvedHistoryFiles.Count == 0) return;

        Console.WriteLine("\nFolgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
        ExtractionHelpers.PrintFileTree(resolvedHistoryFiles);

        string confirmPrompt = _config.LoadHistoryIntoSystemInstruction
            ? "Sollen diese Dateien als System Instructions hochgeladen werden? (LoadHistoryIntoSystemInstruction = true) (j/n): "
            : "Sollen diese Dateien als History geladen und für die Session hochgeladen werden? (j/n): ";
        Console.Write(confirmPrompt);
        if (Console.ReadLine()?.Trim().ToLower() != "j") return;

        if (_config.LoadHistoryIntoSystemInstruction) {
            Console.WriteLine("\n  [INFO] Lade Dateien als System Instructions hoch (dies kann einen Moment dauern)...");
            string quotedFileList = string.Join(", ", resolvedHistoryFiles.Select(p => $"\"{p}\""));
            var (uploadSuccess, _, uploadedParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {quotedFileList}", _config.LoadHistoryIntoSystemInstruction);
            if (uploadSuccess && uploadedParts.Count > 0) {
                _historyParts.AddRange(uploadedParts);
                _historyWasLoaded = true;
                Console.WriteLine("  [INFO] Dateien erfolgreich hochgeladen und werden in die System Instruction eingebunden.");
                await WarmUpSystemInstructionCacheAsync(includeDummyPart0: true);
            } else {
                Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
            }
            return;
        }

        // Non-system-instruction path: upload as multi-turn batches
        List<(string Label, List<string> Files)> historyBatches = _config.HistoryBatchCount > 1
            ? ExtractionHelpers.GroupHistoryFilesByTopLevelSubfolder(resolvedHistoryFiles, _config.HistoryPreloadPaths, _config.HistoryBatchCount)
            : [("(alle)", resolvedHistoryFiles)];

        if (_config.HistoryBatchCount > 1) {
            Console.WriteLine($"\n  [History-Batching] Aufgeteilt in {historyBatches.Count} Batch(es) (konfiguriert: {_config.HistoryBatchCount}).");
        }

        bool allBatchesSucceeded = true;
        for (int batchIndex = 0; batchIndex < historyBatches.Count; batchIndex++) {
            var (batchLabel, batchFiles) = historyBatches[batchIndex];
            bool isLastBatch = batchIndex == historyBatches.Count - 1;

            Console.WriteLine($"\n  [INFO] History-Batch {batchIndex + 1}/{historyBatches.Count}: '{batchLabel}' ({batchFiles.Count} Datei(en)) wird hochgeladen...");

            string quotedBatchFiles = string.Join(", ", batchFiles.Select(p => $"\"{p}\""));
            var (uploadSuccess, _, uploadedParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {quotedBatchFiles}", false);

            if (uploadSuccess && uploadedParts.Count > 0) {
                _historyParts.AddRange(uploadedParts);
                _historyWasLoaded = true;
                Console.WriteLine($"  [INFO] History-Batch {batchIndex + 1}/{historyBatches.Count} erfolgreich geladen ({uploadedParts.Count} Part(s)).");

                if (!isLastBatch) {
                    int interBatchDelay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 120;
                    Console.WriteLine($"  [Rate-Limit] Inter-Batch-Pause: {interBatchDelay}s vor nächstem Batch...");
                    await ExtractionHelpers.SmartDelayAsync(interBatchDelay, $"Warte zwischen Batch {batchIndex + 1} und {batchIndex + 2}...");
                }
            } else {
                Console.WriteLine($"  [FEHLER] Batch {batchIndex + 1}/{historyBatches.Count} konnte nicht hochgeladen werden.");
                allBatchesSucceeded = false;
                break;
            }
        }

        if (allBatchesSucceeded && _historyWasLoaded) {
            int tokenRefillDelay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130;
            Console.WriteLine($"  [Rate-Limit] Warte {tokenRefillDelay} Sekunden (Token Refill) nach History-Upload...");
            await ExtractionHelpers.SmartDelayAsync(tokenRefillDelay, "Warte auf Token-Refill nach History-Acknowledgment...");
        }
    }

    /// <summary>
    /// [AI Context] Resolves a file path relative to a common base directory for display purposes.
    /// [Human] Wandelt einen absoluten Pfad in einen relativen Pfad um (für Konsolenausgaben).
    /// </summary>
    private static string ResolveRelativePath(string filePath, string? commonBase) {
        string rawRelPath = !string.IsNullOrEmpty(commonBase)
            ? Path.GetRelativePath(commonBase, filePath)
            : Path.GetFileName(filePath);
        return ExtractionHelpers.NormalizeRelativePath(rawRelPath);
    }

    private async Task ProcessYouTubeTasksAsync() {
        List<YouTubeTranscriptionTask> tasksToProcess = [];

        if (_config.YouTubeTasks != null && _config.YouTubeTasks.Length > 0) {
            Console.WriteLine($"\n[YouTube Mode] Es wurden {_config.YouTubeTasks.Length} Aufgabe(n) in der Konfiguration gefunden.");
            Console.Write("Möchtest du diese ausführen (j/y) oder interaktiv eine neue YouTube-URL eingeben (u/url)? [Standard: j]: ");
            string choice = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
            if (choice == "u" || choice == "url" || choice == "n") {
                var interactiveTask = ExtractionHelpers.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
                if (interactiveTask != null) {
                    tasksToProcess.Add(interactiveTask);
                }
            }
            else {
                tasksToProcess.AddRange(_config.YouTubeTasks);
            }
        }
        else {
            Console.WriteLine("\n[YouTube Mode] Keine vorgegebenen YouTube-Aufgaben in der Konfiguration gefunden.");
            var interactiveTask = ExtractionHelpers.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
            if (interactiveTask != null) {
                tasksToProcess.Add(interactiveTask);
            }
        }

        if (tasksToProcess.Count == 0) {
            Console.WriteLine("[INFO] Keine YouTube-Aufgaben zum Verarbeiten.");
            return;
        }

        Console.WriteLine($"\n[YouTube Mode] Starte Transkription für {tasksToProcess.Count} YouTube-Video(s)...");

        if (!await EnsureSessionSetupAsync()) return;

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

            Console.WriteLine($"\n[YouTube Consumer] === Starte API-Extraktion für URL: {task.VideoUrl} ({baseName}) ===");
            List<string> generatedTexFiles = [];
            string fullOutputTextRaw = "";

            for (int i = 0; i < task.Fragments.Count; i++) {
                var frag = task.Fragments[i];
                int partNum = i + 1;
                Console.WriteLine($"\n--- Verarbeite Fragment {partNum}/{task.Fragments.Count}: {frag.StartTime} bis {frag.EndTime} ({frag.PartTitle}) ---");

                string dateNotice = (partNum == 1)
                    ? "Please note that since this is part 1 of the lecture, the date of the transcription is important."
                    : $"The lecture took place... Please note that since this is part {partNum} of the lecture, the date is not so important (but tell it anyway).";

                string parsedPrompt = $"Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n[IMPORTANT INSTRUCTION FOR YOUTUBE VIDEO]:\nThis is part {partNum} ('{frag.PartTitle}') of the lecture. Please focus ONLY on transcribing and extracting the chosen video fragment starting at timestamp {frag.StartTime} and ending at timestamp {frag.EndTime}.\n{dateNotice}";

                var attachmentParts = new List<Part> {
                    Part.FromUri(task.VideoUrl, "video/mp4")
                };

                var (texOutput, _, _, _) = await GenerateTexFromUploadedPartAsync(
                    task.VideoUrl, partNum, baseName, parsedPrompt, attachmentParts, generatedTexFiles
                );

                if (!string.IsNullOrWhiteSpace(texOutput)) {
                    string cleanTex = ExtractionHelpers.CleanLatexResponse(texOutput);
                    fullOutputTextRaw += $"\n\n% --- TEIL {partNum}: {frag.StartTime}-{frag.EndTime} ({frag.PartTitle}) ---\n" + cleanTex;

                    string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{partNum}.tex");
                    string partContent = cleanTex;
                    if (!partContent.StartsWith("% Startzeit:") && !partContent.StartsWith("% Zeitstempel:")) {
                        partContent = $"% Startzeit: {frag.StartTime} | Ende: {frag.EndTime}\n\n" + partContent;
                    }
                    await System.IO.File.WriteAllTextAsync(targetPartPath, partContent);
                    generatedTexFiles.Add(targetPartPath);
                    Console.WriteLine($"  [Erfolg] Teildatei gespeichert unter: {targetPartPath}");
                }
            }

            if (!string.IsNullOrWhiteSpace(fullOutputTextRaw)) {
                string combinedPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}.tex");
                await System.IO.File.WriteAllTextAsync(combinedPath, fullOutputTextRaw.Trim());
                Console.WriteLine($"\n🎉 Zusammengeführte YouTube-Transkription gespeichert unter: {combinedPath}");
            }
        }
    }

    /// <summary>
    /// [AI Context] Interactive control loop for the AutoExtraction mode. 
    /// Allows developers to dynamically adjust FFmpeg speeds, trigger specific files, or chat directly with the configured model for prompt debugging before launching a massive batch job.
    /// [Human] Eine interaktive Konsole, um vor dem großen Batch-Start Parameter (wie Video-Speed) zu testen oder den Prompt zu debuggen.
    /// </summary>
    private void PrintCommandsMenu() {
        Console.WriteLine("\n📋 Befehle:");
        Console.WriteLine("  1) 📜 Befehle anzeigen");
        Console.WriteLine("  2) ⚡ Video-Geschwindigkeit setzen (z.B. 'set speed 1.5' oder nur '2'). Standard: 1.2");
        Console.WriteLine("  3) 🎬 Einzelnes Video interaktiv auswählen und konvertieren");
        Console.WriteLine("  4) 🚀 Alle Videos im Quellordner konvertieren");
        Console.WriteLine("  5) 🚪 Beenden (exit/quit)");
        Console.WriteLine("  6) 📺 YouTube-Video transkribieren (per URL oder Config)");
        Console.WriteLine("  7) 🤖 Modell auswählen (aktuell: " + _config.CurrentModel + ")");
        Console.WriteLine("  8) 🔧 Latex Refinement interaktiv starten (Debugging)");
        Console.WriteLine("  9) 🔑 API-Key Profil wechseln (z.B. 'change-key 2', 0 für dediziert) (aktuell: " + (_config.ActiveApiProfile == 0 ? "dediziert" : $"Profil {_config.ActiveApiProfile}") + ")");
        Console.WriteLine("  (Alles andere wird als normaler Chat-Prompt zum Debuggen an Gemini gesendet)");
        Console.WriteLine("\n💡 Hinweis: Um System Instruction und History dauerhaft zu ändern, müssen die Dateien auf der Festplatte angepasst und das Programm neu gestartet werden.");
    }

    private async Task ReplLoopAsync() {
        PrintCommandsMenu();

        while (true) {
            if (!Console.IsInputRedirected) {
                while (Console.KeyAvailable) Console.ReadKey(intercept: true);
            }
            Console.Write("\nAutoExt> ");
            string input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(input)) continue;

            string normalizedInput = input.TrimStart('/');
            if (normalizedInput == "5" || normalizedInput.Equals("exit", StringComparison.OrdinalIgnoreCase) || normalizedInput.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

            if (normalizedInput == "1" || normalizedInput.Equals("show commands", StringComparison.OrdinalIgnoreCase)) {
                PrintCommandsMenu();
            }
            else if (normalizedInput == "2" || normalizedInput.StartsWith("2 ") || normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) {
                string speedInput = "";
                if (normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) speedInput = normalizedInput[9..].Trim();
                else if (normalizedInput.StartsWith("2 ")) speedInput = normalizedInput[2..].Trim();
                else if (normalizedInput == "2") {
                    Console.Write("Neuer Speed-Wert (z.B. 1.5): ");
                    speedInput = Console.ReadLine()?.Trim() ?? "";
                }

                if (double.TryParse(speedInput, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsedSpeed)) {
                    _speed = parsedSpeed;
                    Console.WriteLine($"Speed gesetzt auf {_speed}x");
                }
                else {
                    Console.WriteLine("Ungültiger Wert für speed.");
                }
            }
            else if (normalizedInput == "3" || normalizedInput.Equals("convert chosen video", StringComparison.OrdinalIgnoreCase)) {
                var files = FfmpegUtilities.ConsoleUiHelper.SelectSingleFile(_config.SourceFolder);
                if (files.Length > 0) {
                    await SetupContextAndProcessAsync(files);
                }
            }
            else if (normalizedInput == "4" || normalizedInput.Equals("convert all videos", StringComparison.OrdinalIgnoreCase)) {
                var files = ExtractionHelpers.SelectAndFilterVideosForBatch(_config.SourceFolder);
                if (files.Length > 0) {
                    await SetupContextAndProcessAsync(files);
                }
            }
            else if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                _debugChatHistory.Clear();
                Console.WriteLine("  [INFO] Debug-Chat Verlauf gelöscht.");
            }
            else if (normalizedInput == "6" || normalizedInput.Equals("youtube", StringComparison.OrdinalIgnoreCase)) {
                await ProcessYouTubeTasksAsync();
            }
            else if (normalizedInput == "7" || normalizedInput.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) {
                SelectModel();
                ConfigLoader<AiStudioAutoExtractionConfig>.Save(_config);
                ExtractionHelpers.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: false, _latexRefinementConfig);
                Console.WriteLine($"  [INFO] Modell für diese Session auf '{_config.CurrentModel}' gesetzt und für die gesamte Pipeline (AutoExtraction & LatexRefinement) in beiden JSON-Konfigurationen gespeichert.");
            }
            else if (normalizedInput == "8" || normalizedInput.Equals("run refinement", StringComparison.OrdinalIgnoreCase)) {
                await RefinementUiHelper.StartInteractiveRefinementAsync(_latexRefinementConfig, _config);
            }
            else if (normalizedInput == "9" || normalizedInput.StartsWith("9 ") || normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase) || normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) {
                string profileInput = "";
                if (normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase)) {
                    profileInput = normalizedInput["change-key".Length..].Trim();
                }
                else if (normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) {
                    profileInput = normalizedInput["change key".Length..].Trim();
                }
                else if (normalizedInput.StartsWith("9 ")) {
                    profileInput = normalizedInput[2..].Trim();
                }

                if (string.IsNullOrEmpty(profileInput)) {
                    Console.Write("Neues API-Key Profil (0-3): ");
                    profileInput = Console.ReadLine()?.Trim() ?? "";
                }

                if (int.TryParse(profileInput, out int newProfile) && newProfile >= 0 && newProfile <= 3) {
                    string? newApiKey;
                    if (newProfile == 0) {
                        newApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-automated-content-extraction");
                    }
                    else {
                        newApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKey(newProfile);
                    }

                    if (!string.IsNullOrEmpty(newApiKey)) {
                        _client = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(newApiKey);
                        _attachmentHandler.UpdateClient(_client);
                        _config.ActiveApiProfile = newProfile;
                        ConfigLoader<AiStudioAutoExtractionConfig>.Save(_config);
                        Console.WriteLine($"  [INFO] API-Key erfolgreich auf Profil {newProfile} gewechselt und in Konfiguration gespeichert!");
                    }
                }
                else {
                    Console.WriteLine("  [Fehler] Bitte eine gültige Profilnummer (0, 1, 2 oder 3) angeben.");
                }
            }
            else {
                await DebugChatAsync(input); // Chat erhält den originalen Input
            }
        }
    }

    /// <summary>
    /// [AI Context] Interactive model picker that reads models from _config.Model[] array in the configured order.
    /// The user's selection is persisted via CurrentModelIndex so it survives restarts.
    /// [Human] Das Startmenü in der Konsole. Modelle werden aus der JSON-Config gelesen – einfach dort die Liste anpassen.
    /// </summary>
    private void SelectModel() {
        string[] models = _config.Model;
        if (models.Length == 0) {
            Console.WriteLine("  [WARNUNG] Keine Modelle in der Konfiguration vorhanden.");
            return;
        }

        Console.WriteLine($"\n=== Model Selection (AI Studio) ===");
        Console.WriteLine("Wähle ein Modell:");
        for (int i = 0; i < models.Length; i++) {
            string marker = (i == _config.CurrentModelIndex) ? " [aktiv]" : "";
            Console.WriteLine($" {i + 1}) {models[i]}{marker}");
        }
        Console.Write($"Auswahl (1-{models.Length}) [Aktuell: {_config.CurrentModel}]: ");

        string choice = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrEmpty(choice)) return;

        if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= models.Length) {
            _config.CurrentModelIndex = idx - 1;
            ExtractionHelpers.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: false, _latexRefinementConfig);
        }
        else if (choice.Contains('-')) {
            // Freetext model name – find or append
            int found = Array.IndexOf(models, choice);
            if (found >= 0) {
                _config.CurrentModelIndex = found;
            }
            else {
                Console.WriteLine($"  [INFO] Modell '{choice}' nicht in der Liste gefunden. Auswahl unverändert.");
            }
            ExtractionHelpers.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: false, _latexRefinementConfig);
        }
    }

    /// <summary>
    /// [AI Context] A dedicated REPL chat for testing prompts against the model without initializing the full FFmpeg pipeline.
    /// Contains identical retry/backoff logic to the main extraction loop to accurately simulate API conditions.
    /// [Human] Der Debug-Chat. Hier kannst du mit der KI schreiben und testen, wie sie auf Prompts reagiert, bevor du hunderte Videos durchjagst.
    /// </summary>
    private async Task DebugChatAsync(string input) {
        _debugChatHistory.Add(new Content { Role = "user", Parts = [new() { Text = input }] });

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        if (SupportsThinking(_config.CurrentModel)) {
            bool isGemini25 = _config.CurrentModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
            }
            else if (_config.ThinkingBudget.HasValue) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
            }
        }

        Console.Write($"\n[Debug Chat] {_config.CurrentModel} (Strg+C zum Abbrechen): ");

        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
        Console.CancelKeyPress += cancelHandler;

        int maxRetries = 8;
        int backoff = 45;
        string fullResponse = "";
        bool exceptionCaught = false;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            fullResponse = "";
            bool isGenerating = true;
            var inputInterceptorTask = Task.Run(async () => {
                while (isGenerating) {
                    if (!ExtractionHelpers.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
                        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                        Console.WriteLine("\n[AI-Model] Still waiting for the acknowledgment / response. Please wait...");
                    }
                    await Task.Delay(100);
                }
            });

            try {
                if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");
                int requestInputTokens = 0;
                int requestOutputTokens = 0;
                int requestCachedTokens = 0;

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.CurrentModel, _debugChatHistory, requestConfig);
                await foreach (var chunk in responseStream.WithCancellation(cts.Token)) {
                    if (cts.IsCancellationRequested) break;
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Console.Write(txt);
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
                Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens:N0} | Output: {requestOutputTokens:N0} (inkl. Thinking Tokens)");
                Console.WriteLine($"  [Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {(Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens)):N0} | Output: {_sessionTotalOutputTokens:N0}");

                Console.WriteLine();
                isGenerating = false;
                await inputInterceptorTask;
                break; // Erfolg
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
                isGenerating = false;
                await inputInterceptorTask;
                exceptionCaught = true;
                break;
            }
            catch (Exception ex) {
                isGenerating = false;
                await inputInterceptorTask;

                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

                bool isOverloaded = ApiResilience.IsTransientError(ex);
                if (isOverloaded && attempt < maxRetries) {
                    // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
                    // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
                    // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
                    // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
                    int waitTime;
                    string contextMsg = " [Debug Chat]";
                    string delayMessage = "Still waiting for the acknowledgment / processing...";

                    if (ApiResilience.IsNetworkConnectionError(ex)) {
                        waitTime = 300; // 5 Minuten
                        Console.WriteLine($"\n[Netzwerk-Fehler]{contextMsg} Verbindung unterbrochen ({ex.GetType().Name}: {ex.Message}).");
                        Console.WriteLine($"  Keine Panik! Du hast jetzt 300 Sekunden (5 Minuten) Zeit, um deinen Hotspot oder deine Internetverbindung zu reparieren...");
                        Console.WriteLine($"  --> Sobald die Verbindung wieder steht, drücke ENTER, um sofort weiterzumachen! (Versuch {attempt + 1}/{maxRetries})");
                        delayMessage = "Warte auf Wiederherstellung der Internetverbindung / Hotspot...";
                    }
                    else if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
                        waitTime = 180; // 3 Minuten
                        Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        backoff = waitTime;
                    }
                    else if (attempt == 1) {
                        var retryMatch = MyRegex().Match(ex.Message);
                        if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                            waitTime = serverSuggestedDelay + 20;
                            Console.WriteLine($"\n[Rate Limit]{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        }
                        else {
                            waitTime = backoff;
                            Console.WriteLine($"\n[Rate Limit / Überlastung]{contextMsg} Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                        }
                        backoff = waitTime;
                    }
                    else {
                        backoff += 30;
                        waitTime = backoff;
                        Console.WriteLine($"\n[Rate Limit]{contextMsg} Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                    }
                    if (!await ExtractionHelpers.SmartDelayAsync(waitTime, delayMessage)) { exceptionCaught = true; break; }
                }
                else {
                    Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
                    // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
                    _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
                    break;
                }
            }
        }

        Console.CancelKeyPress -= cancelHandler;

        if (exceptionCaught || cts.IsCancellationRequested) {
            Console.WriteLine("\n\n[INFO] Debug-Chat durch Benutzer abgebrochen.");
        }
        else if (!string.IsNullOrWhiteSpace(fullResponse)) {
            _debugChatHistory.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
        }
        else if (_debugChatHistory.Count > 0 && _debugChatHistory.Last().Role == "user") {
            // Falls abgebrochen wurde, bevor die KI etwas gesagt hat, die User-Nachricht entfernen.
            _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
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
    /// [AI Context] Sends a lightweight handshake request containing the System Instruction to Google AI Studio.
    /// This warms up Google's implicit prefix cache and enforces a token refill delay
    /// before heavy video processing begins, preventing Quota Errors and ensuring high cache hits.
    /// [Human] Wärme-Handshake: Sendet ein kleines Signal an Google, damit die KI die System Instruction vorab in den impliziten Cache laedt.
    /// </summary>
    private async Task<bool> WarmUpSystemInstructionCacheAsync(int? customDelay = null, bool includeDummyPart0 = false) {
        Console.WriteLine("\n  [Cache-Warming] Starte initialen Handshake-Roundtrip, um die System Instruction bei Google im impliziten Cache zu aktivieren...");

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = 100
        };

        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        string handshakeText = $"[Cache-Warming Handshake] System instruction and instructions loaded. Please acknowledge with exactly: '[AI-Model: {_config.CurrentModel}] Handshake confirmed. Ready.'";

        // [AI Context] When DebugSendReferenceFile is enabled, the warm-up user-turn is split into TWO Parts:
        //   Part 0: contextText + dummyPart0 + GetStaticPromptBeginning(1)  ← IDENTICAL to Part 1's pre-video text Part
        //   Part 1: handshake instruction (throwaway, the response doesn't matter)
        // This Part boundary after Part 0 mirrors the boundary that exists in the real Part-1 request (between
        // the pre-video text and the video attachment), so Google's implicit prefix cache can match the full
        // SysInstruction + Part-0-text sequence and cache it before the first real video request arrives.
        // When SendDummyFileWithEachWarmUpRound is true, the dummy block is included in EVERY warm-up round
        // (regardless of includeDummyPart0 and DebugSendReferenceFile) to give Google a consistent user-turn
        // structure across all warm-up rounds, improving cache association.
        bool shouldIncludeDummy = (_config.DebugSendReferenceFile && includeDummyPart0) || _config.SendDummyFileWithEachWarmUpRound;
        List<Part> warmupParts;
        if (shouldIncludeDummy) {
            // [AI Context] dummy-part0.tex is ~4500 tokens of Lorem Ipsum – large enough to anchor Google's
            // implicit prefix cache on the user-turn portion even without relying solely on the system instruction.
            // This Part 0 is bit-identical to Part 1's pre-video text Part, ensuring maximum cache hits.
            string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{GetDummyPart0Content()}\n</reference_context>\n\n";

            // Part 0: pre-video prefix – token-identical to Part 1's first text Part.
            // Part 1: throwaway handshake – the response is irrelevant; only the cache priming matters.
            warmupParts = [
                new Part { Text = ReferenceContextPreamble + dummyReferenceBlock + GetStaticPromptBeginning(1) },
                new Part { Text = handshakeText }
            ];
        } else {
            warmupParts = [new Part { Text = handshakeText }];
        }

        var pingContent = new List<Content> {
            new() {
                Role = "user",
                Parts = warmupParts
            }
        };


        try {
            string responseText = "";
            int inputTokens = 0, outputTokens = 0, cachedTokens = 0;

            bool success = await ApiResilience.ExecuteStreamWithRetryAsync(
                streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, pingContent, requestConfig),
                onChunkReceived: async (chunk) => {
                    string txt = chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    responseText += txt;
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) inputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) outputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) cachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                    await Task.CompletedTask;
                },
                cancellationToken: CancellationToken.None,
                retryContext: "Cache-Warming Handshake"
            );

            if (success) {
                int freshTokens = Math.Max(0, inputTokens - cachedTokens);
                _sessionTotalInputTokens += inputTokens;
                _sessionTotalOutputTokens += outputTokens;
                _sessionTotalCachedTokens += cachedTokens;
                _sessionMaxFreshTokens = Math.Max(_sessionMaxFreshTokens, freshTokens);

                Console.WriteLine($"  [Cache-Warming] Handshake erfolgreich.");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Console.WriteLine($"  [Gemini Antwort] {responseText.Trim()}");
                }
                if (inputTokens > 0) {
                    Console.WriteLine($"  [Tokens] Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {freshTokens:N0} | Output: {outputTokens:N0}");
                }

                int delay = customDelay ?? (_config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130);
                Console.WriteLine($"  [Rate-Limit] Warte {delay} Sekunden (Token Refill)...");
                await ExtractionHelpers.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
                return true;
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"  [WARNUNG] Cache-Warming Handshake fehlgeschlagen: {ex.Message}. Fahre trotzdem fort.");
            int delay = customDelay ?? (_config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130);
            Console.WriteLine($"  [Rate-Limit] Warte {delay} Sekunden (Token Refill nach Handshake)...");
            await ExtractionHelpers.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
        }
        return true;
    }

    /// <summary>
    /// [AI Context] Sends a simple "Hello" debug roundtrip if enabled in config.
    /// [Human] Reiner Debug-Roundtrip, um zu testen ob die API antwortet.
    /// </summary>
    private async Task<bool> DebugHelloRoundtripAsync() {
        Console.WriteLine("\n  [Debug] Starte 'Hello' Roundtrip (DebugHelloRoundtrip = true)...");

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = 200
        };

        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        var debugContent = new List<Content> {
            new() {
                Role = "user",
                Parts = [new() { Text = "Hi, this is a debug roundtrip. Please reply with a short 'Hello' or 'Hi'." }]
            }
        };

        bool success = false;
        string fullResponse = "";
        int maxRetries = 3;
        int backoff = 10;
        int inputTokens = 0, outputTokens = 0, cachedTokens = 0;

        for (int attempt = 0; attempt < maxRetries; attempt++) {
            try {
                var response = await _client.Models.GenerateContentAsync(_config.CurrentModel, debugContent, requestConfig);
                fullResponse = response.Text ?? "";
                if (response.UsageMetadata != null) {
                    inputTokens = response.UsageMetadata.PromptTokenCount ?? 0;
                    outputTokens = response.UsageMetadata.CandidatesTokenCount ?? 0;
                    cachedTokens = response.UsageMetadata.CachedContentTokenCount ?? 0;
                    int freshTokens = Math.Max(0, inputTokens - cachedTokens);
                    _sessionTotalInputTokens += inputTokens;
                    _sessionTotalOutputTokens += outputTokens;
                    _sessionTotalCachedTokens += cachedTokens;
                    _sessionMaxFreshTokens = Math.Max(_sessionMaxFreshTokens, freshTokens);
                }

                Console.WriteLine($"  [Tokens] Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Frisch: {Math.Max(0, inputTokens - cachedTokens):N0} | Output: {outputTokens:N0}");
                Console.WriteLine($"  [Gemini Antwort] {fullResponse.Trim()}");
                success = true;
                break;
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                if (attempt < maxRetries - 1) {
                    Console.WriteLine($"[Debug] Retry in {backoff}s...");
                    await Task.Delay(backoff * 1000);
                    backoff += 10;
                }
            }
        }

        if (success) {
            _sessionPreamble.Add(debugContent[0]);
            _sessionPreamble.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
            int delay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 60;
            Console.WriteLine($"  [Rate-Limit] Warte {delay}s (Token Refill) nach Debug 'Hello' Roundtrip...");
            await ExtractionHelpers.SmartDelayAsync(delay, "Warte auf Token-Refill nach Debug Roundtrip...");
            return true;
        }
        else {
            Console.WriteLine("[FEHLER] Debug Roundtrip fehlgeschlagen.");
            return false;
        }
    }

    /// <summary>
    /// [AI Context] Executes the batch processing workflow.
    /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
    /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
    /// </summary>
    private async Task ProcessFilesAsync(string[] files) {
        // Chronologisch aufsteigend sortieren anhand des Dateinamens und der Woche
        files = [.. files.OrderBy(videoFile => VideoDateParser.Parse(videoFile).Date).ThenBy(videoFile => VideoDateParser.Parse(videoFile).WeekNumber ?? int.MaxValue).ThenBy(videoFile => videoFile)];

        // [AI Context] We use a bounded channel (capacity 1) to synchronize the FFmpeg Producer task and the Gemini Consumer task.
        // This allows FFmpeg to prepare the *next* video while Gemini is waiting for the API to process the *current* video, maximizing throughput.
        // [Human] Wir nutzen einen 'Kanal' (Channel), um FFmpeg (Videobearbeitung) und Gemini (KI-Analyse) parallel laufen zu lassen.
        // Während die KI das erste Video analysiert, schneidet FFmpeg im Hintergrund schon das zweite. Das spart enorm Zeit!
        var channel = Channel.CreateBounded<(string originalFile, string fileSpecificOutputFolder, string tmpFolderForFile, List<(string FilePath, double StartTime)> parts, bool isCached, double fullOriginalVideoDuration)>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
        var producerTask = Task.Run(async () => {
            foreach (var file in files) {
                string baseName = Path.GetFileNameWithoutExtension(file);
                baseName = SpeedCompressedRegex().Replace(baseName, "");
                baseName = CompressedRegex().Replace(baseName, "");
                // Create a file-specific output folder within the main target folder
                string fileSpecificOutputFolder = Path.Combine(_config.TargetFolder, baseName);
                if (!Directory.Exists(fileSpecificOutputFolder)) {
                    Directory.CreateDirectory(fileSpecificOutputFolder);
                }
                // Create a file-specific temporary folder inside the file-specific output folder
                string tmpFolderForFile = Path.Combine(fileSpecificOutputFolder, "tmp");
                if (!Directory.Exists(tmpFolderForFile)) {
                    Directory.CreateDirectory(tmpFolderForFile);
                }

                // Audio extraction was moved to the Consumer loop to run in parallel with API calls

                // Removed dateStr from filename pattern for caching to work across days for 2-hour window
                var cachedParts = Directory.GetFiles(tmpFolderForFile, $"{baseName}-part*.mp4").ToList();

                double fullOriginalVideoDuration = await FfmpegUtilities.FfmpegToolkit.GetVideoDurationAsync(file); // Get original video duration
                TimeSpan cacheDuration = TimeSpan.FromHours(48); // Set cache duration to 48 hours (2 days)
                bool useCache = false;

                if (cachedParts.Count > 0) {
                    var fileInfo = new FileInfo(cachedParts[0]);
                    if ((DateTime.Now - fileInfo.LastWriteTime) <= cacheDuration) {
                        // [AI Context] Defend against incomplete caches from interrupted FFmpeg runs.
                        // We expect exactly 3 parts. We also check if the files are actually valid (not 0 bytes)
                        // [Human] Wenn ein alter Lauf abgebrochen ist, liegen vielleicht nur 1-2 Teile im Cache, oder sie sind 0 Bytes groß. Das wird hier verhindert!
                        bool allFilesValid = true;
                        foreach (var cachedPartFile in cachedParts) {
                            if (new FileInfo(cachedPartFile).Length < 1024) { // less than 1KB is definitely invalid for a video
                                allFilesValid = false;
                                break;
                            }
                        }

                        if (cachedParts.Count >= _config.NumberOfParts && allFilesValid) {
                            useCache = true;
                        }
                        else {
                            Console.WriteLine($"\n  [Cache] Ignoriere unvollständigen oder defekten Cache für '{Path.GetFileName(file)}' ({cachedParts.Count} Teil(e), valid: {allFilesValid}). FFmpeg wird neu gestartet...");
                            foreach (var stalePartFile in cachedParts) { try { System.IO.File.Delete(stalePartFile); } catch { } }
                        }
                    }
                }

                if (useCache) {
                    Console.WriteLine($"\n[Cache] FFmpeg übersprungen für '{file}'. Verwende folgende gecachte Dateien (jünger als 48h):");
                    cachedParts.Sort();

                    // Determine the duration of the video that was actually split (either pre-compressed input or processed output)
                    double speedVideoDuration;
                    bool wasInputFilePreCompressedWhenCached = PreCompressedFileRegex().IsMatch(Path.GetFileName(file).ToLowerInvariant());

                    if (wasInputFilePreCompressedWhenCached) {
                        // If the input file was pre-compressed, its duration is what was effectively "processed" and split.
                        speedVideoDuration = await FfmpegUtilities.FfmpegToolkit.GetVideoDurationAsync(file);
                    }
                    else {
                        // Otherwise, it was the output of ProcessGeneralVideoAsync that was cached.
                        string expectedProcessedVideoPath = Path.Combine(tmpFolderForFile, $"{baseName}-speed-{_speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}-compressed.mp4");
                        speedVideoDuration = await FfmpegUtilities.FfmpegToolkit.GetVideoDurationAsync(expectedProcessedVideoPath);
                    }
                    double segmentLengthForCached = (speedVideoDuration > 0) ? (speedVideoDuration + (_config.NumberOfParts - 1) * _config.OverlapSeconds) / _config.NumberOfParts : 0;
                    var cachedPartsWithTimes = new List<(string FilePath, double StartTime)>();
                    for (int i = 0; i < cachedParts.Count; i++) {
                        double startTime = (segmentLengthForCached > 0 && i > 0) ? i * (segmentLengthForCached - _config.OverlapSeconds) : 0;
                        Console.WriteLine($"  - {cachedParts[i]} (Est. Start: {startTime.ToString("F2", CultureInfo.InvariantCulture)}s)");
                        cachedPartsWithTimes.Add((cachedParts[i], startTime));
                    }

                    await channel.Writer.WriteAsync((file, fileSpecificOutputFolder, tmpFolderForFile, cachedPartsWithTimes, true, fullOriginalVideoDuration));
                    continue;
                }

                // Determine if the file is already in a "compressed" format
                bool isPreCompressed = PreCompressedFileRegex().IsMatch(Path.GetFileName(file).ToLowerInvariant());

                string? videoToSplit;
                if (isPreCompressed) {
                    Console.WriteLine($"\n[FFmpeg Producer] {Path.GetFileName(file)} ist bereits als komprimiert markiert. Überspringe Vorverarbeitung, starte direkt Splitting...");
                    videoToSplit = file; // Use the original file directly for splitting
                }
                else {
                    Console.WriteLine($"\n[FFmpeg Producer] Starte Vorverarbeitung für {Path.GetFileName(file)} ({_speed}x Speed, 1 FPS, Mono)...");
                    videoToSplit = await FfmpegUtilities.FfmpegToolkit.ProcessGeneralVideoAsync(file, tmpFolderForFile, speedMultiplier: _speed, fps: 1, downmixToMono: true, scaleTo720p: false, overwrite: true, preset: _config.FfmpegPreset);
                    if (videoToSplit == null) {
                        Console.WriteLine($"  [FFmpeg Producer] Vorverarbeitung für {Path.GetFileName(file)} fehlgeschlagen. Überspringe Datei.");
                        continue;
                    }
                }

                Console.WriteLine($"\n[FFmpeg Producer] Starte Splitting für {Path.GetFileName(videoToSplit)} in {_config.NumberOfParts} Teile ({_config.OverlapSeconds}s Overlap)...");
                var rawPartsWithTimes = await FfmpegUtilities.FfmpegToolkit.ProcessSplitVideoAsync(videoToSplit, tmpFolderForFile, parts: _config.NumberOfParts, overlapSeconds: _config.OverlapSeconds, downmixToMono: false, streamCopy: true, overwrite: true, preset: _config.FfmpegPreset);

                if (rawPartsWithTimes.Count > 0) {
                    List<(string FilePath, double StartTime)> safePartsWithTimes = [];
                    for (int i = 0; i < rawPartsWithTimes.Count; i++) {
                        string safePartPath = Path.Combine(tmpFolderForFile, $"{baseName}-part{i + 1}.mp4");

                        if (!string.Equals(rawPartsWithTimes[i].FilePath, safePartPath, StringComparison.OrdinalIgnoreCase)) {
                            if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
                            System.IO.File.Move(rawPartsWithTimes[i].FilePath, safePartPath);
                        }

                        safePartsWithTimes.Add((safePartPath, rawPartsWithTimes[i].StartTime));
                    }
                    await channel.Writer.WriteAsync((file, fileSpecificOutputFolder, tmpFolderForFile, safePartsWithTimes, false, fullOriginalVideoDuration));
                }
            }
            channel.Writer.Complete(); // Signalisiert dem Fließband: "Feierabend, es kommen keine Videos mehr."
        });

        // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
        // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
        bool hasErrors = false;

        await foreach (var (file, fileSpecificOutputFolder, tmpFolderForFile, partsWithTimes, isCached, fullOriginalVideoDuration) in channel.Reader.ReadAllAsync()) {
            // Ensure the file-specific output folder exists before starting processing
            if (!Directory.Exists(fileSpecificOutputFolder)) {
                Directory.CreateDirectory(fileSpecificOutputFolder);
            }


            Console.WriteLine($"\n[Gemini Consumer] === Starte API-Extraktion für {Path.GetFileName(file)} ===");
            List<string> generatedTexFiles = [];
            string baseName = Path.GetFileNameWithoutExtension(file);
            baseName = SpeedCompressedRegex().Replace(baseName, "");
            baseName = CompressedRegex().Replace(baseName, "");
            if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
                baseName = "step1-" + baseName;
            }

            // [AI Context] The dummy-part0.tex reference block is now prepended directly from disk in
            // GenerateTexFromUploadedPartAsync. No part0 file needs to be written or tracked here.
            string fullOutputTextRaw = ""; // Stores text as is, no timestamp adjustment
            string fullOutputTextOffsetted = ""; // Stores text with timestamps adjusted by partStartTimeSeconds
            int fileTotalInputTokens = 0;
            int fileTotalOutputTokens = 0;
            int fileTotalCachedTokens = 0;
            bool fileProcessingSuccess = true;
            Task<(bool success, string? parsedPrompt, List<Part> attachmentParts)>? pendingVideoUploadTask = null;
            Task<List<Part>>? pendingAudioUploadTask = null;
            Task? rateLimitDelayTask = null;
            TimeSpan cacheDuration = TimeSpan.FromHours(2); // Define cache duration once

            // [AI Context] Initialize refinementClient early because the parallel audio upload task (pendingAudioUploadTask)
            // needs to upload the audio to the EXACT SAME Google Cloud Project / API Key that LatexRefinementSession will use.
            // Otherwise, LatexRefinementSession gets a ClientError: "You do not have permission to access the File".
            Client? refinementClient = null;
            if (_config.GoIntoLatexRefinement) {
                if (_latexRefinementConfig != null) {
                    _latexRefinementConfig.UseVertex = false;
                    if (_config.NumberOfParts <= 1 && _latexRefinementConfig.Step1MergeAndTimestamp != null) {
                        Console.WriteLine($"\n[AutoExtraction] NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.");
                        _latexRefinementConfig.Step1MergeAndTimestamp.Enabled = false;
                    }
                    if (_config.UseChosenModelForRestOfPipeline) {
                        Console.WriteLine($"\n[AutoExtraction] UseChosenModelForRestOfPipeline = true. Übernehme Modell '{_config.CurrentModel}' und Parameter im Arbeitsspeicher für das Refinement...");
                        void applyParams(Config.BackendParameters target) {
                            target.CurrentModel = _config.CurrentModel;
                            target.Temperature = _config.Temperature;
                            target.TopP = _config.TopP;
                            target.TopK = _config.TopK;
                            target.MaxOutputTokens = _config.MaxOutputTokens;
                            target.ThinkingBudget = _config.ThinkingBudget;
                            target.ThinkingLevel = _config.ThinkingLevel;
                        }
                        if (_latexRefinementConfig.Step1MergeAndTimestamp?.AiStudio != null) applyParams(_latexRefinementConfig.Step1MergeAndTimestamp.AiStudio);
                        if (_latexRefinementConfig.Step2SpeechRefinement?.AiStudio != null) applyParams(_latexRefinementConfig.Step2SpeechRefinement.AiStudio);
                        if (_latexRefinementConfig.Step3LastRefinement?.AiStudio != null) applyParams(_latexRefinementConfig.Step3LastRefinement.AiStudio);
                    }
                }
                string? extractedRefinementEnvName = (_latexRefinementConfig?.AiStudioApiKeyEnvNames != null && _latexRefinementConfig.AiStudioApiKeyEnvNames.Length > _latexRefinementConfig.AiStudioActiveApiProfile)
                    ? _latexRefinementConfig.AiStudioApiKeyEnvNames[_latexRefinementConfig.AiStudioActiveApiProfile]
                    : null;
                string envName = !string.IsNullOrEmpty(extractedRefinementEnvName)
                    ? extractedRefinementEnvName
                    : "API_KEY-latex-refinement";
                string refinementApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
                refinementClient = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);
            }

            // Handle Audio File Generation
            Task? audioExtractionTask = null;
            void startAudioTask() {
                if (_config.GenerateAudioFile && audioExtractionTask == null) {
                    string expectedAudioPath = Path.Combine(fileSpecificOutputFolder, $"{Path.GetFileNameWithoutExtension(file)}_audio.aac");
                    bool useCachedAudio = false;
                    if (System.IO.File.Exists(expectedAudioPath)) {
                        TimeSpan audioCacheDuration = TimeSpan.FromHours(48);
                        if ((DateTime.Now - System.IO.File.GetLastWriteTime(expectedAudioPath)) <= audioCacheDuration) {
                            useCachedAudio = true;
                        }
                    }
                    if (useCachedAudio) {
                        Console.WriteLine($"\n[Cache] Vorhandene Audio-Datei (jünger als 48h) gefunden: {Path.GetFileName(expectedAudioPath)}. Überspringe Audio-Extraktion.");
                    }
                    else {
                        audioExtractionTask = Task.Run(async () => {
                            Console.WriteLine($"\n[FFmpeg] Starte parallele Audio-Extraktion im Hintergrund für {Path.GetFileName(file)}...");
                            await FfmpegUtilities.FfmpegToolkit.ExtractAudioAsAacAsync(file, fileSpecificOutputFolder);
                            Console.WriteLine($"\n[FFmpeg] Audio-Extraktion für {Path.GetFileName(file)} abgeschlossen.");
                        });
                    }
                }
            }

            for (int i = 0; i < partsWithTimes.Count; i++) {
                string safePartPath = partsWithTimes[i].FilePath;
                double partStartTimeSeconds = partsWithTimes[i].StartTime;
                string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}.tex");

                Console.WriteLine($"\nVerarbeite Teil {i + 1}/{partsWithTimes.Count}: {Path.GetFileName(safePartPath)}");
                // Check if the .tex file already exists and is not older than 2 hours
                if (System.IO.File.Exists(targetPartPath) && (DateTime.Now - System.IO.File.GetLastWriteTime(targetPartPath)) <= cacheDuration) {
                    Console.WriteLine($"  [Resume] Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(targetPartPath)}. Überspringe API-Extraktion für diesen Teil.");
                    string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
                    generatedTexFiles.Add(targetPartPath);
                    fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampHelper.ExtractContentWithoutTimestampHeader(existingTex); // For raw output
                    if (_config.GenerateOffsetFiles) {
                        fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Aus Cache geladen) ---\n" + LatexTimestampHelper.AdjustTimestamps(LatexTimestampHelper.ExtractContentWithoutTimestampHeader(existingTex), partStartTimeSeconds); // For offsetted output
                    }
                    startAudioTask();
                    continue;
                }

                // [Human Context] declearing the variable `result` as a tuple.
                (string texOutput, int partInputTokens, int partOutputTokens, int partCachedTokens) result;

                bool uploadSuccess;
                string? parsedPrompt;
                List<Part> attachmentParts;

                Task<(bool success, string? parsedPrompt, List<Part> attachmentParts)> uploadTask;

                if (_config.EnableParallelFileUploads && pendingVideoUploadTask != null) {
                    Console.WriteLine($"  [Pre-Upload] Nutze im Hintergrund bereits hochgeladenes Video für Teil {i + 1}...");
                    uploadTask = pendingVideoUploadTask;
                }
                else {
                    uploadTask = PrepareAndUploadPartAsync(safePartPath, i + 1, partsWithTimes.Count, file, fullOriginalVideoDuration);
                }

                (uploadSuccess, parsedPrompt, attachmentParts) = await uploadTask;
                if (!uploadSuccess) {
                    Console.WriteLine($"  [Fehler] Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                    fileProcessingSuccess = false;
                    hasErrors = true;
                    break;
                }

                startAudioTask();

                if (rateLimitDelayTask != null) {
                    Console.WriteLine("  [Rate-Limit] Warte auf Freigabe des vorherigen Timers...");
                    await rateLimitDelayTask;
                    rateLimitDelayTask = null;
                }

                // If EnableParallelFileUploads is enabled, start pre-uploading the next part (or the audio file if this is the last part) while Gemini processes the current part.
                if (_config.EnableParallelFileUploads) {
                    if (i + 1 < partsWithTimes.Count) {
                        string nextTexPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 2}.tex");
                        if (!System.IO.File.Exists(nextTexPath)) {
                            Console.WriteLine($"  [Pre-Upload] Starte parallelen Video-Upload für nächsten Teil ({i + 2}/{partsWithTimes.Count}) im Hintergrund...");
                            pendingVideoUploadTask = PrepareAndUploadPartAsync(partsWithTimes[i + 1].FilePath, i + 2, partsWithTimes.Count, file, fullOriginalVideoDuration);
                        }
                        else {
                            pendingVideoUploadTask = null;
                        }
                    }
                    else if (i == partsWithTimes.Count - 1 && _config.GenerateAudioFile && _config.GoIntoLatexRefinement) {
                        pendingAudioUploadTask = Task.Run(async () => {
                            if (audioExtractionTask != null) {
                                await audioExtractionTask;
                            }
                            var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                            string audioPath = aacFiles.OrderByDescending(aacFile => System.IO.File.GetLastWriteTime(aacFile)).FirstOrDefault()
                                               ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");
                            if (System.IO.File.Exists(audioPath)) {
                                Console.WriteLine($"\n  [Pre-Upload] Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...");
                                var handler = new AttachmentHandler(refinementClient ?? _client, fileSpecificOutputFolder, [fileSpecificOutputFolder], true, "", null, false, _config.FileActivationDelaySeconds, _config.VideoUploadTimeoutSeconds, _config.VideoUploadMaxRetries);
                                var (audioUploadOk, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioPath}\"");
                                if (audioUploadOk) return attached;
                            }
                            return [];
                        });
                    }
                }

                /************************************************************************************************************************
                 * [Human Context] Here is where all the magic happens. We call the function   GenerateTexFromUploadedPartAsync to generate the LaTeX code for the current part. 
                 * This function is the core of the program and is responsible for generating the LaTeX code for the current part.
                 ************************************************************************************************************************/

                result = await GenerateTexFromUploadedPartAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles); // generated TexFiles could contain part0 for instance.

                fileTotalInputTokens += result.partInputTokens;
                fileTotalOutputTokens += result.partOutputTokens;
                fileTotalCachedTokens += result.partCachedTokens;

                if (i + 1 < partsWithTimes.Count) {
                    rateLimitDelayTask = Task.Run(async () => {
                        int delay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130;
                        // [AI Context] A delay is enforced here to accommodate strictly-enforced tokens-per-minute (TPM) and requests-per-minute (RPM) quotas by the API provider.
                        // [Human] Wir warten hier, da wir ein hartes Limit von Tokens pro Minute haben. Das stellt sicher, dass das Limit vor dem nächsten Aufruf wieder zurückgesetzt ist.
                        Console.WriteLine($"\n  [Timer] Warte {delay} Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
                        await ExtractionHelpers.SmartDelayAsync(delay, "Warte auf Rate-Limits (Token Refill)...");
                    });
                }
                int partFreshTokens = Math.Max(0, result.partInputTokens - result.partCachedTokens);

                if (!string.IsNullOrWhiteSpace(result.texOutput)) {
                    string cleanTex = ExtractionHelpers.CleanLatexResponse(result.texOutput);

                    // Store the raw output for the combined file without offset
                    fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {result.partInputTokens:N0}, Gecacht {result.partCachedTokens:N0}, Frisch/Video {partFreshTokens:N0}, Output {result.partOutputTokens:N0}) ---\n" + cleanTex;
                    if (_config.GenerateOffsetFiles) {
                        fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {result.partInputTokens:N0}, Gecacht {result.partCachedTokens:N0}, Frisch/Video {partFreshTokens:N0}, Output {result.partOutputTokens:N0}) ---\n" + LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds); // Accumulate offsetted text for new parts
                    }

                    // Prepend the start time to the individual part .tex file
                    string partHeader = BuildTexPartHeader(
                        sourcePartFileName: Path.GetFileName(safePartPath),
                        partStartTimeSeconds: partStartTimeSeconds,
                        inputTokens: result.partInputTokens,
                        cachedTokens: result.partCachedTokens,
                        freshTokens: partFreshTokens,
                        outputTokens: result.partOutputTokens);
                    string uniqueTargetPartPath = GetUniqueTexPath(targetPartPath);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + cleanTex);

                    if (_config.GenerateOffsetFiles) {
                        // NEW: Save the offsetted version of this individual part
                        string offsettedPartContent = LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                        string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}-offset.tex");
                        string uniqueTargetPartPathOffset = GetUniqueTexPath(targetPartPathOffset);
                        await System.IO.File.WriteAllTextAsync(uniqueTargetPartPathOffset, partHeader + offsettedPartContent);
                        Console.WriteLine($"  [Erfolg] Offset-korrigierter Teil gespeichert unter: {Path.GetFileName(uniqueTargetPartPathOffset)}");
                    }
                    generatedTexFiles.Add(uniqueTargetPartPath);
                }
                else {
                    Console.WriteLine($"\n[FEHLER] Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
                    fileProcessingSuccess = false;
                    hasErrors = true;
                    // Clean up individual part files if processing failed mid-way
                    foreach (var failedTexFile in generatedTexFiles) {
                        try { System.IO.File.Delete(failedTexFile); } catch { /* Ignore */ }
                    }
                    // Try to delete the file-specific output folder if it's empty or contains only temporary stuff
                    if (Directory.Exists(fileSpecificOutputFolder) && !Directory.EnumerateFileSystemEntries(fileSpecificOutputFolder).Any()) {
                        Directory.Delete(fileSpecificOutputFolder);
                    }
                    break;
                }
            }

            if (fileProcessingSuccess) {
                string targetFilePath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-all.tex");
                string targetFilePathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-all-offset.tex");

                int fileTotalFreshTokens = Math.Max(0, fileTotalInputTokens - fileTotalCachedTokens);
                string uniqueTargetFilePath = GetUniqueTexPath(targetFilePath);
                string header = BuildTexCombinedHeader(
                    sourceFileName: Path.GetFileName(file),
                    totalParts: partsWithTimes.Count,
                    totalInputTokens: fileTotalInputTokens,
                    totalCachedTokens: fileTotalCachedTokens,
                    totalFreshTokens: fileTotalFreshTokens,
                    totalOutputTokens: fileTotalOutputTokens);
                await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, header + fullOutputTextRaw);
                Console.WriteLine($"\n[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}");

                string refinementTargetFile = uniqueTargetFilePath;

                if (_config.GenerateOffsetFiles) {
                    // New: Generate the offset version
                    // Note: The last part's StartTime is used as a reference point, but the overall offset should be partStartTimeSeconds from the respective part.
                    // We already accumulated the correctly offsetted text in fullOutputTextOffsetted within the loop.
                    string uniqueTargetFilePathOffset = GetUniqueTexPath(targetFilePathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, header + fullOutputTextOffsetted);
                    Console.WriteLine($"[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}");
                    refinementTargetFile = uniqueTargetFilePathOffset;
                }

                // Trigger LatexRefinementSession immediately for the generated offset file, if enabled.
                // Warten, bis das Audio fertig ist, bevor das Refinement startet,
                // da das Refinement die Audiodatei für die API benötigt!
                if (audioExtractionTask != null) {
                    Console.WriteLine($"\n[AutoExtraction] Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...");
                    await audioExtractionTask;
                }

                // LatexRefinementSession uses its own dedicated API key (resolved at the start of the processing loop)
                // refinementClient is already initialized and the audio file was uploaded using it.

                List<Part>? preUploadedAudioParts = null;
                if (_config.EnableParallelFileUploads && pendingAudioUploadTask != null) {
                    Console.WriteLine($"\n[AutoExtraction] Warte auf Abschluss des parallelen Audio-Uploads...");
                    preUploadedAudioParts = await pendingAudioUploadTask;
                }

                // Check for the most recent audio file by looking at modified times, or simply look for the exact name.
                // Since ExtractAudioAsAacAsync might create -copy-1 if it exists, let's just grab the newest .aac file in the folder.
                var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                string audioFilePath = aacFiles.OrderByDescending(aacFile => System.IO.File.GetLastWriteTime(aacFile)).FirstOrDefault()
                                       ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");

                Console.WriteLine($"\n[AutoExtraction] Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
                // Pass the AI Studio client for refinement, as VertexAutoExtractionSession requires an AI Studio client for this
                var refinementSession = new DirectChatAiInteraction.LatexRefinementSession(
                    refinementClient ?? _client,
                    _latexRefinementConfig!,
                    refinementTargetFile,
                    _config,
                    audioFilePath,
                    preUploadedAudioParts);

                AttachmentHandler.HasJustUploaded = false;
                await refinementSession.StartAsync();
            }
        }

        // Warten, bis der Producer-Task sauber beendet wurde (fängt Fehler ab)
        await producerTask;

        if (hasErrors) {
            Console.WriteLine("\n[AutoExtraction] Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).");
        }
        else {
            Console.WriteLine("\n[AutoExtraction] Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!");
        }
    }

    private static string GetUniqueTexPath(string originalPath) {
        if (!System.IO.File.Exists(originalPath)) {
            return originalPath;
        }

        Console.WriteLine($"  [Hinweis] Zieldatei '{Path.GetFileName(originalPath)}' existiert bereits.");
        string dir = Path.GetDirectoryName(originalPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(originalPath);
        string ext = Path.GetExtension(originalPath);
        int copyIndex = 1;
        string newPath;
        do {
            newPath = Path.Combine(dir, $"{baseName}-copy-{copyIndex}{ext}");
            copyIndex++;
        } while (System.IO.File.Exists(newPath));

        Console.WriteLine($"  [Info] Neue Datei wird erstellt: '{Path.GetFileName(newPath)}'");
        return newPath;
    }

    /// <summary>
    /// [AI Context] Builds the metadata header for an individual .tex part file.
    /// Includes model parameters, timestamp offset, and per-part token usage statistics.
    /// [Human] Baut den Metadaten-Header für eine einzelne .tex-Teildatei.
    /// </summary>
    private string BuildTexPartHeader(string sourcePartFileName, double partStartTimeSeconds,
        int inputTokens, int cachedTokens, int freshTokens, int outputTokens) {
        return $"% ==========================================\n" +
               $"% AutoExtraction Source Part: {sourcePartFileName}\n" +
               BuildTexModelParameterBlock() +
               $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
               $"% PART_START_SECONDS: {partStartTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}\n" +
               $"% ------------------------------------------\n" +
               $"% Token Usage Analysis (Google GenAI):\n" +
               $"%   - Total Prompt Tokens : {inputTokens:N0} (Gesamtumfang des Aufmerksamkeitshorizonts)\n" +
               $"%   - Cached Context      : {cachedTokens:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
               $"%   - Fresh Input Tokens  : {freshTokens:N0} (Echter neuer Payload: Video-Segment + Prompt)\n" +
               $"%   - Generated Output    : {outputTokens:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
               $"% ==========================================\n\n";
    }

    /// <summary>
    /// [AI Context] Builds the metadata header for the combined (-all) .tex file.
    /// Includes model parameters and aggregated token usage across all parts.
    /// [Human] Baut den Metadaten-Header für die zusammengeführte (-all) .tex-Datei.
    /// </summary>
    private string BuildTexCombinedHeader(string sourceFileName, int totalParts,
        int totalInputTokens, int totalCachedTokens, int totalFreshTokens, int totalOutputTokens) {
        return $"% ==========================================\n" +
               $"% AutoExtraction Combined Source: {sourceFileName}\n" +
               BuildTexModelParameterBlock() +
               $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
               $"% ------------------------------------------\n" +
               $"% Token Usage Summary across {totalParts} Part(s):\n" +
               $"%   - Total Prompt Tokens : {totalInputTokens:N0} (Summe aller Prompts über alle Teile)\n" +
               $"%   - Cached Context      : {totalCachedTokens:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
               $"%   - Fresh Input Tokens  : {totalFreshTokens:N0} (Echter neuer Payload für alle Video-Teile)\n" +
               $"%   - Total Output Tokens : {totalOutputTokens:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
               $"% ==========================================\n\n";
    }

    /// <summary>
    /// [AI Context] Builds the common model parameter block used in all .tex headers.
    /// [Human] Gemeinsamer Block mit Modell-Parametern für alle .tex-Header.
    /// </summary>
    private string BuildTexModelParameterBlock() {
        return $"% Model: {_config.CurrentModel}\n" +
               $"% Temperature: {_config.Temperature}\n" +
               $"% TopP: {_config.TopP}\n" +
               $"% TopK: {_config.TopK}\n" +
               $"% MaxOutputTokens: {_config.MaxOutputTokens}\n" +
               (_config.ThinkingBudget.HasValue ? $"% ThinkingBudget: {_config.ThinkingBudget.Value}\n" : "") +
               (!string.IsNullOrEmpty(_config.ThinkingLevel) ? $"% ThinkingLevel: {_config.ThinkingLevel}\n" : "");
    }

    private async Task<(bool success, string? parsedPrompt, List<Part> attachmentParts)> PrepareAndUploadPartAsync(string partFile, int partNumber, int totalParts, string originalFileName, double fullOriginalVideoDuration) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        double partDurationSeconds = await FfmpegUtilities.FfmpegToolkit.GetVideoDurationAsync(partFile);
        TimeSpan partDuration = TimeSpan.FromSeconds(partDurationSeconds);
        string durationString = string.Format("{0:D2} minutes and {1:D2} seconds", partDuration.Minutes, partDuration.Seconds);

        TimeSpan fullVideoTime = TimeSpan.FromSeconds(fullOriginalVideoDuration);
        string fullDurationString = string.Format("{0:D2} minutes and {1:D2} seconds", fullVideoTime.Minutes, fullVideoTime.Seconds);

        // Dynamic parameters only – the static prompt beginning (GetStaticPromptBeginning) is
        // prepended as a separate Part BEFORE the video in GenerateTexFromUploadedPartAsync.
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
        if (!uploadSuccess || attachmentParts.Count == 0) return (false, null, []);

        return (true, parsedPrompt, attachmentParts);
    }

    /// <summary>
    /// [AI Context] Executes the Gemini API generation call for a single video segment.
    /// To guarantee optimal implicit prefix cache hits across multi-part extractions, prompt parts are assembled in strict prefix-stable order:
    /// 1. Read-only reference context (previousTexFiles) inlined first BEFORE the video to form a growing shared prefix across parts.
    /// 2. Primary payload (attachmentParts / video) second.
    /// 3. Segment-specific parameters third.
    /// [Human] Generiert den LaTeX-Code für ein bestimmtes Videosegment. Hält die Prompt-Reihenfolge strikt ein, damit Googles impliziter Cache optimal greift.
    /// </summary>
    private async Task<(string texOutput, int inputTokens, int outputTokens, int cachedTokens)> GenerateTexFromUploadedPartAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        // Create System Instruction at the very beginning of the request setup
        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        if (SupportsThinking(_config.CurrentModel)) {
            bool isGemini25 = _config.CurrentModel.Contains("2.5", StringComparison.OrdinalIgnoreCase);
            if (!isGemini25 && !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingLevel = _config.ThinkingLevel };
            }
            else if (_config.ThinkingBudget.HasValue) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = _config.ThinkingBudget };
            }
        }

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        var userPromptParts = new List<Part>();

        // 1. Pre-video Part: static prompt beginning, optionally prefixed by reference context files.
        //    This Part is bit-identical between the warm-up dummy turn and the real Part-1 turn, enabling
        //    Google's implicit prefix cache to recognise the full prefix up to the start of the video.
        string staticBeginning = GetStaticPromptBeginning(partNumber);
        if (_config.DebugSendReferenceFile) {
            // [AI Context] Always prepend dummy-part0.tex first. This creates a constant, large (~4500 token)
            // anchor that is bit-identical between the warm-up Part 0 and Part 1's Part 0, enabling Google's
            // implicit prefix cache to hit on the preamble + dummyBlock + staticBeginning for Part 1.
            // For Part 2+, the dummy block is still the first reference, followed by the previously
            // generated .tex parts – these grow with each part but the prefix still benefits from caching.
            string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{GetDummyPart0Content()}\n</reference_context>\n\n";

            var referenceContextBuilder = new System.Text.StringBuilder(ReferenceContextPreamble);
            referenceContextBuilder.Append(dummyReferenceBlock);

            if (previousTexFiles.Count > 0) {
                Console.WriteLine("  [Kontext] Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:");
                foreach (var previousTexFile in previousTexFiles) {
                    string previousTexFileName = Path.GetFileName(previousTexFile);
                    Console.WriteLine($"    - {previousTexFileName}");
                    string previousTexContent = await System.IO.File.ReadAllTextAsync(previousTexFile);
                    referenceContextBuilder.Append($"<reference_context file=\"{previousTexFileName}\">\n{previousTexContent}\n</reference_context>\n\n");
                }
            }

            userPromptParts.Add(new Part { Text = referenceContextBuilder.ToString() + staticBeginning });
        } else {
            userPromptParts.Add(new Part { Text = staticBeginning });
        }

        // 2. Primary payload (video attachment) – the dynamic video bytes break the prefix here.
        userPromptParts.AddRange(attachmentParts);

        // 3. Dynamic parameters only AFTER the video.
        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        var history = new List<Content>();
        history.AddRange(_sessionPreamble);
        history.Add(new Content { Role = "user", Parts = userPromptParts });

        string fullResponse = "";
        int currentRequest = 1;
        int maxRequestsPerPart = 6;
        int interactionInputTokens = 0;
        int interactionOutputTokens = 0;
        int interactionCachedTokens = 0;

        string logContext = $"[Part {partNumber}] {Path.GetFileName(originalFileName)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
        if (previousTexFiles.Count > 0) {
            logContext += $"\n[Kontext-Dateien]: {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
        }
        logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";
        string currentLogPrompt = logContext;

        try {
            Console.WriteLine("\n  [Token-Analyse] Berechne Token-Anzahl für die einzelnen Bestandteile...");
            var videoContents = new List<Content> { new() { Role = "user", Parts = attachmentParts } };
            var videoCount = await _client.Models.CountTokensAsync(_config.CurrentModel, videoContents);
            Console.WriteLine($"    - Video-Token: {videoCount.TotalTokens}");

            if (_config.DebugSendReferenceFile && userPromptParts.Count > 0 && !string.IsNullOrEmpty(userPromptParts[0].Text)) {
                var texContents = new List<Content> { new() { Role = "user", Parts = [userPromptParts[0]] } };
                var texCount = await _client.Models.CountTokensAsync(_config.CurrentModel, texContents);
                string fileInfo = previousTexFiles.Count > 0
                    ? $"dummy-part0.tex + {previousTexFiles.Count} Datei(en): {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}"
                    : "dummy-part0.tex";
                Console.WriteLine($"    - Inlined Kontext ({fileInfo}) Token: {texCount.TotalTokens}");
            }

            var totalCount = await _client.Models.CountTokensAsync(_config.CurrentModel, history);
            Console.WriteLine($"    -> Gesamt-Token in History (Video + Kontext + Prompt): {totalCount.TotalTokens}\n");
        }
        catch (Exception ex) {
            Console.WriteLine($"  [Token-Analyse] Fehler beim Zählen der Token: {ex.Message}\n");
        }

        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
        Console.CancelKeyPress += cancelHandler;

        while (true) {
            Console.WriteLine($"  [API] Sende Anfrage für Part {partNumber} an Google AI Studio ({_config.CurrentModel}) (Request {currentRequest}/{maxRequestsPerPart})...");
            GroundingMetadata? accumulatedGrounding = null;
            string chunkResp = "";
            int requestInputTokens = 0;
            int requestOutputTokens = 0;
            int requestCachedTokens = 0;
            bool callSuccess = false;

            try {
                callSuccess = await ApiResilience.ExecuteStreamWithRetryAsync(
                    streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, history, requestConfig),
                    onChunkReceived: async (chunk) => {
                        string txt = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                        Console.Write(txt);
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
                Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Fahre mit nächstem Teil fort.");
                Console.WriteLine($"Finaler Fehler: {ex.Message}");
                break;
            }

            if (accumulatedGrounding != null) {
                Console.WriteLine("\n\n  🔍 [Google Search Grounding] Quellen:");
                if (accumulatedGrounding.WebSearchQueries != null && accumulatedGrounding.WebSearchQueries.Count > 0) {
                    Console.WriteLine($"    Suchanfragen: {string.Join(", ", accumulatedGrounding.WebSearchQueries.Select(q => $"\"{q}\""))}");
                }
                if (accumulatedGrounding.GroundingChunks != null) {
                    int refIdx = 1;
                    foreach (var chunkRef in accumulatedGrounding.GroundingChunks) {
                        if (chunkRef.Web != null) {
                            Console.WriteLine($"     [{refIdx}] {chunkRef.Web.Title} - {chunkRef.Web.Uri}");
                            refIdx++;
                        }
                    }
                }
            }

            if (!callSuccess) {
                Console.WriteLine("\n\n[INFO] Generierung durch Benutzer abgebrochen oder fehlgeschlagen.");
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

            Console.WriteLine($"\n  [Request Tokens] Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {freshReqTokens:N0} | Output: {requestOutputTokens:N0} (inkl. Thinking Tokens)");
            Console.WriteLine($"  [Part Total Tokens] Total Prompt: {interactionInputTokens:N0} | Gecacht: {interactionCachedTokens:N0} | Frisch: {freshPartTokens:N0} | Output: {interactionOutputTokens:N0} (inkl. Thinking Tokens)");
            Console.WriteLine($"  [Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {freshSessTokens:N0} | Output: {_sessionTotalOutputTokens:N0}");

            fullResponse += chunkResp;
            await _sessionLogger.LogChatAsync(currentLogPrompt, currentLogPrompt, _config.CurrentModel, chunkResp, "AutoExtraction", requestInputTokens, requestOutputTokens, requestCachedTokens);

            bool segmentComplete = SegmentCompleteRegex().IsMatch(chunkResp);
            bool videoComplete = VideoCompleteRegex().IsMatch(chunkResp);

            if (videoComplete) break;

            if (currentRequest >= maxRequestsPerPart) {
                Console.WriteLine($"\n\n[WARNUNG] Maximale Anzahl an Requests ({maxRequestsPerPart}) für diesen Teil erreicht. Breche ab.\n  Teil: {partFile}");
                break;
            }

            string continuePrompt = segmentComplete ? "Continue" :
                $"[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
                $"{(chunkResp.Length > 300 ? "...\n" + chunkResp[^300..] : chunkResp)}\n\n" +
                "Please \"continue\" exactly where you left off. Do not open a new ```latex block if you were already inside one, just continue the text directly.";

            if (segmentComplete) Console.WriteLine("\n  [AutoExtraction] Segment-Limit erreicht. Sende 'Continue'...");
            else Console.WriteLine("\n  [AutoExtraction] Unerwartetes Ende der Antwort. Bereite automatisierten 'Continue'-Prompt vor...");

            Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

            history.Add(new Content { Role = "model", Parts = [new() { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new() { Text = continuePrompt }] });
            currentLogPrompt = $"[Continue Prompt für Part {partNumber}]:\n{continuePrompt}";

            int delay = _config.VideoPartDelaySeconds > 0 ? _config.VideoPartDelaySeconds : 130;
            // [AI Context] A delay is enforced here to accommodate strictly-enforced tokens-per-minute (TPM) and requests-per-minute (RPM) quotas by the API provider.
            // [Human] Wir warten hier, da wir ein hartes Limit von Tokens pro Minute haben. Das stellt sicher, dass das Limit vor dem nächsten Aufruf wieder zurückgesetzt ist.
            Console.WriteLine($"\n  [Timer] Warte {delay} Sekunden vor der Fortsetzung, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
            if (!await ExtractionHelpers.SmartDelayAsync(delay, "Warte auf Rate-Limits (Token Refill)...")) {
                Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
                break;
            }

            currentRequest++;
        }

        Console.CancelKeyPress -= cancelHandler;
        AttachmentHandler.HasJustUploaded = false;
        return (fullResponse, interactionInputTokens, interactionOutputTokens, interactionCachedTokens);
    }

    /// <summary>
    /// [AI Context] Determines whether a Gemini model supports thinking parameters (`ThinkingConfig`, `HIGH`/`LOW` levels, `ThinkingBudget`).
    /// [Human] Prüft, ob das gewählte KI-Modell die erweiterten Denk-Parameter (Thinking Level/Budget) unterstützt.
    /// </summary>
    private static bool SupportsThinking(string modelName) {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        return modelName.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) ||
               modelName.Contains("thinking", StringComparison.OrdinalIgnoreCase);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)s""")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-speed-[\d\.]+-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SpeedCompressedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CompressedRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PreCompressedFileRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SegmentCompleteRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VideoCompleteRegex();
}