using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Latex;
using Spectre.Console;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Runs the YouTube transcription pipeline: resolves which tasks to process (configured
/// or interactively entered), then for each task sends every configured timestamp fragment to the
/// model as a <c>Part.FromUri</c> video reference and writes per-fragment and combined .tex output.
///
/// <para>Extracted from <c>AiStudioAutoExtractionSession</c> (Phase 11). It is a distinct feature
/// from the FFmpeg video pipeline - no local files, no splitting, no upload - and shares only the
/// two calls behind <see cref="IYouTubeTranscriptionHost"/>. Keeping it out of the session class
/// means work on the video pipeline never has to read it.</para>
/// [Human] Führt die YouTube-Transkription aus: pro Fragment eine Anfrage an das Modell, dann
/// Einzel- und Gesamtdatei schreiben. Aus der Session-Klasse herausgelöst.
/// </summary>
public sealed class YouTubeTaskRunner(IAutoExtractionConfig config, IYouTubeTranscriptionHost host) {
    private readonly IAutoExtractionConfig _config = config;
    private readonly IYouTubeTranscriptionHost _host = host;

    public async Task RunAsync() {
        var tasksToProcess = ResolveTasks();
        if (tasksToProcess.Count == 0) {
            Ui.Info("Keine YouTube-Aufgaben zum Verarbeiten.");
            return;
        }

        Ui.Step($"Starte Transkription für {tasksToProcess.Count} YouTube-Video(s)...", "YouTube Mode");

        if (!await _host.EnsureSessionSetupAsync()) return;

        foreach (var task in tasksToProcess) {
            if (string.IsNullOrWhiteSpace(task.VideoUrl)) continue;
            await TranscribeTaskAsync(task);
        }
    }

    /// <summary>
    /// [AI Context] Decides between the tasks declared in the JSON config and one entered
    /// interactively. Declining the configured tasks falls through to the interactive prompt, so
    /// "no" means "let me type a different URL" rather than "do nothing".
    /// [Human] Wählt zwischen den in der JSON konfigurierten Aufgaben und einer interaktiv
    /// eingegebenen.
    /// </summary>
    private List<YouTubeTranscriptionTask> ResolveTasks() {
        List<YouTubeTranscriptionTask> tasks = [];

        if (_config.YouTubeTasks != null && _config.YouTubeTasks.Length > 0) {
            Ui.Info($"Es wurden {_config.YouTubeTasks.Length} Aufgabe(n) in der Konfiguration gefunden.", "YouTube Mode");
            if (Ui.Confirm("Möchtest du diese Aufgaben ausführen?", true)) {
                tasks.AddRange(_config.YouTubeTasks);
                return tasks;
            }
        }
        else {
            Ui.Info("Keine vorgegebenen YouTube-Aufgaben in der Konfiguration gefunden.", "YouTube Mode");
        }

        var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
        if (interactiveTask != null) {
            tasks.Add(interactiveTask);
        }
        return tasks;
    }

    private async Task TranscribeTaskAsync(YouTubeTranscriptionTask task) {
        string baseName = string.IsNullOrWhiteSpace(task.OutputName) ? "youtube-lecture" : task.OutputName;
        if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
            baseName = "step1-" + baseName;
        }

        string fileSpecificOutputFolder = Path.Combine(_config.TargetFolder, baseName);
        if (!Directory.Exists(fileSpecificOutputFolder)) {
            Directory.CreateDirectory(fileSpecificOutputFolder);
        }

        Ui.Step($"Starte API-Extraktion für URL: {task.VideoUrl} ({baseName})", "YouTube Consumer");
        List<string> generatedTexFiles = [];
        string fullOutputTextRaw = "";

        for (int i = 0; i < task.Fragments.Count; i++) {
            var frag = task.Fragments[i];
            int partNum = i + 1;
            Ui.Step($"Verarbeite Fragment {partNum}/{task.Fragments.Count}: {frag.StartTime} bis {frag.EndTime} ({frag.PartTitle})");

            string parsedPrompt = BuildFragmentPrompt(frag, partNum);
            var attachmentParts = new List<Part> { Part.FromUri(task.VideoUrl, "video/mp4") };

            string texOutput = (await _host.TranscribeSegmentToLatexAsync(
                task.VideoUrl, partNum, baseName, parsedPrompt, attachmentParts, generatedTexFiles
            )).LatexBody;

            if (string.IsNullOrWhiteSpace(texOutput)) continue;

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

        if (!string.IsNullOrWhiteSpace(fullOutputTextRaw)) {
            string combinedPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}.tex");
            await System.IO.File.WriteAllTextAsync(combinedPath, fullOutputTextRaw.Trim());
            Ui.Success($"Zusammengeführte YouTube-Transkription gespeichert unter: {combinedPath}");
        }
    }

    /// <summary>
    /// [AI Context] Builds the per-fragment instruction. Part 1 is told the lecture date matters;
    /// later parts are told it matters less but should still be stated - the model otherwise either
    /// omits the date entirely or repeats a full date header on every part.
    /// [Human] Baut die Anweisung für ein Fragment. Teil 1 betont das Datum, spätere Teile nicht.
    /// </summary>
    public static string BuildFragmentPrompt(YouTubeTimestampFragment frag, int partNum) {
        string dateNotice = (partNum == 1)
            ? "Please note that since this is part 1 of the lecture, the date of the transcription is important."
            : $"The lecture took place... Please note that since this is part {partNum} of the lecture, the date is not so important (but tell it anyway).";

        return $"Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n[IMPORTANT INSTRUCTION FOR YOUTUBE VIDEO]:\nThis is part {partNum} ('{frag.PartTitle}') of the lecture. Please focus ONLY on transcribing and extracting the chosen video fragment starting at timestamp {frag.StartTime} and ending at timestamp {frag.EndTime}.\n{dateNotice}";
    }
}
