using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Media;

/// <summary>
/// [AI Context] Manages FFmpeg preprocessing tasks for video/audio files before feeding them to the AI.
/// Interactive console menu that acts as a frontend for the FfmpegToolkit.
/// [Human] Dies ist die Menü-Oberfläche, wenn du im Hauptmenü "3" drückst. Sie regelt die Konfiguration und Konvertierung.
/// </summary>
public class FfmpegInteractiveSession(FfmpegSessionConfig config) {
    private readonly string DefaultSourceFolder = config.SourceFolder;
    private readonly string DefaultDestinationFolder = config.TargetFolder;

    // Conversion Settings State
    private double _speedMultiplier = 1.2;
    private int _fps = 1;
    private bool _downmixToMono = true;
    private bool _scaleTo720p = true;
    private string _preset = "fast";
    private double? _startTimeSeconds = null;
    private double? _durationSeconds = null;

    // Custom Mode State
    private bool _useCustomTemplate = false;
    private string _customCommandTemplate = "";
    private string _customOutputExtension = ".mp4";

    // Splitting State
    private int _splitParts = 1; // 1 means no split
    private double _overlapSeconds = 180;

    public async Task StartAsync() {
        Ui.Header("🎬 FFmpeg Console Video Preprocessor Dashboard");

        // [AI Context] Source folder → destination folder → file selection, as a step machine so each
        // one can be corrected with "back" instead of restarting the session from the main menu.
        // [Human] Quellordner, Zielordner und Dateiauswahl als Schrittkette mit "Zurück".
        var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
        string sourceFolder = string.IsNullOrEmpty(DefaultSourceFolder) ? ffmpegConfig.SourceFolder : DefaultSourceFolder;
        string destFolder = "";
        string[] filesToProcess = [];
        int setupStep = 0;

        while (setupStep < 3) {
            switch (setupStep) {
                case 0: {
                    var folder = ConfigurationPrompts.PromptForSourceFolder(sourceFolder, newFolder => {
                        ffmpegConfig.SourceFolder = newFolder;
                        ConfigLoader<FfmpegSessionConfig>.Save(ffmpegConfig);
                    });
                    if (!folder.IsValue) return;

                    sourceFolder = folder.Value!;
                    if (!Directory.Exists(sourceFolder)) {
                        Ui.Error($"Quellordner '{sourceFolder}' existiert nicht.");
                        break;
                    }
                    setupStep = 1;
                    break;
                }

                case 1: {
                    var destination = ResolveDestinationFolder(sourceFolder);
                    if (destination.IsBack) { setupStep = 0; break; }
                    if (!destination.IsValue) return;
                    destFolder = destination.Value!;
                    setupStep = 2;
                    break;
                }

                default: {
                    var files = SelectTargetFiles(sourceFolder);
                    if (files.IsBack) { setupStep = 1; break; }
                    if (!files.IsValue) return;

                    filesToProcess = files.Value!;
                    if (filesToProcess.Length == 0) {
                        Ui.Warn("Keine Dateien zur Verarbeitung ausgewählt. Breche ab.");
                        return;
                    }
                    setupStep = 3;
                    break;
                }
            }
        }

        while (true) {
            string choice = RenderDashboardAndPromptChoice(sourceFolder, destFolder, filesToProcess);

            if (choice == "exit") {
                Ui.Info("Breche ab und kehre zum Hauptmenü zurück.");
                return;
            }

            switch (choice) {
                case "speed":
                    ConfigureSpeed();
                    break;
                case "fps":
                    ConfigureFps();
                    break;
                case "audio":
                    _downmixToMono = !_downmixToMono;
                    _useCustomTemplate = false;
                    Ui.Success($"Downmix to Mono: {(_downmixToMono ? "AKTIVIERT (Mono 96k)" : "DEAKTIVIERT (Original Stereo Copy)")}");
                    break;
                case "resolution":
                    _scaleTo720p = !_scaleTo720p;
                    _useCustomTemplate = false;
                    Ui.Success($"Auf 720p skalieren: {(_scaleTo720p ? "AKTIVIERT" : "DEAKTIVIERT (Original-Auflösung)")}");
                    break;
                case "preset":
                    ConfigurePreset();
                    break;
                case "range":
                    ConfigureTimeRange();
                    break;
                case "split":
                    ConfigureSplitting();
                    break;
                case "custom":
                    ConfigureCustomCommand();
                    break;
                case "start":
                    await RunConversionProcessAsync(filesToProcess, destFolder);
                    Ui.Info("Konvertierung abgeschlossen.");
                    return;
            }
        }
    }

