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
        Console.WriteLine("\n==================================================");
        Console.WriteLine(" 🎬 FFmpeg Console Video Preprocessor Dashboard");
        Console.WriteLine("==================================================");

        // Phase 1: Setup and Validation
        if (!SetupDirectories(out string sourceFolder, out string destFolder)) return;

        // Phase 2: Select conversion target mode (Single File vs. Folder Batch)
        string[] filesToProcess = SelectTargetFiles(sourceFolder);
        if (filesToProcess == null || filesToProcess.Length == 0) {
            Console.WriteLine("Keine Dateien zur Verarbeitung ausgewählt. Breche ab.");
            return;
        }

        // Phase 3: Dashboard loop
        while (true) {
            RenderDashboard(sourceFolder, destFolder, filesToProcess);
            
            Console.Write("\nWahl (1-10) [Standard: 9 (Start)]: ");
            string choice = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(choice)) choice = "9";

            if (choice == "10" || choice.Equals("exit", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine("Breche ab und kehre zum Hauptmenü zurück.");
                return;
            }

            switch (choice) {
                case "1":
                    ConfigureSpeed();
                    break;
                case "2":
                    ConfigureFps();
                    break;
                case "3":
                    _downmixToMono = !_downmixToMono;
                    _useCustomTemplate = false; // reset custom mode if standard settings are modified
                    Console.WriteLine($"\n  [OK] Downmix to Mono: {(_downmixToMono ? "AKTIVIERT (Mono 96k)" : "DEAKTIVIERT (Original Stereo Copy)")}");
                    break;
                case "4":
                    _scaleTo720p = !_scaleTo720p;
                    _useCustomTemplate = false;
                    Console.WriteLine($"\n  [OK] Auf 720p skalieren: {(_scaleTo720p ? "AKTIVIERT" : "DEAKTIVIERT (Original-Auflösung)")}");
                    break;
                case "5":
                    ConfigurePreset();
                    break;
                case "6":
                    ConfigureTimeRange();
                    break;
                case "7":
                    ConfigureSplitting();
                    break;
                case "8":
                    ConfigureCustomCommand();
                    break;
                case "9":
                    // Run the actual conversion process
                    await RunConversionProcessAsync(filesToProcess, destFolder);
                    Console.WriteLine("\nDrücke ENTER um fortzufahren...");
                    Console.ReadLine();
                    return;
                default:
                    Console.WriteLine("Ungültige Auswahl.");
                    break;
            }
        }
    }

    private bool SetupDirectories(out string sourceFolder, out string destFolder) {
        var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
        string currentSource = string.IsNullOrEmpty(DefaultSourceFolder) ? ffmpegConfig.SourceFolder : DefaultSourceFolder;

        // Use our nice folder selector (which now supports predefined folders and explorer!)
        sourceFolder = ConsoleUiHelper.ConfirmOrChangeSourceFolder(currentSource, newFolder => {
            ffmpegConfig.SourceFolder = newFolder;
            ConfigLoader<FfmpegSessionConfig>.Save(ffmpegConfig);
        });

        destFolder = DefaultDestinationFolder;
        if (string.IsNullOrEmpty(destFolder)) {
            destFolder = Path.Combine(sourceFolder, "extracted_output");
        }

        Console.Write($"\nAktueller Zielordner (Destination): {destFolder}\nMöchten Sie diesen Zielordner beibehalten? (j/n, Standard: j): ");
        string? destChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (destChoice == "n" || destChoice == "nein" || destChoice == "no") {
            Console.Write("Neuen Zielordner eingeben: ");
            string newDest = Console.ReadLine()?.Trim() ?? "";
            if (!string.IsNullOrEmpty(newDest)) {
                destFolder = newDest.Trim('\"', '\'');
            }
        }

        if (!Directory.Exists(sourceFolder)) {
            Console.WriteLine($"Error: Quellordner '{sourceFolder}' existiert nicht.");
            return false;
        }

        if (!Directory.Exists(destFolder)) {
            try {
                Console.WriteLine($"Erstelle Zielordner '{destFolder}'...");
                Directory.CreateDirectory(destFolder);
            }
            catch (Exception ex) {
                Console.WriteLine($"Fehler beim Erstellen des Zielordners: {ex.Message}");
                return false;
            }
        }

        return true;
    }

    private static string[] SelectTargetFiles(string sourceFolder) {
        Console.WriteLine("\nDateiauswahl-Modus:");
        Console.WriteLine("  1) Einzelne Videodatei auswählen");
        Console.WriteLine("  2) Alle Videodateien im Quellordner verarbeiten (Batch-Modus)");
        Console.Write("Auswahl (1-2) [Standard: 1]: ");
        
        string choice = Console.ReadLine()?.Trim() ?? "";
        if (choice == "2") {
            return ConsoleUiHelper.SelectBatchFiles(sourceFolder);
        }
        else {
            return ConsoleUiHelper.SelectSingleFile(sourceFolder);
        }
    }

    private void RenderDashboard(string sourceFolder, string destFolder, string[] filesToProcess) {
        Console.WriteLine("\n==================================================");
        Console.WriteLine("        🛠️ FFmpeg Konvertierungs-Dashboard");
        Console.WriteLine("==================================================");
        Console.WriteLine($" 📁 Quellordner: {sourceFolder}");
        Console.WriteLine($" 📁 Zielordner:  {destFolder}");
        Console.WriteLine($" 🎬 Zu verarbeiten ({filesToProcess.Length} Datei(en)):");
        for (int i = 0; i < Math.Min(filesToProcess.Length, 5); i++) {
            Console.WriteLine($"    - {Path.GetFileName(filesToProcess[i])}");
        }
        if (filesToProcess.Length > 5) {
            Console.WriteLine($"    ... und {filesToProcess.Length - 5} weitere.");
        }
        Console.WriteLine("--------------------------------------------------");
        
        if (_useCustomTemplate) {
            Console.WriteLine(" ⚙️ MODUS: KUNDENSPEZIFISCHE FFmpeg Parameter (Custom Mode)");
            Console.WriteLine($"    Befehls-Template: {_customCommandTemplate}");
            Console.WriteLine($"    Ausgabe-Erweiterung: {_customOutputExtension}");
            Console.WriteLine("--------------------------------------------------");
        }
        else {
            string rangeText = (_startTimeSeconds.HasValue || _durationSeconds.HasValue)
                ? $"Start={_startTimeSeconds ?? 0}s, Dauer={_durationSeconds?.ToString() ?? "Rest"}"
                : "Komplettes Video";
            
            string splitText = (_splitParts > 1)
                ? $"{_splitParts} Teile (Überlappung: {_overlapSeconds}s)"
                : "Deaktiviert (Einzelne Datei)";

            Console.WriteLine($" 1) ⚡ Geschwindigkeit (Speed):    {_speedMultiplier:F1}x");
            Console.WriteLine($" 2) 🎞️ Bilder pro Sekunde (FPS):   {_fps} FPS");
            Console.WriteLine($" 3) 🔊 Tonspur-Format (Audio):     {(_downmixToMono ? "Mono (96k AAC, empfohlen)" : "Stereo (Kopie)")}");
            Console.WriteLine($" 4) 📺 Auflösung (Resolution):     {(_scaleTo720p ? "720p (Skaliert, empfohlen)" : "Originalgröße")}");
            Console.WriteLine($" 5) 🗜️ Kompression (Preset):      {_preset}");
            Console.WriteLine($" 6) ⏳ Zeitbereich (Range):        {rangeText}");
            Console.WriteLine($" 7) ✂️ Splitting / Aufteilen:      {splitText}");
            Console.WriteLine(" 8) ⚙️ Benutzerdefinierten FFmpeg-Befehl eingeben...");
            Console.WriteLine("--------------------------------------------------");
        }
        Console.WriteLine(" 9) 🚀 KONVERTIERUNG STARTEN");
        Console.WriteLine(" 10) 🚪 Zurück zum Hauptmenü");
    }

    private void ConfigureSpeed() {
        Console.Write($"\nNeue Geschwindigkeit eingeben (z. B. 1.0, 1.2, 1.3, 1.5) [aktuell: {_speedMultiplier}x]: ");
        string input = Console.ReadLine()?.Trim() ?? "";
        if (double.TryParse(input, CultureInfo.InvariantCulture, out double val) && val >= 0.1 && val <= 10.0) {
            _speedMultiplier = val;
            _useCustomTemplate = false;
            Console.WriteLine($"  [OK] Geschwindigkeit auf {_speedMultiplier}x gesetzt.");
        }
        else if (!string.IsNullOrEmpty(input)) {
            Console.WriteLine("  [ERROR] Ungültiger Wert.");
        }
    }

    private void ConfigureFps() {
        Console.Write($"\nBilder pro Sekunde (FPS) eingeben (z. B. 1, 2, 5, 10) [aktuell: {_fps}]: ");
        string input = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(input, out int val) && val >= 1 && val <= 60) {
            _fps = val;
            _useCustomTemplate = false;
            Console.WriteLine($"  [OK] FPS auf {_fps} gesetzt.");
        }
        else if (!string.IsNullOrEmpty(input)) {
            Console.WriteLine("  [ERROR] Ungültiger Wert.");
        }
    }

    private void ConfigurePreset() {
        string[] presets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
        Console.WriteLine("\nWähle Kompressions-Voreinstellung (Preset):");
        for (int i = 0; i < presets.Length; i++) {
            string activeMark = presets[i] == _preset ? " *" : "";
            Console.WriteLine($"  {i + 1}) {presets[i]}{activeMark}");
        }
        Console.Write($"Auswahl (1-{presets.Length}) [aktuell: {_preset}]: ");
        string input = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(input, out int choice) && choice >= 1 && choice <= presets.Length) {
            _preset = presets[choice - 1];
            _useCustomTemplate = false;
            Console.WriteLine($"  [OK] Kompression Preset auf '{_preset}' gesetzt.");
        }
    }

    private void ConfigureTimeRange() {
        Console.WriteLine("\n--- Zeitbereichs-Auswahl (z. B. 00:10:00, 5:30 oder Sekunden) ---");
        Console.Write($"Startzeit eingeben [aktuell: {(_startTimeSeconds.HasValue ? _startTimeSeconds.Value.ToString() : "Anfang")}]: ");
        string startInput = Console.ReadLine()?.Trim() ?? "";
        
        double? startSec = ParseTimeInput(startInput);
        if (startSec.HasValue) {
            _startTimeSeconds = startSec;
        }
        else if (startInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || startInput == "0" || string.IsNullOrEmpty(startInput)) {
            _startTimeSeconds = null;
        }

        Console.Write($"Dauer eingeben [aktuell: {(_durationSeconds.HasValue ? _durationSeconds.Value.ToString() : "Gesamtes restliches Video")}]: ");
        string durInput = Console.ReadLine()?.Trim() ?? "";

        double? durSec = ParseTimeInput(durInput);
        if (durSec.HasValue) {
            _durationSeconds = durSec;
        }
        else if (durInput.Equals("clear", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(durInput)) {
            _durationSeconds = null;
        }

        _useCustomTemplate = false;
        Console.WriteLine("  [OK] Zeitbereich konfiguriert.");
    }

    private void ConfigureSplitting() {
        Console.Write("\nIn wie viele Teile soll das Video aufgeteilt werden? (1 = kein Splitting) [aktuell: " + _splitParts + "]: ");
        string input = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(input, out int parts) && parts >= 1) {
            _splitParts = parts;
            if (_splitParts > 1) {
                Console.Write($"Überlappungszeit in Sekunden eingeben [aktuell: {_overlapSeconds}s]: ");
                string overlapInput = Console.ReadLine()?.Trim() ?? "";
                if (double.TryParse(overlapInput, CultureInfo.InvariantCulture, out double overlap) && overlap >= 0) {
                    _overlapSeconds = overlap;
                }
            }
            _useCustomTemplate = false;
            Console.WriteLine($"  [OK] Splitting konfiguriert: {_splitParts} Teile.");
        }
    }

    private void ConfigureCustomCommand() {
        Console.WriteLine("\n--- Freier FFmpeg-Befehlsmodus ---");
        Console.WriteLine("Tipp: Verwende {0} als Platzhalter für die Input-Datei und {1} für die Output-Datei.");
        Console.WriteLine("Beispiel: -i \"{0}\" -vcodec libx264 -preset fast -crf 28 \"{1}\"");
        Console.Write("Eigenen Befehl eingeben: ");
        string template = Console.ReadLine() ?? "";
        if (!string.IsNullOrWhiteSpace(template)) {
            _customCommandTemplate = template;
            Console.Write("Ausgabe-Dateiendung eingeben (Standard: .mp4): ");
            string ext = Console.ReadLine()?.Trim() ?? ".mp4";
            if (!ext.StartsWith(".")) ext = "." + ext;
            _customOutputExtension = ext;
            
            _useCustomTemplate = true;
            Console.WriteLine("  [OK] Custom Mode aktiviert.");
        }
    }

    private async Task RunConversionProcessAsync(string[] filesToProcess, string destFolder) {
        Console.WriteLine("\n==================================================");
        Console.WriteLine(" 🚀 Starte FFmpeg Konvertierungsprozess...");
        Console.WriteLine("==================================================");

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
