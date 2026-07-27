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
using LectureExtraction.App;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;
using LectureExtraction.Refinement;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline. 
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos. 
/// Schau bitte auch das entsprechende .json-File an!
/// </summary>
/// <remarks>
/// Note: This class is 'partial' because it uses the [GeneratedRegex] attribute 
/// at the bottom of the file for compile-time regex generation (SYSLIB1045).
/// </remarks>
public partial class VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentHandler attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) {
    public static readonly string[] AvailableModels = [
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview"
    ];

    private readonly Client _client = client;
    private readonly VertexAutoExtractionConfig _config = config;
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
    // [AI Context] Active Google Cloud Context Cache resource name if caching is active.
    private string? _cachedContentName = null;

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

        Console.WriteLine("\n🚀 [AutoExtraction] Starte Vertex AI Extraction Session...");
        Console.WriteLine($"  📁 Quelle (Source): {_config.SourceFolder}");
        Console.WriteLine($"  📁 Ziel (Target):   {_config.TargetFolder}");
        if (!string.IsNullOrWhiteSpace(_config.ProjectId)) {
            Console.WriteLine($"  ☁️  API-Projekt:     {_config.ProjectId} ({_config.Location})");
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
                Console.WriteLine("\nFolgende System Instruction-Dateien sind konfiguriert:");

                // Resolve all files from configured paths, handling directories
                var resolvedInstructionFiles = HistoryFileResolver.ResolveHistoryFiles(_config.SystemInstructionPaths);

                if (resolvedInstructionFiles.Count > 0) {
                    FileTreeRenderer.PrintFileTree(resolvedInstructionFiles);
                    List<string> distinctHistoryFiles = [];
                    if (_config.LoadHistoryIntoSystemInstruction && !_historyWasLoaded) {
                        distinctHistoryFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
                        if (distinctHistoryFiles.Count > 0) {
                            Console.WriteLine("\nFolgende Dateien sind als History konfiguriert (werden aber direkt in die System Instruction geladen):");
                            FileTreeRenderer.PrintFileTree(distinctHistoryFiles);
                        }
                    }

                    string promptText = _config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0
                        ? "System Instructions und History laden? (j/n): "
                        : "System Instructions laden? (j/n): ";
                    Console.Write(promptText);

                    if (Console.ReadLine()?.Trim().ToLower() == "j") {
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
                            Console.WriteLine($"  [INFO] System Instruction geladen: {relPath}");
                        }
                        _systemInstructionText = instructionBuilder.ToString();

                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            Console.WriteLine("\n  [INFO] Lade History-Dateien für System Instruction ein...");
                            string fileList = string.Join(", ", distinctHistoryFiles.Select(p => $"\"{p}\""));
                            var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}", true, commonBase);
                            if (success && attachmentParts.Count > 0) {
                                _historyParts.AddRange(attachmentParts);
                                _historyWasLoaded = true;
                                Console.WriteLine("  [INFO] Dateien erfolgreich eingelesen und in die System Instruction eingebunden.");
                            }
                            else {
                                Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht eingelesen werden.");
                            }
                        }
                    }
                }
                else {
                    Console.WriteLine("  [WARNUNG] Keine System Instruction-Dateien gefunden oder konfiguriert.");
                }
            }
        }

        if (!_historyWasLoaded) {
            var distinctFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (distinctFiles.Count > 0) {
                Console.WriteLine("\nFolgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
                FileTreeRenderer.PrintFileTree(distinctFiles);
                if (_config.LoadHistoryIntoSystemInstruction) {
                    Console.Write("Sollen diese Dateien als System Instructions hochgeladen werden? (LoadHistoryIntoSystemInstruction = true) (j/n): ");
                }
                else {
                    Console.Write("Sollen diese Dateien als History geladen und für die Session hochgeladen werden? (j/n): ");
                }

                if (Console.ReadLine()?.Trim().ToLower() == "j") {
                    if (_config.LoadHistoryIntoSystemInstruction) {
                        Console.WriteLine("\n  [INFO] Lade Dateien als System Instructions hoch (dies kann einen Moment dauern)...");
                    }
                    else {
                        Console.WriteLine("\n  [INFO] Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
                    }
                    string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
                    var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}", _config.LoadHistoryIntoSystemInstruction);
                    if (success && attachmentParts.Count > 0) {
                        _historyParts.AddRange(attachmentParts);
                        _historyWasLoaded = true;
                        if (_config.LoadHistoryIntoSystemInstruction) {
                            Console.WriteLine("  [INFO] Dateien erfolgreich hochgeladen und werden in die System Instruction eingebunden (Acknowledge wird übersprungen).");
                        }
                        else {
                            Console.WriteLine("  [INFO] History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
                            if (!await AcknowledgeHistoryAsync(fileList)) return false;
                        }
                    }
                    else {
                        Console.WriteLine("  [FEHLER] Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
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
        return true;
    }

    private async Task ProcessYouTubeTasksAsync() {
        List<YouTubeTranscriptionTask> tasksToProcess = [];

        if (_config.YouTubeTasks != null && _config.YouTubeTasks.Length > 0) {
            Console.WriteLine($"\n[YouTube Mode] Es wurden {_config.YouTubeTasks.Length} Aufgabe(n) in der Konfiguration gefunden.");
            Console.Write("Möchtest du diese ausführen (j/y) oder interaktiv eine neue YouTube-URL eingeben (u/url)? [Standard: j]: ");
            string choice = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
            if (choice == "u" || choice == "url" || choice == "n") {
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
            Console.WriteLine("\n[YouTube Mode] Keine vorgegebenen YouTube-Aufgaben in der Konfiguration gefunden.");
            var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
            if (interactiveTask != null) {
                tasksToProcess.Add(interactiveTask);
            }
        }

        if (tasksToProcess.Count == 0) {
            Console.WriteLine("[INFO] Keine YouTube-Aufgaben zum Verarbeiten.");
            return;
        }

        Console.WriteLine($"\n[YouTube Mode] Starte Transkription für {tasksToProcess.Count} YouTube-Video(s)...");

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

                    string texOutput = (await GenerateTexFromUploadedPartAsync(
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
                Console.WriteLine("  [INFO] Context Caching wurde in Konfiguration deaktiviert. Lösche aktiven Cache bei Google...");
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
            Console.WriteLine($"  [INFO] Nutze bestehenden Google Kontext-Cache: {_cachedContentName} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})");
            return;
        }

        if (!string.IsNullOrEmpty(savedState.CacheName)) {
            await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
        }

        Console.WriteLine("  [INFO] Erstelle neuen Kontext-Cache bei Google (dies kann einen Moment dauern)...");
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
                Console.WriteLine($"  [INFO] Google Kontext-Cache erfolgreich angelegt: {_cachedContentName} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})");
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"  [FEHLER] Konnte Kontext-Cache nicht erstellen: {ex.GetType().Name} - {ex.Message}. Falle auf normalen Upload zurück.");
            _cachedContentName = null;
        }
    }

    /// <summary>
    /// [AI Context] Interactive UI to adjust context caching defaults and persist them to json.
    /// [Human] Interaktives Menü, um Caching-Dauer und Verlängerungsintervall anzupassen.
    /// </summary>
    private void ConfigureCachingSettings() {
        Console.WriteLine("\n⚙️ Aktuelle Context Caching Einstellungen:");
        Console.WriteLine($"  UseContextCaching:              {_config.UseContextCaching}");
        Console.WriteLine($"  ContextCachingMinutes:          {_config.ContextCachingMinutes} min");
        Console.WriteLine($"  ContextCachingIncrementMinutes: {_config.ContextCachingIncrementMinutes} min");
        Console.Write("\nContext Caching aktivieren? (j/n oder Enter für keine Änderung): ");
        string? toggle = Console.ReadLine()?.Trim().ToLower();
        if (toggle == "j") _config.UseContextCaching = true;
        else if (toggle == "n") _config.UseContextCaching = false;

        Console.Write($"Neue Standarddauer in Minuten (aktuell {_config.ContextCachingMinutes}): ");
        string? durInput = Console.ReadLine()?.Trim();
        if (int.TryParse(durInput, out int d) && d > 0) _config.ContextCachingMinutes = d;

        Console.Write($"Neues Verlängerungsintervall in Minuten (aktuell {_config.ContextCachingIncrementMinutes}): ");
        string? incInput = Console.ReadLine()?.Trim();
        if (int.TryParse(incInput, out int inc) && inc > 0) _config.ContextCachingIncrementMinutes = inc;

        ConfigLoader<VertexAutoExtractionConfig>.Save(_config);
        Console.WriteLine("  [INFO] Einstellungen in VertexAutoExtractionConfig.json gespeichert.");
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
        Console.WriteLine($"  9) ⏳ Context Caching verlängern (+{_config.ContextCachingIncrementMinutes} min Standard)");
        Console.WriteLine("  10) 🐷 Context Caching beenden (Save Money! Geld sparen)");
        Console.WriteLine("  11) ⚙️ Standardwerte für Context Caching ändern");
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
                string val = "";
                if (normalizedInput.StartsWith("set speed", StringComparison.OrdinalIgnoreCase)) val = normalizedInput[9..].Trim();
                else if (normalizedInput.StartsWith("2 ")) val = normalizedInput[2..].Trim();
                else if (normalizedInput == "2") {
                    Console.Write("Neuer Speed-Wert (z.B. 1.5): ");
                    val = Console.ReadLine()?.Trim() ?? "";
                }

                if (double.TryParse(val, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s)) {
                    _speed = s;
                    Console.WriteLine($"Speed gesetzt auf {_speed}x");
                }
                else {
                    Console.WriteLine("Ungültiger Wert für speed.");
                }
            }
            else if (normalizedInput == "3" || normalizedInput.Equals("convert chosen video", StringComparison.OrdinalIgnoreCase)) {
                var files = FileSelectionPrompt.SelectSingleFile(_config.SourceFolder);
                if (files.Length > 0) {
                    await SetupContextAndProcessAsync(files);
                }
            }
            else if (normalizedInput == "4" || normalizedInput.Equals("convert all videos", StringComparison.OrdinalIgnoreCase)) {
                var files = VideoBatchSelector.SelectAndFilterVideosForBatch(_config.SourceFolder);
                if (files.Length > 0) {
                    await SetupContextAndProcessAsync(files);
                }
            }
            else if (normalizedInput == "6" || normalizedInput.Equals("youtube", StringComparison.OrdinalIgnoreCase)) {
                await ProcessYouTubeTasksAsync();
            }
            else if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                _debugChatHistory.Clear();
                Console.WriteLine("  [INFO] Debug-Chat Verlauf gelöscht.");
            }
            else if (normalizedInput == "7" || normalizedInput.StartsWith("set model", StringComparison.OrdinalIgnoreCase)) {
                SelectModel();
                ConfigLoader<VertexAutoExtractionConfig>.Save(_config);
                ModelSyncService.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: true, _latexRefinementConfig);
                Console.WriteLine($"  [INFO] Modell für diese Session auf '{_config.CurrentModel}' gesetzt und für die gesamte Pipeline (AutoExtraction & LatexRefinement) in beiden JSON-Konfigurationen gespeichert.");
            }
            else if (normalizedInput == "8" || normalizedInput.Equals("run refinement", StringComparison.OrdinalIgnoreCase)) {
                if (_latexRefinementConfig != null) {
                    _latexRefinementConfig.UseVertex = Program.Activate_Vertex;
                }
                await RefinementUiHelper.StartInteractiveRefinementAsync(_latexRefinementConfig!, _config);
            }
            else if (normalizedInput == "9" || normalizedInput.Equals("prolong cache", StringComparison.OrdinalIgnoreCase)) {
                bool extendedAny = false;

                // 1) Vertex Main Extraction Cache
                string? mainCacheName = _cachedContentName;
                if (string.IsNullOrEmpty(mainCacheName)) {
                    var mainState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
                    mainCacheName = mainState.CacheName;
                }

                if (!string.IsNullOrEmpty(mainCacheName)) {
                    var savedState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
                    var updated = await ContextCacheStateManager.ExtendCacheAsync(_client, savedState, _config.ContextCachingIncrementMinutes, ContextCacheStateManager.StateFileVertex);
                    if (updated != null) {
                        Console.WriteLine($"  [INFO] Video-Extraktions Kontext-Cache '{mainCacheName}' verlängert um {_config.ContextCachingIncrementMinutes} Minuten (Neu gültig bis {updated.ExpireTimeUtc.ToLocalTime():t}).");
                        _cachedContentName = mainCacheName;
                        extendedAny = true;
                    }
                }

                // 2) LaTeX Schritt 1 Cache
                var step1State = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileLatexStep1);
                if (!string.IsNullOrEmpty(step1State.CacheName)) {
                    int incMin = _latexRefinementConfig?.Step1MergeAndTimestamp?.Vertex?.ContextCachingIncrementMinutes ?? 30;
                    var updated = await ContextCacheStateManager.ExtendCacheAsync(_client, step1State, incMin, ContextCacheStateManager.StateFileLatexStep1);
                    if (updated != null) {
                        Console.WriteLine($"  [INFO] LaTeX Schritt 1 Kontext-Cache '{step1State.CacheName}' verlängert um {incMin} Minuten (Neu gültig bis {updated.ExpireTimeUtc.ToLocalTime():t}).");
                        extendedAny = true;
                    }
                }

                // 3) LaTeX Schritt 2 Cache
                var step2State = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileLatexStep2);
                if (!string.IsNullOrEmpty(step2State.CacheName)) {
                    int incMin = _latexRefinementConfig?.Step2SpeechRefinement?.Vertex?.ContextCachingIncrementMinutes ?? 30;
                    var updated = await ContextCacheStateManager.ExtendCacheAsync(_client, step2State, incMin, ContextCacheStateManager.StateFileLatexStep2);
                    if (updated != null) {
                        Console.WriteLine($"  [INFO] LaTeX Schritt 2 Kontext-Cache '{step2State.CacheName}' verlängert um {incMin} Minuten (Neu gültig bis {updated.ExpireTimeUtc.ToLocalTime():t}).");
                        extendedAny = true;
                    }
                }

                // 4) LaTeX Schritt 3 Cache
                var step3State = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileLatexStep3);
                if (!string.IsNullOrEmpty(step3State.CacheName)) {
                    int incMin = _latexRefinementConfig?.Step3LastRefinement?.Vertex?.ContextCachingIncrementMinutes ?? 30;
                    var updated = await ContextCacheStateManager.ExtendCacheAsync(_client, step3State, incMin, ContextCacheStateManager.StateFileLatexStep3);
                    if (updated != null) {
                        Console.WriteLine($"  [INFO] LaTeX Schritt 3 Kontext-Cache '{step3State.CacheName}' verlängert um {incMin} Minuten (Neu gültig bis {updated.ExpireTimeUtc.ToLocalTime():t}).");
                        extendedAny = true;
                    }
                }

                if (!extendedAny) {
                    Console.WriteLine("  [WARNUNG] Es sind aktuell keine aktiven Google Kontext-Caches vorhanden, die verlängert werden können.");
                }
            }
            else if (normalizedInput == "10" || normalizedInput.Equals("stop cache", StringComparison.OrdinalIgnoreCase)) {
                bool clearedAny = false;

                // 1) Vertex Main Extraction Cache
                string? mainCacheName = _cachedContentName;
                if (string.IsNullOrEmpty(mainCacheName)) {
                    var mainState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
                    mainCacheName = mainState.CacheName;
                }

                if (!string.IsNullOrEmpty(mainCacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, mainCacheName);
                    ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
                    _cachedContentName = null;
                    Console.WriteLine("  [INFO] 🐷 Video-Extraktions Kontext-Cache vorzeitig beendet und bei Google gelöscht.");
                    clearedAny = true;
                }

                // 2) LaTeX Schritt 1 Cache
                var step1State = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileLatexStep1);
                if (!string.IsNullOrEmpty(step1State.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, step1State.CacheName);
                    ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileLatexStep1);
                    Console.WriteLine("  [INFO] 🐷 LaTeX Schritt 1 Kontext-Cache vorzeitig beendet und bei Google gelöscht.");
                    clearedAny = true;
                }

                // 3) LaTeX Schritt 2 Cache
                var step2State = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileLatexStep2);
                if (!string.IsNullOrEmpty(step2State.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, step2State.CacheName);
                    ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileLatexStep2);
                    Console.WriteLine("  [INFO] 🐷 LaTeX Schritt 2 Kontext-Cache vorzeitig beendet und bei Google gelöscht.");
                    clearedAny = true;
                }

                // 4) LaTeX Schritt 3 Cache
                var step3State = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileLatexStep3);
                if (!string.IsNullOrEmpty(step3State.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, step3State.CacheName);
                    ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileLatexStep3);
                    Console.WriteLine("  [INFO] 🐷 LaTeX Schritt 3 Kontext-Cache vorzeitig beendet und bei Google gelöscht.");
                    clearedAny = true;
                }

                if (!clearedAny) {
                    Console.WriteLine("  [WARNUNG] Es sind aktuell keine aktiven Google Kontext-Caches vorhanden, die gelöscht werden können.");
                }
            }
            else if (normalizedInput == "11" || normalizedInput.Equals("config cache", StringComparison.OrdinalIgnoreCase)) {
                ConfigureCachingSettings();
            }
            else if (normalizedInput.Equals("clear", StringComparison.OrdinalIgnoreCase)) {
                _debugChatHistory.Clear();
                Console.WriteLine("  [INFO] Debug-Chat Verlauf gelöscht.");
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

        Console.WriteLine($"\n=== Model Selection (Vertex AI) ===");
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
            ModelSyncService.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: true, _latexRefinementConfig);
        }
        else if (choice.Contains('-')) {
            int found = Array.IndexOf(models, choice);
            if (found >= 0) {
                _config.CurrentModelIndex = found;
            }
            else {
                Console.WriteLine($"  [INFO] Modell '{choice}' nicht in der Liste gefunden. Auswahl unverändert.");
            }
            ModelSyncService.SyncModelToRefinementConfig(_config.CurrentModel, isVertex: true, _latexRefinementConfig);
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
                    if (!InteractiveDelay.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
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
                Console.WriteLine($"\n  [Request Tokens]       Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {Math.Max(0, requestInputTokens - requestCachedTokens):N0} | Output: {requestOutputTokens:N0} (inkl. Thinking Tokens)");
                Console.WriteLine($"  [Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens):N0} | Output: {_sessionTotalOutputTokens:N0}");

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
                    if (!await InteractiveDelay.SmartDelayAsync(waitTime, delayMessage)) { exceptionCaught = true; break; }
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

        if (!string.IsNullOrWhiteSpace(fullResponse)) {
            _debugChatHistory.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
        }
        else if (_debugChatHistory.Count > 0 && _debugChatHistory.Last().Role == "user") {
            // Falls abgebrochen wurde, bevor die KI etwas gesagt hat, die User-Nachricht entfernen.
            _debugChatHistory.RemoveAt(_debugChatHistory.Count - 1);
        }
    }

    /// <summary>
    /// [AI Context] Forces a real API call to explicitly acknowledge the history payload. 
    /// This guarantees the model context is correctly primed before batch processing starts and provides immediate visual feedback.
    /// [Human] Sendet die geladenen History-Dateien an Gemini und wartet auf eine Bestätigung. So stellen wir sicher, dass die KI den Kontext gefressen hat, bevor es losgeht.
    /// </summary>
    private async Task<bool> AcknowledgeHistoryAsync(string loadedFiles = "") {
        var historyPromptParts = new List<Part>(_historyParts) {
            new() { Text = $"Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {_config.CurrentModel}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards." }
        };
        var userContent = new Content { Role = "user", Parts = historyPromptParts };

        _sessionPreamble.Add(userContent);

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature, // Use config value, or hardcode 0.0 for initial acknowledgment? Let's use config.
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens // Use config value, or hardcode a smaller value for acknowledgment? Let's use config.
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

        Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.CurrentModel}: ");
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
                if (attempt > 1) Console.Write($"\n[Versuch {attempt}/{maxRetries}] Sende Anfrage... ");

                int requestInputTokens = 0;
                int requestOutputTokens = 0;
                int requestCachedTokens = 0;

                var responseStream = _client.Models.GenerateContentStreamAsync(_config.CurrentModel, _sessionPreamble, requestConfig);
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
                finalInputTokens = requestInputTokens;
                finalOutputTokens = requestOutputTokens;
                finalCachedTokens = requestCachedTokens;
                Console.WriteLine($"\n  [Request Tokens]       Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {(Math.Max(0, requestInputTokens - requestCachedTokens)):N0} | Output: {requestOutputTokens:N0} (inkl. Thinking Tokens)");
                Console.WriteLine($"  [Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {(Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens)):N0} | Output: {_sessionTotalOutputTokens:N0}");

                Console.WriteLine();
                success = true;
                break;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException || ex.Message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("\n[INFO] Bestätigung durch Benutzer abgebrochen.");
                break;
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                bool isOverloaded = ApiResilience.IsTransientError(ex);
                if (isOverloaded && attempt < maxRetries) {
                    // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
                    // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
                    // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
                    // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
                    int waitTime;
                    string contextMsg = " [History Bestätigung]";
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
                    if (!await InteractiveDelay.SmartDelayAsync(waitTime, delayMessage)) { break; }
                }
                else {
                    Console.WriteLine($"\n[Abbruch] Der Fehler konnte nicht durch einen automatischen Retry behoben werden.");
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
            Console.WriteLine("\n[FEHLER] Konnte Bestätigung für History nicht erhalten. Breche Extraktion ab.");
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
    private async Task ProcessFilesAsync(string[] files) {
        // Chronologisch aufsteigend sortieren anhand des Dateinamens und der Woche
        files = [.. files.OrderBy(f => VideoDateParser.Parse(f).Date).ThenBy(f => VideoDateParser.Parse(f).WeekNumber ?? int.MaxValue).ThenBy(f => f)];

        // [AI Context] We use a bounded channel (capacity 1) to synchronize the FFmpeg Producer task and the Gemini Consumer task.
        // This allows FFmpeg to prepare the *next* video while Gemini is waiting for the API to process the *current* video, maximizing throughput.
        // [Human] Wir nutzen einen 'Kanal' (Channel), um FFmpeg (Videobearbeitung) und Gemini (KI-Analyse) parallel laufen zu lassen.
        // Während die KI das erste Video analysiert, schneidet FFmpeg im Hintergrund schon das zweite. Das spart enorm Zeit!
        var channel = Channel.CreateBounded<PreparedVideo>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
        var producerTask = Task.Run(async () => {
            foreach (var file in files) {
                string baseName = Path.GetFileNameWithoutExtension(file);
                baseName = SpeedCompressedSuffixRegex().Replace(baseName, "");
                baseName = CompressedSuffixRegex().Replace(baseName, "");
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

                double fullOriginalVideoDuration = await FfmpegToolkit.GetVideoDurationAsync(file); // Get original video duration
                TimeSpan cacheDuration = TimeSpan.FromHours(48); // Set cache duration to 48 hours (2 days)
                bool useCache = false;

                if (cachedParts.Count > 0) {
                    var fileInfo = new FileInfo(cachedParts[0]);
                    if ((DateTime.Now - fileInfo.LastWriteTime) <= cacheDuration) {
                        // [AI Context] Defend against incomplete caches from interrupted FFmpeg runs, and against
                        // stale caches left over from a run with a different NumberOfParts (split geometry only
                        // matches the exact part count it was produced with). We also check if the files are
                        // actually valid (not 0 bytes).
                        // [Human] Wenn ein alter Lauf abgebrochen ist, liegen vielleicht nur 1-2 Teile im Cache, oder sie sind 0 Bytes groß. Das wird hier verhindert!
                        bool allFilesValid = true;
                        foreach (var cp in cachedParts) {
                            if (new FileInfo(cp).Length < 1024) { // less than 1KB is definitely invalid for a video
                                allFilesValid = false;
                                break;
                            }
                        }

                        if (cachedParts.Count == _config.NumberOfParts && allFilesValid) {
                            useCache = true;
                        }
                        else {
                            Console.WriteLine($"\n  [Cache] Ignoriere unvollständigen oder defekten Cache für '{Path.GetFileName(file)}' ({cachedParts.Count} Teil(e), valid: {allFilesValid}). FFmpeg wird neu gestartet...");
                            foreach (var f in cachedParts) { try { System.IO.File.Delete(f); } catch { } }
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
                        speedVideoDuration = await FfmpegToolkit.GetVideoDurationAsync(file);
                    }
                    else {
                        // Otherwise, it was the output of ProcessGeneralVideoAsync that was cached.
                        string expectedProcessedVideoPath = Path.Combine(tmpFolderForFile, $"{baseName}-speed-{_speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}-compressed.mp4");
                        speedVideoDuration = await FfmpegToolkit.GetVideoDurationAsync(expectedProcessedVideoPath);
                    }
                    double segmentLengthForCached = (speedVideoDuration > 0) ? (speedVideoDuration + (_config.NumberOfParts - 1) * _config.OverlapSeconds) / _config.NumberOfParts : 0;
                    var cachedPartsWithTimes = new List<VideoSegment>();
                    for (int i = 0; i < cachedParts.Count; i++) {
                        double startTime = (segmentLengthForCached > 0 && i > 0) ? i * (segmentLengthForCached - _config.OverlapSeconds) : 0;
                        Console.WriteLine($"  - {cachedParts[i]} (Est. Start: {startTime.ToString("F2", CultureInfo.InvariantCulture)}s)");
                        cachedPartsWithTimes.Add(new VideoSegment(cachedParts[i], startTime));
                    }

                    await channel.Writer.WriteAsync(new PreparedVideo(file, fileSpecificOutputFolder, tmpFolderForFile, cachedPartsWithTimes, true, fullOriginalVideoDuration));
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
                    videoToSplit = await FfmpegToolkit.ProcessGeneralVideoAsync(file, tmpFolderForFile, speedMultiplier: _speed, fps: 1, downmixToMono: true, scaleTo720p: false, overwrite: true, preset: _config.FfmpegPreset);
                    if (videoToSplit == null) {
                        Console.WriteLine($"  [FFmpeg Producer] Vorverarbeitung für {Path.GetFileName(file)} fehlgeschlagen. Überspringe Datei.");
                        continue;
                    }
                }

                Console.WriteLine($"\n[FFmpeg Producer] Starte Splitting für {Path.GetFileName(videoToSplit)} in {_config.NumberOfParts} Teile ({_config.OverlapSeconds}s Overlap)...");
                var rawPartsWithTimes = await FfmpegToolkit.ProcessSplitVideoAsync(videoToSplit, tmpFolderForFile, parts: _config.NumberOfParts, overlapSeconds: _config.OverlapSeconds, downmixToMono: false, streamCopy: true, overwrite: true, preset: _config.FfmpegPreset);

                if (rawPartsWithTimes.Count > 0) {
                    List<VideoSegment> safePartsWithTimes = [];
                    for (int i = 0; i < rawPartsWithTimes.Count; i++) {
                        string safePartPath = Path.Combine(tmpFolderForFile, $"{baseName}-part{i + 1}.mp4");

                        if (!string.Equals(rawPartsWithTimes[i].FilePath, safePartPath, StringComparison.OrdinalIgnoreCase)) {
                            if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
                            System.IO.File.Move(rawPartsWithTimes[i].FilePath, safePartPath);
                        }

                        safePartsWithTimes.Add(new VideoSegment(safePartPath, rawPartsWithTimes[i].StartTimeSeconds));
                    }
                    await channel.Writer.WriteAsync(new PreparedVideo(file, fileSpecificOutputFolder, tmpFolderForFile, safePartsWithTimes, false, fullOriginalVideoDuration));
                }
            }
            channel.Writer.Complete(); // Signalisiert dem Fließband: "Feierabend, es kommen keine Videos mehr."
        });

        // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
        // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
        bool hasErrors = false;
        bool isFirstVideo = true;

        await foreach (var (file, fileSpecificOutputFolder, tmpFolderForFile, partsWithTimes, isCached, fullOriginalVideoDuration) in channel.Reader.ReadAllAsync()) {
            if (isFirstVideo) {
                isFirstVideo = false;
                Console.WriteLine("\n[Optimierung] Erstes Video wurde gesplittet. Erstelle jetzt Google Cloud Context Cache...");
                await InitializeContextCachingAsync();
            }

            // Ensure the file-specific output folder exists before starting processing
            if (!Directory.Exists(fileSpecificOutputFolder)) {
                Directory.CreateDirectory(fileSpecificOutputFolder);
            }


            Console.WriteLine($"\n[Gemini Consumer] === Starte API-Extraktion für {Path.GetFileName(file)} ===");
            List<string> generatedTexFiles = [];
            string baseName = Path.GetFileNameWithoutExtension(file);
            baseName = SpeedCompressedSuffixRegex().Replace(baseName, "");
            baseName = CompressedSuffixRegex().Replace(baseName, "");
            if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
                baseName = "step1-" + baseName;
            }
            string fullOutputTextRaw = ""; // Stores text as is, no timestamp adjustment
            string fullOutputTextOffsetted = ""; // Stores text with timestamps adjusted by partStartTimeSeconds
            TokenUsage fileTotalTokens = default;
            bool fileProcessingSuccess = true;
            TimeSpan cacheDuration = TimeSpan.FromHours(2); // Define cache duration once
            var audioTrackExtractor = new AudioTrackExtractor(file, fileSpecificOutputFolder);

            Task<SegmentUpload>? pendingVideoUploadTask = null;
            Task<List<Part>>? pendingAudioUploadTask = null;

            for (int i = 0; i < partsWithTimes.Count; i++) {
                string safePartPath = partsWithTimes[i].FilePath;
                double partStartTimeSeconds = partsWithTimes[i].StartTimeSeconds;
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
                    audioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);
                    continue;
                }

                SegmentTranscript result;
                bool uploadSuccess;
                string? parsedPrompt;
                List<Part> attachmentParts;

                Task<SegmentUpload> uploadTask;

                if (_config.EnableParallelFileUploads && pendingVideoUploadTask != null) {
                    Console.WriteLine($"  [Pre-Upload] Nutze im Hintergrund bereits hochgeladenes Video für Teil {i + 1}...");
                    uploadTask = pendingVideoUploadTask;
                }
                else {
                    uploadTask = PrepareAndUploadPartAsync(safePartPath, i + 1, partsWithTimes.Count, file, fullOriginalVideoDuration);
                }

                SegmentUpload upload = await uploadTask;
                (uploadSuccess, parsedPrompt, attachmentParts) = (upload.Succeeded, upload.Prompt, upload.Attachments);
                if (!uploadSuccess) {
                    Console.WriteLine($"  [Fehler] Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                    fileProcessingSuccess = false;
                    hasErrors = true;
                    break;
                }

                audioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);

                // [AI Context] Validate context cache and auto-extend or re-create if expired or missing before sending each part.
                if (_config.UseContextCaching) {
                    var cacheState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
                    double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
                    bool cacheValid = false;

                    if (!string.IsNullOrEmpty(_cachedContentName) && remainingMin > 0) {
                        if (remainingMin < _config.ContextCachingMinimumRemainingMinutes) {
                            Console.WriteLine($"  [Cache] Nur noch {remainingMin:F1} min verbleibend (Schwellenwert: {_config.ContextCachingMinimumRemainingMinutes} min). Verlängere automatisch um {_config.ContextCachingIncrementMinutes} min...");
                            var updatedState = await ContextCacheStateManager.ExtendCacheAsync(_client, cacheState, _config.ContextCachingIncrementMinutes, ContextCacheStateManager.StateFileVertex);
                            if (updatedState != null) {
                                Console.WriteLine($"  [Cache] Cache verlängert. Gültig bis {updatedState.ExpireTimeUtc.ToLocalTime():t}.");
                                cacheValid = true;
                            }
                        }
                        else {
                            cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(_client, _cachedContentName);
                        }
                    }

                    if (!cacheValid) {
                        Console.WriteLine("  [Cache] Kein gültiger Cache aktiv oder Cache abgelaufen. Erstelle neuen Google Kontext-Cache für diesen Teil...");
                        ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
                        _cachedContentName = null;
                        await InitializeContextCachingAsync();
                    }
                }

                // [AI Context] If EnableParallelFileUploads is enabled, start pre-uploading the next part (or the audio file if this is the last part) while Gemini processes the current part.
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
                            if (audioTrackExtractor.PendingTask != null) {
                                await audioTrackExtractor.PendingTask;
                            }
                            var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                            string audioPath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                               ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");
                            if (System.IO.File.Exists(audioPath)) {
                                Console.WriteLine($"\n  [Pre-Upload] Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...");
                                var handler = new AttachmentHandler(_client, fileSpecificOutputFolder, [fileSpecificOutputFolder], false, _config.GcsBucketName);
                                var (s, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioPath}\"");
                                if (s) return attached;
                            }
                            return [];
                        });
                    }
                }

                result = await GenerateTexFromUploadedPartAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles);

                fileTotalTokens += result.Usage;
                int partFreshTokens = result.Usage.Fresh;

                if (!string.IsNullOrWhiteSpace(result.LatexBody)) {
                    string cleanTex = LatexResponseCleaner.CleanLatexResponse(result.LatexBody);

                    // Store the raw output for the combined file without offset
                    fullOutputTextRaw += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {result.Usage.Input:N0}, Gecacht {result.Usage.Cached:N0}, Frisch/Video {partFreshTokens:N0}, Output {result.Usage.Output:N0}) ---\n" + cleanTex;
                    if (_config.GenerateOffsetFiles) {
                        fullOutputTextOffsetted += $"\n\n% --- TEIL {i + 1} (Tokens: Input Gesamt {result.Usage.Input:N0}, Gecacht {result.Usage.Cached:N0}, Frisch/Video {partFreshTokens:N0}, Output {result.Usage.Output:N0}) ---\n" + LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds); // Accumulate offsetted text for new parts
                    }

                    // Prepend the start time to the individual part .tex file
                    string partHeader = $"% ==========================================\n" +
                                        $"% AutoExtraction Source Part: {Path.GetFileName(safePartPath)}\n" +
                                        $"% Model: {_config.CurrentModel}\n" +
                                        $"% Temperature: {_config.Temperature}\n" +
                                        $"% TopP: {_config.TopP}\n" +
                                        $"% TopK: {_config.TopK}\n" +
                                        $"% MaxOutputTokens: {_config.MaxOutputTokens}\n" +
                                        (_config.ThinkingBudget.HasValue ? $"% ThinkingBudget: {_config.ThinkingBudget.Value}\n" : "") +
                                        (!string.IsNullOrEmpty(_config.ThinkingLevel) ? $"% ThinkingLevel: {_config.ThinkingLevel}\n" : "") +
                                        $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                        $"% PART_START_SECONDS: {partStartTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}\n" +
                                        $"% ------------------------------------------\n" +
                                        $"% Token Usage Analysis (Google GenAI):\n" +
                                        $"%   - Total Prompt Tokens : {result.Usage.Input:N0} (Gesamtumfang des Aufmerksamkeitshorizonts)\n" +
                                        $"%   - Cached Context      : {result.Usage.Cached:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
                                        $"%   - Fresh Input Tokens  : {partFreshTokens:N0} (Echter neuer Payload: Video-Segment + Prompt)\n" +
                                        $"%   - Generated Output    : {result.Usage.Output:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
                                        $"% ==========================================\n\n";
                    string uniqueTargetPartPath = ExtractionHelpers.GetUniqueTexPath(targetPartPath);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + cleanTex);

                    if (_config.GenerateOffsetFiles) {
                        // NEW: Save the offsetted version of this individual part
                        string offsettedPartContent = LatexTimestampHelper.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                        string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}-offset.tex");
                        string uniqueTargetPartPathOffset = ExtractionHelpers.GetUniqueTexPath(targetPartPathOffset);
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
                    foreach (var f in generatedTexFiles) {
                        try { System.IO.File.Delete(f); } catch { /* Ignore */ }
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

                int fileTotalFreshTokens = fileTotalTokens.Fresh;
                string uniqueTargetFilePath = ExtractionHelpers.GetUniqueTexPath(targetFilePath);
                string header = $"% ==========================================\n" +
                                $"% AutoExtraction Combined Source: {Path.GetFileName(file)}\n" +
                                $"% Model: {_config.CurrentModel}\n" +
                                $"% Temperature: {_config.Temperature}\n" +
                                $"% TopP: {_config.TopP}\n" +
                                $"% TopK: {_config.TopK}\n" +
                                $"% MaxOutputTokens: {_config.MaxOutputTokens}\n" +
                                (_config.ThinkingBudget.HasValue ? $"% ThinkingBudget: {_config.ThinkingBudget.Value}\n" : "") +
                                (!string.IsNullOrEmpty(_config.ThinkingLevel) ? $"% ThinkingLevel: {_config.ThinkingLevel}\n" : "") +
                                $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                                $"% ------------------------------------------\n" +
                                $"% Token Usage Summary across {partsWithTimes.Count} Part(s):\n" +
                                $"%   - Total Prompt Tokens : {fileTotalTokens.Input:N0} (Summe aller Prompts über alle Teile)\n" +
                                $"%   - Cached Context      : {fileTotalTokens.Cached:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
                                $"%   - Fresh Input Tokens  : {fileTotalFreshTokens:N0} (Echter neuer Payload für alle Video-Teile)\n" +
                                $"%   - Total Output Tokens : {fileTotalTokens.Output:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
                                $"% ==========================================\n\n";
                await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, header + fullOutputTextRaw);
                Console.WriteLine($"\n[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}");

                string refinementTargetFile = uniqueTargetFilePath;

                if (_config.GenerateOffsetFiles) {
                    // New: Generate the offset version
                    // Note: The last part's StartTime is used as a reference point, but the overall offset should be partStartTimeSeconds from the respective part.
                    // We already accumulated the correctly offsetted text in fullOutputTextOffsetted within the loop.
                    string uniqueTargetFilePathOffset = ExtractionHelpers.GetUniqueTexPath(targetFilePathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, header + fullOutputTextOffsetted);
                    Console.WriteLine($"[AutoExtraction] Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}");
                    refinementTargetFile = uniqueTargetFilePathOffset;
                }

                // Trigger LatexRefinementSession immediately for the generated offset file, if enabled.
                // Warten, bis das Audio fertig ist, bevor das Refinement startet,
                // da das Refinement die Audiodatei für die API benötigt!
                if (audioTrackExtractor.PendingTask != null) {
                    Console.WriteLine($"\n[AutoExtraction] Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...");
                    await audioTrackExtractor.PendingTask;
                }

                // LatexRefinementSession uses its own dedicated API key, so we need to resolve it.
                if (_latexRefinementConfig != null) {
                    _latexRefinementConfig.UseVertex = Program.Activate_Vertex;
                    if (_config.NumberOfParts <= 1) {
                        Console.WriteLine($"\n[AutoExtraction] NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.");
                        _latexRefinementConfig.Step1MergeAndTimestamp.Enabled = false;
                    }
                }
                Client refinementClient = GoogleAiClientBuilder.BuildVertexClient(_latexRefinementConfig?.VertexProjectId ?? "", _latexRefinementConfig?.VertexLocation ?? "");

                // Check for the most recent audio file by looking at modified times, or simply look for the exact name.
                // Since ExtractAudioAsAacAsync might create -copy-1 if it exists, let's just grab the newest .aac file in the folder.
                var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                string audioFilePath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                       ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");

                List<Part>? preUploadedAudioParts = null;
                if (_config.EnableParallelFileUploads && pendingAudioUploadTask != null) {
                    Console.WriteLine($"\n[AutoExtraction] Warte auf Abschluss des parallelen Audio-Uploads...");
                    preUploadedAudioParts = await pendingAudioUploadTask;
                }

                Console.WriteLine($"\n[AutoExtraction] Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
                // Pass the Vertex AI client for refinement, as VertexAutoExtractionSession requires an Vertex AI client for this
                var refinementSession = new LatexRefinementSession(
                    refinementClient,
                    _latexRefinementConfig!,
                    refinementTargetFile,
                    _config,
                    audioFilePath,
                    preUploadedAudioParts);

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
    private async Task<SegmentUpload> PrepareAndUploadPartAsync(string partFile, int partNumber, int totalParts, string originalFileName, double fullOriginalVideoDuration) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        string prompt = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";

        if (partNumber == 1) {
            prompt = $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown"}), and week number ({dateInfo.WeekInfo ?? "N/A"}) are important metadata since this is part 1 of the lecture. " + prompt;
        }
        else {
            prompt = $"The lecture took place on {dateContext} (Day of the week: {dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown"}). This is not so important since this is part {partNumber} of the lecture. " + prompt;
        }

        double partDurationSeconds = await FfmpegToolkit.GetVideoDurationAsync(partFile);
        TimeSpan t = TimeSpan.FromSeconds(partDurationSeconds);
        string durationString = string.Format("{0:D2} minutes and {1:D2} seconds", t.Minutes, t.Seconds);

        TimeSpan fullVideoTime = TimeSpan.FromSeconds(fullOriginalVideoDuration);
        string fullDurationString = string.Format("{0:D2} minutes and {1:D2} seconds", fullVideoTime.Minutes, fullVideoTime.Seconds);

        prompt += "\n\n<context_and_parameters>\n" +
                  "IMPORTANT: The System Instructions (System Prompt) contain the absolute rules, syntax specifications, and constraints for this transcription and MUST be followed strictly. The parameters below only specify details for this video fragment:\n\n" +
                  $"<parameter name=\"source_video\">You must transcribe the video attachment named `{Path.GetFileName(partFile)}` verbatim according to the system instructions. Ensure you transcribe every single spoken word up to the very last second of the video, even if it cuts off mid-sentence.</parameter>\n" +
                  $"<parameter name=\"segment_info\">You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long. The duration of the entire lecture video is {fullDurationString}.</parameter>\n" +
                  $"<parameter name=\"duration_and_timestamps\">Do NOT calculate any time offset for the 'spoken-clean' environment. Start at 00:00:00 and ensure the final timestamp in your very last 'spoken-clean' block perfectly matches the segment length ({durationString}).</parameter>\n";

        if (partNumber != 1) {
            prompt += "<parameter name=\"segment_start\">\n" +
                      "1. Start the transcription EXACTLY where the audio begins in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction.\n" +
                      "2. If the previous part ended in the middle of an environment (like a `proof`, `short-proof`, or `math-stroke`), you MUST logically continue that environment in this part (e.g., start with `\\begin{proof}` or `\\begin{math-stroke}` if the professor is still doing the proof/derivation). However, you must still transcribe the spoken words exactly from where this new video segment begins.\n" +
                      "</parameter>\n";
        }

        prompt += "<parameter name=\"merging_and_scope\">Do NOT attempt to merge the current part with the previous parts (i.e. do not try to fix the cut). Focus solely on transcribing this fragment as it is. As specified in the System Instructions, keep mathematical derivations and explanations self-contained and grouped within 'math-stroke' environments to preserve logical flow.</parameter>\n" +
                  "</context_and_parameters>";

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
        if (!uploadSuccess || attachmentParts.Count == 0) return new SegmentUpload(false, null, []);

        return new SegmentUpload(true, parsedPrompt, attachmentParts);
    }

    /// <summary>
    /// [AI Context] Executes the Vertex AI generation call for a single video segment.
    /// Prompt parts are assembled in strict prefix-stable order (payload first, parameters second, reference context trailing) to preserve cache alignment.
    /// [Human] Generiert den LaTeX-Code für ein bestimmtes Videosegment über Vertex AI.
    /// </summary>
    private async Task<SegmentTranscript> GenerateTexFromUploadedPartAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var userPromptParts = new List<Part>();

        // 1. If InlinePrecedingLecTexParts is enabled, inline previous .tex files BEFORE the video payload to enable implicit prefix caching across parts.
        if (_config.InlinePrecedingLecTexParts && _config.DebugSendReferenceFile && previousTexFiles.Count > 0) {
            Console.WriteLine("  [Kontext] Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:");
            string contextText =
                "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
                "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
                "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
                "CRITICAL RULES:\n" +
                "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
                $"2. Your SOLE task is to transcribe the NEW attached video segment: `{Path.GetFileName(partFile)}`.\n" +
                "3. Treat these context files as read-only and focus entirely on the new video fragment.\n\n";
            foreach (var texFile in previousTexFiles) {
                Console.WriteLine($"    - {Path.GetFileName(texFile)}");
                string content = await System.IO.File.ReadAllTextAsync(texFile);
                contextText += $"<reference_context file=\"{Path.GetFileName(texFile)}\">\n{content}\n</reference_context>\n\n";
            }
            userPromptParts.Add(new Part { Text = contextText.TrimEnd() });
        }

        // 2. Primary payload (attachmentParts / video)
        userPromptParts.AddRange(attachmentParts);

        // 3. Add segment prompt parameters
        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        // 4. Fallback: Append previous .tex files AT THE END if InlinePrecedingLecTexParts is disabled
        if (!_config.InlinePrecedingLecTexParts && _config.DebugSendReferenceFile && previousTexFiles.Count > 0) {
            Console.WriteLine("  [Kontext] Sende folgende bereits generierte .tex-Dateien als Referenzkontext mit (am Ende angehängt):");
            string contextText =
                "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
                "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
                "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
                "CRITICAL RULES:\n" +
                "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
                $"2. Your SOLE task is to transcribe the NEW attached video segment: `{Path.GetFileName(partFile)}`.\n" +
                "3. Treat these context files as read-only and focus entirely on the new video fragment.\n\n";
            foreach (var texFile in previousTexFiles) {
                Console.WriteLine($"    - {Path.GetFileName(texFile)}");
                string content = await System.IO.File.ReadAllTextAsync(texFile);
                contextText += $"<reference_context file=\"{Path.GetFileName(texFile)}\">\n{content}\n</reference_context>\n\n";
            }
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

        using var cts = new CancellationTokenSource();
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; try { cts.Cancel(); } catch { } }
        Console.CancelKeyPress += cancelHandler;

        while (true) {
            Console.WriteLine($"  [API] Sende Anfrage für Part {partNumber} an Vertex AI ({_config.CurrentModel}) (Request {currentRequest}/{maxRequestsPerPart})...");
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

            Console.WriteLine($"\n  [Request Tokens]       Total Prompt: {requestInputTokens:N0} | Gecacht: {requestCachedTokens:N0} | Frisch: {freshReqTokens:N0} | Output: {requestOutputTokens:N0} (inkl. Thinking Tokens)");
            Console.WriteLine($"  [Part Total Tokens]    Total Prompt: {interactionInputTokens:N0} | Gecacht: {interactionCachedTokens:N0} | Frisch: {freshPartTokens:N0} | Output: {interactionOutputTokens:N0} (inkl. Thinking Tokens)");
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
            else Console.WriteLine("\n  [AutoExtraction] Unerwartetes Ende der Antwort (Max Tokens?). Bereite automatisierten 'Continue'-Prompt vor...");

            Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

            history.Add(new Content { Role = "model", Parts = [new() { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new() { Text = continuePrompt }] });
            currentLogPrompt = $"[Continue Prompt für Part {partNumber}]:\n{continuePrompt}";

            // [AI Context] Under Vertex AI, we do not have strict RPM / TPM limits, but a 150s delay provides a buffer to avoid transient concurrency limits or spikes.
            // [Human] Unter Vertex AI sind die Rate-Limits höher, aber eine Pause von 150s schützt vor temporären Server-Spikes. (Oder drücke Enter für sofortigen Skip)
            Console.WriteLine($"\n  [Timer] Warte 150 Sekunden vor der Fortsetzung... (Oder drücke Enter für sofortigen Skip)");
            if (!await InteractiveDelay.SmartDelayAsync(150, "Warte auf Fortsetzung (Sicherheits-Puffer)...")) {
                Console.WriteLine("\n\n[INFO] Warten durch Benutzer abgebrochen.");
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

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PreCompressedFileRegex();
}