    /// <summary>
    /// [AI Context] Confirms or replaces the destination folder and makes sure it exists. Loops on a
    /// creation failure rather than aborting the session - the user can simply name a different
    /// folder - and reports "back" so the caller can re-ask for the source folder.
    /// [Human] Bestätigt oder ändert den Zielordner und legt ihn bei Bedarf an.
    /// </summary>
    private PromptResult<string> ResolveDestinationFolder(string sourceFolder) {
        while (true) {
            string destFolder = DefaultDestinationFolder;
            if (string.IsNullOrEmpty(destFolder)) {
                destFolder = Path.Combine(sourceFolder, "extracted_output");
            }

            Ui.Info($"Aktueller Zielordner (Destination): {destFolder}");
            var keepDest = Ui.ConfirmOrBack("Möchten Sie diesen Zielordner beibehalten?", true);
            if (!keepDest.IsValue) return new PromptResult<string>(keepDest.Outcome, null);

            if (!keepDest.Value) {
                destFolder = Ui.Ask("Neuen Zielordner eingeben:").Trim('\"', '\'');
            }

            if (!Directory.Exists(destFolder)) {
                try {
                    Ui.Info($"Erstelle Zielordner '{destFolder}'...");
                    Directory.CreateDirectory(destFolder);
                }
                catch (Exception ex) {
                    Ui.Error($"Fehler beim Erstellen des Zielordners: {ex.Message}");
                    continue;
                }
            }

            return PromptResult.FromValue(destFolder);
        }
    }

    private static PromptResult<string[]> SelectTargetFiles(string sourceFolder) {
        var selection = Ui.Select("Dateiauswahl-Modus:", [
            ("Einzelne Videodatei auswählen", false),
            ("Alle Videodateien im Quellordner verarbeiten (Batch-Modus)", true)
        ]);

        if (!selection.IsValue) return new PromptResult<string[]>(selection.Outcome, null);

        return PromptResult.FromValue(selection.Value
            ? FileSelectionPrompt.SelectBatchFiles(sourceFolder)
            : FileSelectionPrompt.SelectSingleFile(sourceFolder));
    }

