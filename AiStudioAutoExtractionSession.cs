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
        "gemini-3.5-flash",
        "gemini-3-flash-preview"
    ];

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
        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (_config.SystemInstructionPaths != null && _config.SystemInstructionPaths.Length != 0) {
                Console.WriteLine("\nFolgende System Instruction-Dateien sind konfiguriert:");

                // Resolve all files from configured paths, handling directories
                var resolvedInstructionFiles = ExtractionHelpers.ResolveHistoryFiles(_config.SystemInstructionPaths);

                if (resolvedInstructionFiles.Count > 0) {
                    ExtractionHelpers.PrintFileTree(resolvedInstructionFiles);
                    List<string> distinctHistoryFiles = [];
                    if (_config.LoadHistoryIntoSystemInstruction && !_historyWasLoaded) {
                        distinctHistoryFiles = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);
                        if (distinctHistoryFiles.Count > 0) {
                            Console.WriteLine("\nFolgende Dateien sind als History konfiguriert (werden aber direkt in die System Instruction geladen):");
                            ExtractionHelpers.PrintFileTree(distinctHistoryFiles);
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
                        string? commonBase = ExtractionHelpers.FindCommonBaseDirectory(allPathsForIndex);

                        var instructionBuilder = new System.Text.StringBuilder();
                        instructionBuilder.AppendLine("# SYSTEM PROTOCOL & SYSTEM INSTRUCTIONS (MASTER CONSTRAINTS)");
                        instructionBuilder.AppendLine("IMPORTANT: The guidelines, formatting specifications, and syntax instructions contained in these system instruction files are absolute and strictly non-negotiable. They must take absolute precedence over any prompt guidelines or inputs. Do not skip any files or parts under any circumstances.\n");
                        instructionBuilder.AppendLine("In order to fulfill the job of creating a high-value educational masterpiece that safely compiles, you need to know the file structure of the system prompt and read all of those files carefully.\n");
                        instructionBuilder.AppendLine("# Folder Structure of System Instructions\n");
                        instructionBuilder.AppendLine("## System Instructions");
                        instructionBuilder.Append(ExtractionHelpers.GenerateMarkdownFileTree(resolvedInstructionFiles, commonBase));

                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            instructionBuilder.AppendLine("\n## Training History");
                            instructionBuilder.Append(ExtractionHelpers.GenerateMarkdownFileTree(distinctHistoryFiles, commonBase));
                        }
                        instructionBuilder.AppendLine("\n******\n------\n******\n");

                        foreach (var filePath in resolvedInstructionFiles) {
                            string rawRelPath = !string.IsNullOrEmpty(commonBase)
                                ? Path.GetRelativePath(commonBase, filePath)
                                : Path.GetFileName(filePath);
                            string relPath = ExtractionHelpers.NormalizeRelativePath(rawRelPath);
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
            var distinctFiles = ExtractionHelpers.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (distinctFiles.Count > 0) {
                Console.WriteLine("\nFolgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
                ExtractionHelpers.PrintFileTree(distinctFiles);
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
                string val = "";
                if (normalizedInput.StartsWith("change-key", StringComparison.OrdinalIgnoreCase)) {
                    val = normalizedInput["change-key".Length..].Trim();
                }
                else if (normalizedInput.StartsWith("change key", StringComparison.OrdinalIgnoreCase)) {
                    val = normalizedInput["change key".Length..].Trim();
                }
                else if (normalizedInput.StartsWith("9 ")) {
                    val = normalizedInput[2..].Trim();
                }

                if (string.IsNullOrEmpty(val)) {
                    Console.Write("Neues API-Key Profil (0-3): ");
                    val = Console.ReadLine()?.Trim() ?? "";
                }

                if (int.TryParse(val, out int newProfile) && newProfile >= 0 && newProfile <= 3) {
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
            if (_config.ThinkingBudget.HasValue || !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (!string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig.ThinkingLevel = _config.ThinkingLevel;
                }
                else if (_config.ThinkingBudget.HasValue) {
                    requestConfig.ThinkingConfig.ThinkingBudget = _config.ThinkingBudget;
                }
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
        if (!string.IsNullOrWhiteSpace(_systemInstructionText) || (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0)) {
            var sysParts = new List<Part>();
            if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
            if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) sysParts.AddRange(_historyParts);
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }
        if (SupportsThinking(_config.CurrentModel)) {
            if (_config.ThinkingBudget.HasValue || !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (!string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig.ThinkingLevel = _config.ThinkingLevel;
                }
                else if (_config.ThinkingBudget.HasValue) {
                    requestConfig.ThinkingConfig.ThinkingBudget = _config.ThinkingBudget;
                }
            }
        }

        // Console.Write($"\n[AutoExtraction] Warte auf Bestätigung der History von {_config.Model}: ");
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
                    // Console.Write(txt);
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
                // Console.WriteLine($"\n  [Request Tokens] Input: {requestInputTokens} | Output: {requestOutputTokens} (inkl. Thinking Tokens)");
                // Console.WriteLine($"  [Session Total Tokens] Input: {_sessionTotalInputTokens} | Output: {_sessionTotalOutputTokens}");

                // Console.WriteLine();
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
                    if (!await ExtractionHelpers.SmartDelayAsync(waitTime, delayMessage)) { break; }
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
                        foreach (var cp in cachedParts) {
                            if (new FileInfo(cp).Length < 1024) { // less than 1KB is definitely invalid for a video
                                allFilesValid = false;
                                break;
                            }
                        }

                        if (cachedParts.Count >= _config.NumberOfParts && allFilesValid) {
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
                    if (_config.NumberOfParts <= 1) {
                        Console.WriteLine($"\n[AutoExtraction] NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.");
                        _latexRefinementConfig.Step1MergeAndTimestamp.Enabled = false;
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
                    uploadTask = PrepareAndUploadPartAsync(safePartPath, i + 1, partsWithTimes.Count, file);
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
                            pendingVideoUploadTask = PrepareAndUploadPartAsync(partsWithTimes[i + 1].FilePath, i + 2, partsWithTimes.Count, file);
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
                            string audioPath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                               ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");
                            if (System.IO.File.Exists(audioPath)) {
                                Console.WriteLine($"\n  [Pre-Upload] Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...");
                                var handler = new AttachmentHandler(refinementClient ?? _client, fileSpecificOutputFolder, [fileSpecificOutputFolder], true, "");
                                var (s, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioPath}\"");
                                if (s) return attached;
                            }
                            return [];
                        });
                    }
                }

                result = await GenerateTexFromUploadedPartAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles);

                fileTotalInputTokens += result.partInputTokens;
                fileTotalOutputTokens += result.partOutputTokens;
                fileTotalCachedTokens += result.partCachedTokens;

                if (i + 1 < partsWithTimes.Count) {
                    rateLimitDelayTask = Task.Run(async () => {
                        // [AI Context] A 70-second delay is enforced here to accommodate strictly-enforced tokens-per-minute (TPM) and requests-per-minute (RPM) quotas by the API provider. 1m10s ensures a full quota refresh.
                        // [Human] Wir warten hier 1 Minute und 10 Sekunden (70s), da wir ein hartes Limit von Tokens pro Minute haben. Das stellt sicher, dass das Limit vor dem nächsten Aufruf wieder zurückgesetzt ist.
                        Console.WriteLine($"\n  [Timer] Warte 70 Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
                        await ExtractionHelpers.SmartDelayAsync(70, "Warte auf Rate-Limits (Token Refill)...");
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
                                        $"%   - Total Prompt Tokens : {result.partInputTokens:N0} (Gesamtumfang des Aufmerksamkeitshorizonts)\n" +
                                        $"%   - Cached Context      : {result.partCachedTokens:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
                                        $"%   - Fresh Input Tokens  : {partFreshTokens:N0} (Echter neuer Payload: Video-Segment + Prompt)\n" +
                                        $"%   - Generated Output    : {result.partOutputTokens:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
                                        $"% ==========================================\n\n";
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

                int fileTotalFreshTokens = Math.Max(0, fileTotalInputTokens - fileTotalCachedTokens);
                string uniqueTargetFilePath = GetUniqueTexPath(targetFilePath);
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
                                $"%   - Total Prompt Tokens : {fileTotalInputTokens:N0} (Summe aller Prompts über alle Teile)\n" +
                                $"%   - Cached Context      : {fileTotalCachedTokens:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
                                $"%   - Fresh Input Tokens  : {fileTotalFreshTokens:N0} (Echter neuer Payload für alle Video-Teile)\n" +
                                $"%   - Total Output Tokens : {fileTotalOutputTokens:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
                                $"% ==========================================\n\n";
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
                string audioFilePath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
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

    private async Task<(bool success, string? parsedPrompt, List<Part> attachmentParts)> PrepareAndUploadPartAsync(string partFile, int partNumber, int totalParts, string originalFileName) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        string prompt = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";

        if (partNumber == 1) {
            prompt = $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown"}), and week number ({dateInfo.WeekInfo ?? "N/A"}) are important metadata since this is part 1 of the lecture. " + prompt;
        }
        else {
            prompt = $"The lecture took place on {dateContext} (Day of the week: {dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown"}). This is not so important since this is part {partNumber} of the lecture. " + prompt;
        }

        double partDurationSeconds = await FfmpegUtilities.FfmpegToolkit.GetVideoDurationAsync(partFile);
        TimeSpan t = TimeSpan.FromSeconds(partDurationSeconds);
        string durationString = string.Format("{0:D2} minutes and {1:D2} seconds", t.Minutes, t.Seconds);

        prompt += "\n\n<context_and_parameters>\n" +
                  "IMPORTANT: The System Instructions (System Prompt) contain the absolute rules, syntax specifications, and constraints for this transcription and MUST be followed strictly. The parameters below only specify details for this video fragment:\n\n" +
                  $"<parameter name=\"segment_info\">You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long.</parameter>\n" +
                  $"<parameter name=\"duration_and_timestamps\">Do NOT calculate any time offset for the 'spoken-clean' environment. Start at 00:00:00 and ensure the final timestamp in your very last 'spoken-clean' block perfectly matches the segment length ({durationString}).</parameter>\n";

        if (partNumber != 1) {
            prompt += "<parameter name=\"segment_start\">Start the transcription EXACTLY where the professor starts in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction whatsoever.</parameter>\n";
        }

        prompt += "<parameter name=\"merging_and_scope\">Do NOT attempt to merge the current part with the previous parts. Focus solely on transcribing this fragment. As specified in the System Instructions, keep mathematical derivations and explanations self-contained and grouped within 'math-stroke' environments to preserve logical flow.</parameter>\n" +
                  "</context_and_parameters>";

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
        if (!uploadSuccess || attachmentParts.Count == 0) return (false, null, []);

        return (true, parsedPrompt, attachmentParts);
    }

    private async Task<(string texOutput, int inputTokens, int outputTokens, int cachedTokens)> GenerateTexFromUploadedPartAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var userPromptParts = new List<Part>();

        if (previousTexFiles.Count > 0) {
            Console.WriteLine("  [Kontext] Sende folgende bereits generierte .tex-Dateien als Kontext mit:");
            string contextText = "Here are the context files from the previous parts of the lecture. Please note that these files might contain compilation errors from previous, incomplete, or flawed extractions. Treat them as contextual reference material, but do not assume perfect LaTeX syntax or content validity.\n\n";
            foreach (var texFile in previousTexFiles) {
                Console.WriteLine($"    - {Path.GetFileName(texFile)}");
                string content = await System.IO.File.ReadAllTextAsync(texFile);
                contextText += $"<reference_context file=\"{Path.GetFileName(texFile)}\">\n{content}\n</reference_context>\n\n";
            }
            userPromptParts.Add(new() { Text = contextText.TrimEnd() });
        }

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

        if (!string.IsNullOrWhiteSpace(_systemInstructionText) || (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0)) {
            var sysParts = new List<Part>();
            if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
            if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) sysParts.AddRange(_historyParts);
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }
        if (SupportsThinking(_config.CurrentModel)) {
            if (_config.ThinkingBudget.HasValue || !string.IsNullOrEmpty(_config.ThinkingLevel)) {
                requestConfig.ThinkingConfig = new ThinkingConfig();
                if (!string.IsNullOrEmpty(_config.ThinkingLevel)) {
                    requestConfig.ThinkingConfig.ThinkingLevel = _config.ThinkingLevel;
                }
                else if (_config.ThinkingBudget.HasValue) {
                    requestConfig.ThinkingConfig.ThinkingBudget = _config.ThinkingBudget;
                }
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
            else Console.WriteLine("\n  [AutoExtraction] Unerwartetes Ende der Antwort (Max Tokens?). Bereite automatisierten 'Continue'-Prompt vor...");

            Console.WriteLine($"\n  [Sende folgenden Continue-Prompt:]\n{continuePrompt}\n");

            history.Add(new Content { Role = "model", Parts = [new() { Text = chunkResp }] });
            history.Add(new Content { Role = "user", Parts = [new() { Text = continuePrompt }] });
            currentLogPrompt = $"[Continue Prompt für Part {partNumber}]:\n{continuePrompt}";

            // [AI Context] A 70-second delay is enforced here to accommodate strictly-enforced tokens-per-minute (TPM) and requests-per-minute (RPM) quotas by the API provider. 1m10s ensures a full quota refresh.
            // [Human] Wir warten hier 1 Minute und 10 Sekunden (70s), da wir ein hartes Limit von Tokens pro Minute haben. Das stellt sicher, dass das Limit vor dem nächsten Aufruf wieder zurückgesetzt ist.
            Console.WriteLine($"\n  [Timer] Warte 70 Sekunden vor der Fortsetzung, um API-Limits zu schonen... (Oder drücke Enter für sofortigen Skip)");
            if (!await ExtractionHelpers.SmartDelayAsync(70, "Warte auf Rate-Limits (Token Refill)...")) {
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