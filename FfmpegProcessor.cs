using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

public class FfmpegProcessor
{
    // [AI Context] Konfigurierbare Pfade für die FFmpeg-Verarbeitung.
    // SourceFolderPath ist der Ordner, in dem nach Videodateien gesucht wird.
    // TargetFolderPath ist der Ordner, in dem die verarbeiteten Audiodateien gespeichert werden.
    // Beide müssen absolute Pfade sein.

    // Absoluter Pfad zum Quellordner mit den Videodateien.
    // Z.B.: @"C:\Users\miche\Videos\Input"
    private readonly string SourceFolderPath;

    // Absoluter Pfad zum Zielordner für die extrahierten MP3-Dateien.
    // Z.B.: @"C:\Users\miche\Music\Output"
    private readonly string TargetFolderPath;

    public FfmpegProcessor(string sourceFolder, string targetFolder)
    {
        SourceFolderPath = sourceFolder;
        TargetFolderPath = targetFolder;
    }

    /// <summary>
    /// Startet eine interaktive Konsole zur Steuerung der FFmpeg-Aufgaben.
    /// </summary>
    public async Task StartInteractiveAsync()
    {
        Console.WriteLine($"\n--- FFmpeg Manager gestartet ---");
        Console.WriteLine($"Quellordner: {(string.IsNullOrWhiteSpace(SourceFolderPath) ? "[NICHT KONFIGURIERT]" : SourceFolderPath)}");
        Console.WriteLine($"Zielordner:  {(string.IsNullOrWhiteSpace(TargetFolderPath) ? "[NICHT KONFIGURIERT]" : TargetFolderPath)}");

        while (true)
        {
            Console.WriteLine("\nBefehle:");
            Console.WriteLine("  1 / show    -> Zeigt alle Dateien im Zielordner an");
            Console.WriteLine("  2 / process -> Startet die Verarbeitung (MP4 -> MP3) vom Quell- in den Zielordner");
            Console.WriteLine("  exit / quit -> Beendet den FFmpeg Manager");
            Console.Write("\nFFmpeg> ");

            string? input = Console.ReadLine()?.Trim().ToLower();

            if (input == "exit" || input == "quit") break;

            if (input == "1" || input == "show")
            {
                ShowFiles();
            }
            else if (input == "2" || input == "process")
            {
                await ProcessFolderAsync();
            }
            else
            {
                Console.WriteLine("Unbekannter Befehl.");
            }
        }
    }

    private void ShowFiles()
    {
        if (string.IsNullOrWhiteSpace(SourceFolderPath) || !Directory.Exists(SourceFolderPath))
        {
            Console.WriteLine($"[Fehler] Der Quellordner existiert nicht oder ist nicht konfiguriert: '{SourceFolderPath}'");
            return;
        }

        string[] files = Directory.GetFiles(SourceFolderPath);
        if (files.Length == 0)
        {
            Console.WriteLine($"[INFO] Keine Dateien im Ordner gefunden: {SourceFolderPath}");
            return;
        }

        Console.WriteLine($"\nDateien in '{SourceFolderPath}':");
        foreach (var file in files)
        {
            Console.WriteLine($" - {Path.GetFileName(file)}");
        }
    }

    /// <summary>
    /// Sucht nach Videodateien im konfigurierten Ordner und wendet FFmpeg-Befehle darauf an.
    /// </summary>
    public async Task ProcessFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceFolderPath) || !Directory.Exists(SourceFolderPath))
        {
            Console.WriteLine($"[Fehler] Der Quellordner existiert nicht oder ist nicht konfiguriert: '{SourceFolderPath}'");
            return;
        }
        if (string.IsNullOrWhiteSpace(TargetFolderPath) || !Directory.Exists(TargetFolderPath))
        {
            Console.WriteLine($"[Fehler] Der Zielordner existiert nicht oder ist nicht konfiguriert: '{TargetFolderPath}'");
            return;
        }

        // Filter anpassen, falls du auch andere Formate wie .mkv oder .avi verarbeiten möchtest
        string[] videoFiles = Directory.GetFiles(SourceFolderPath, "*.mp4"); 
        
        if (videoFiles.Length == 0)
        {
            Console.WriteLine($"[INFO] Keine MP4-Dateien im Ordner gefunden: {SourceFolderPath}");
            return;
        }

        foreach (var videoFile in videoFiles)
        {
            Console.WriteLine($"\n[FFMPEG] Verarbeite Video: {Path.GetFileName(videoFile)}...");

            // Beispiel-Befehl: Audio als MP3 extrahieren.
            // Passe den Argument-String an deine genauen Bedürfnisse an.
            string outputFile = Path.Combine(TargetFolderPath, Path.GetFileNameWithoutExtension(videoFile) + "_audio.mp3");
            string arguments = $"-y -i \"{videoFile}\" -vn -acodec libmp3lame -q:a 2 \"{outputFile}\"";

            await RunFfmpegCommandAsync(arguments);
        }
    }

    private async Task RunFfmpegCommandAsync(string arguments)
    {
        var processInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg", // Setzt voraus, dass FFmpeg in den System-Umgebungsvariablen (PATH) eingetragen ist.
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // FFmpeg schreibt seinen Log-Output standardmäßig nach StdErr
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processInfo };

        try
        {
            process.Start();

            // Den Output asynchron lesen, um Stream-Deadlocks zu vermeiden
            var readErrorTask = process.StandardError.ReadToEndAsync();
            var readOutputTask = process.StandardOutput.ReadToEndAsync();

            await process.WaitForExitAsync();

            string errorOutput = await readErrorTask;
            string standardOutput = await readOutputTask;

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[Fehler] FFmpeg wurde mit Code {process.ExitCode} beendet.");
                // Optional: Bei Bedarf `errorOutput` in der Konsole ausgeben, um herauszufinden, warum FFmpeg fehlschlug.
                // Console.WriteLine(errorOutput);
            }
            else
            {
                Console.WriteLine("[Erfolg] FFmpeg-Befehl erfolgreich ausgeführt.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ausnahme] Fehler beim Ausführen von FFmpeg: {ex.Message}");
            Console.WriteLine("Stelle sicher, dass FFmpeg auf dem System installiert und im PATH verfügbar ist.");
        }
    }
}