    private string RenderDashboardAndPromptChoice(string sourceFolder, string destFolder, string[] filesToProcess) {
        Ui.Detail($"Quellordner: {sourceFolder}");
        Ui.Detail($"Zielordner:  {destFolder}");
        Ui.Detail($"Zu verarbeiten ({filesToProcess.Length} Datei(en)):");
        for (int i = 0; i < Math.Min(filesToProcess.Length, 5); i++) {
            Ui.Detail($"   - {Path.GetFileName(filesToProcess[i])}");
        }
        if (filesToProcess.Length > 5) {
            Ui.Detail($"   ... und {filesToProcess.Length - 5} weitere.");
        }

        var choices = new Dictionary<string, string>();

        if (_useCustomTemplate) {
            Ui.Warn($"Custom Mode aktiv - Command Template: {_customCommandTemplate}");
        }
        else {
            string rangeText = (_startTimeSeconds.HasValue || _durationSeconds.HasValue)
                ? $"Start={_startTimeSeconds ?? 0}s, Dauer={_durationSeconds?.ToString() ?? "Rest"}"
                : "Komplettes Video";
            
            string splitText = (_splitParts > 1)
                ? $"{_splitParts} Teile (Überlappung: {_overlapSeconds}s)"
                : "Deaktiviert (Einzelne Datei)";

            choices[$"⚡ Geschwindigkeit (Speed):    {_speedMultiplier:F1}x"] = "speed";
            choices[$"🎞️ Bilder pro Sekunde (FPS):   {_fps} FPS"] = "fps";
            choices[$"🔊 Tonspur-Format (Audio):     {(_downmixToMono ? "Mono (96k AAC, empfohlen)" : "Stereo (Kopie)")}"] = "audio";
            choices[$"📺 Auflösung (Resolution):     {(_scaleTo720p ? "720p (Skaliert, empfohlen)" : "Originalgröße")}"] = "resolution";
            choices[$"🗜️ Kompression (Preset):      {_preset}"] = "preset";
            choices[$"⏳ Zeitbereich (Range):        {rangeText}"] = "range";
            choices[$"✂️ Splitting / Aufteilen:      {splitText}"] = "split";
            choices["⚙️ Benutzerdefinierten FFmpeg-Befehl eingeben..."] = "custom";
        }

        choices["🚀 KONVERTIERUNG STARTEN"] = "start";

        var selection = Ui.Select(
            "FFmpeg Konvertierungs-Dashboard:",
            choices.Select(c => (c.Key, c.Value)),
            backLabel: "🚪 Kehre zum Hauptmenü zurück",
            pageSize: 12);

        return selection.IsValue ? selection.Value! : "exit";
    }

    private void ConfigureSpeed() {
        double val = Ui.Ask<double>($"Neue Geschwindigkeit eingeben (z. B. 1.0, 1.2, 1.3, 1.5) [aktuell: {_speedMultiplier}x]:", _speedMultiplier);
        if (val >= 0.1 && val <= 10.0) {
            _speedMultiplier = val;
            _useCustomTemplate = false;
            Ui.Success($"Geschwindigkeit auf {_speedMultiplier}x gesetzt.");
        }
        else {
            Ui.Warn("Ungültiger Wert.");
        }
    }

    private void ConfigureFps() {
        int val = Ui.Ask<int>($"Bilder pro Sekunde (FPS) eingeben (z. B. 1, 2, 5, 10) [aktuell: {_fps}]:", _fps);
        if (val >= 1 && val <= 60) {
            _fps = val;
            _useCustomTemplate = false;
            Ui.Success($"FPS auf {_fps} gesetzt.");
        }
        else {
            Ui.Warn("Ungültiger Wert.");
        }
    }

    private void ConfigurePreset() {
        string[] presets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
        var choice = Ui.Select("Wähle Kompressions-Voreinstellung (Preset):", presets);
        if (!choice.IsValue) return;

        _preset = choice.Value!;
        _useCustomTemplate = false;
        Ui.Success($"Kompression Preset auf '{_preset}' gesetzt.");
    }

    private void ConfigureTimeRange() {
        Ui.Info("Zeitbereichs-Auswahl (z. B. 00:10:00, 5:30 oder Sekunden)", "Zeitbereich");
        string startInput = Ui.Ask<string>($"Startzeit eingeben [aktuell: {(_startTimeSeconds.HasValue ? _startTimeSeconds.Value.ToString() : "Anfang")}]:", "");
        
        double? startSec = ParseTimeInput(startInput);
        if (startSec.HasValue) {
            _startTimeSeconds = startSec;
        }
        else if (startInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || startInput == "0" || string.IsNullOrEmpty(startInput)) {
            _startTimeSeconds = null;
        }

        string durInput = Ui.Ask<string>($"Dauer eingeben [aktuell: {(_durationSeconds.HasValue ? _durationSeconds.Value.ToString() : "Gesamtes restliches Video")}]:", "");

        double? durSec = ParseTimeInput(durInput);
        if (durSec.HasValue) {
            _durationSeconds = durSec;
        }
        else if (durInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(durInput)) {
            _durationSeconds = null;
        }

        _useCustomTemplate = false;
        Ui.Success("Zeitbereich konfiguriert.");
    }

    private void ConfigureSplitting() {
        int parts = Ui.Ask<int>($"In wie viele Teile soll das Video aufgeteilt werden? (1 = kein Splitting) [aktuell: {_splitParts}]:", _splitParts);
        if (parts >= 1) {
            _splitParts = parts;
            if (_splitParts > 1) {
                double overlap = Ui.Ask<double>($"Überlappungszeit in Sekunden eingeben [aktuell: {_overlapSeconds}s]:", _overlapSeconds);
                if (overlap >= 0) {
                    _overlapSeconds = overlap;
                }
            }
            _useCustomTemplate = false;
            Ui.Success($"Splitting konfiguriert: {_splitParts} Teile.");
        }
    }

    private void ConfigureCustomCommand() {
        Ui.Info("Freier FFmpeg-Befehlsmodus", "Custom Mode");
        Ui.Detail("Tipp: Verwende {0} als Platzhalter für die Input-Datei und {1} für die Output-Datei.");
        Ui.Detail("Beispiel: -i \"{0}\" -vcodec libx264 -preset fast -crf 28 \"{1}\"");
        
        string template = Ui.Ask("Eigenen Befehl eingeben:");
        if (!string.IsNullOrWhiteSpace(template)) {
            _customCommandTemplate = template;
            string ext = Ui.Ask<string>("Ausgabe-Dateiendung eingeben [Standard: .mp4]:", ".mp4");
            if (!ext.StartsWith(".")) ext = "." + ext;
            _customOutputExtension = ext;
            
            _useCustomTemplate = true;
            Ui.Success("Custom Mode aktiviert.");
        }
    }

    private async Task RunConversionProcessAsync(string[] filesToProcess, string destFolder) {
        Ui.Header("🚀 Starte FFmpeg Konvertierungsprozess...");

        foreach (string inputFile in filesToProcess) {
            if (_useCustomTemplate) {
                await FfmpegToolkit.ProcessCustomVideoAsync(inputFile, destFolder, _customCommandTemplate, _customOutputExtension);
            }
            else {
                if (_splitParts > 1) {
                    await FfmpegToolkit.ProcessSplitVideoAsync(
                        inputFile, 
                        destFolder, 
                        parts: _splitParts, 
                        overlapSeconds: _overlapSeconds, 
                        downmixToMono: _downmixToMono, 
                        streamCopy: false, 
                        overwrite: false, 
                        cacheFileNamePrefix: Path.GetFileNameWithoutExtension(inputFile), 
                        preset: _preset
                    );
                }
                else {
                    await FfmpegToolkit.ProcessGeneralVideoAsync(
                        inputFile, 
                        destFolder, 
                        speedMultiplier: _speedMultiplier, 
                        fps: _fps, 
                        downmixToMono: _downmixToMono, 
                        audioSampleRate: 48000, 
                        scaleTo720p: _scaleTo720p, 
                        overwrite: false, 
                        preset: _preset,
                        startTimeSeconds: _startTimeSeconds,
                        durationSeconds: _durationSeconds
                    );
                }
            }
        }
    }

    private static double? ParseTimeInput(string input) {
        if (string.IsNullOrWhiteSpace(input)) return null;
        if (double.TryParse(input, CultureInfo.InvariantCulture, out double secs)) {
            return secs;
        }
        if (TimeSpan.TryParse(input, out TimeSpan ts)) {
            return ts.TotalSeconds;
        }
        var parts = input.Split(':');
        if (parts.Length == 2 && double.TryParse(parts[0], out double m) && double.TryParse(parts[1], out double s)) {
            return m * 60 + s;
        }
        return null;
    }
}
