using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Text; // Added for StringBuilder
using System.Threading.Tasks;

namespace LectureExtraction.Media;


/// <summary>
/// Core FFmpeg toolset. Independent of any console/interactive logic.
/// Can be safely called from background tasks, DirectAIInteraction, or APIs.
/// [Human] Hier passiert die wahre Magie! Diese Klasse baut die exakten FFmpeg-Befehle zusammen und führt sie aus.
/// </summary>
public static class FfmpegToolkit {
    /// <summary>
    /// [AI Context] Splits long lecture videos into smaller segments with overlapping audio/video.
    /// This ensures the AI model doesn't miss any spoken sentences or context right at the cut points.
    /// [Human] Schneidet große Videos in Stücke, lässt aber die Enden "überlappen", damit die KI beim Wechsel keinen Satz verpasst.
    /// </summary>
    public static async Task<List<VideoSegment>> ProcessSplitVideoAsync(string inputFile, string destFolder, int parts = 3, double overlapSeconds = 180, bool downmixToMono = false, bool streamCopy = false, bool overwrite = false, string? cacheFileNamePrefix = null, string preset = "fast") {
        var generatedFiles = new List<VideoSegment>();

        if (!File.Exists(inputFile)) {
            Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
            return generatedFiles;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        double duration = await GetVideoDurationAsync(inputFile);

        if (duration <= 0) {
            Console.WriteLine($"\n  [FFmpegToolkit] Error: Could not determine video duration for '{fileName}'.");
            return generatedFiles;
        }

        Console.WriteLine($"\n  [FFmpegToolkit] Splitting into {parts} parts: {Path.GetFileName(inputFile)} (Total Duration: {duration:F2}s)");

        // [AI Context] Mono audio effectively halves the bandwidth and token size for speech-to-text models
        // without losing any transcription accuracy. The 'aformat' filter enforces correct metadata.
        // [Human] KI-Spracherkennung braucht kein Stereo. Mono spart uns gigantische Mengen an Tokens, Geld und Upload-Zeit.
        string audioArgs = downmixToMono ? "-c:a aac -b:a 96k -ac 1 -ar 48000 -af \"aformat=channel_layouts=mono\"" : "-c:a copy";

        if (duration <= overlapSeconds * 2 || parts <= 1) {
            Console.WriteLine("  Warning: Video is too short to meaningfully split (or parts=1). Processing as a single file.");
            string outputFile = overwrite ? Path.Combine(destFolder, $"{fileName}-compressed.mp4") : GetUniqueFilePath(destFolder, $"{fileName}-compressed", ".mp4");
            string ffmpegArgs = streamCopy ? $"-i \"{inputFile}\" -c copy \"{outputFile}\"" : $"-i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -preset {preset} -crf 28 -tune stillimage -g 30 {audioArgs} -r 1 \"{outputFile}\"";

            if (await RunFfmpegAsync(ffmpegArgs)) generatedFiles.Add(new VideoSegment(outputFile, 0));
            return generatedFiles;
        }

        double segmentLength = (duration + (parts - 1) * overlapSeconds) / parts;

        for (int i = 0; i < parts; i++) {
            double start = i * (segmentLength - overlapSeconds);
            double end = start + segmentLength;
            if (end > duration) end = duration;

            string outputBaseName = cacheFileNamePrefix ?? fileName; // Use explicit prefix if provided, else use original filename
            string outputFile = overwrite ? Path.Combine(destFolder, $"{outputBaseName}-part{i + 1}.mp4") : GetUniqueFilePath(destFolder, $"{outputBaseName}-part{i + 1}", ".mp4");
            string ffmpegArgs = streamCopy ? $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -c copy \"{outputFile}\"" : $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -preset {preset} -crf 28 -tune stillimage -g 120 {audioArgs} -r 1 \"{outputFile}\"";

            Console.WriteLine($"\n  [FFmpegToolkit] Part {i + 1}/{parts}: Start={start:F2}s, End={end:F2}s");
            if (!await RunFfmpegAsync(ffmpegArgs)) {
                Console.WriteLine($"  [FAILED] Error processing Part {i + 1}.");
            }
            else {
                Console.WriteLine($"  [SUCCESS] Part {i + 1} completed => {outputFile}");
                generatedFiles.Add(new VideoSegment(outputFile, start));
            }
        }
        return generatedFiles;
    }

    /// <summary>
    /// [AI Context] A highly flexible generic method to prepare videos for AI analysis.
    /// Adjusts speed (atempo), drops framerate (fps=1), and downmixes audio to mono to minimize token usage
    /// while preserving perfectly understandable speech and legible board states.
    /// [Human] Der Standard-Prozess: Macht das Video schneller, reduziert es auf 1 Bild pro Sekunde (reicht für Tafeln!) und macht Audio zu Mono.
    /// </summary>
    public static async Task<string?> ProcessGeneralVideoAsync(
        string inputFile, 
        string destFolder, 
        double speedMultiplier = 1.0, 
        int fps = 1, 
        bool downmixToMono = true, 
        int? audioSampleRate = 48000, 
        bool scaleTo720p = false, 
        bool overwrite = false, 
        string preset = "fast",
        double? startTimeSeconds = null,
        double? durationSeconds = null) {
        if (!File.Exists(inputFile)) {
            Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
            return null;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string speedStr = speedMultiplier.ToString(CultureInfo.InvariantCulture);
        
        string rangeSuffix = "";
        if (startTimeSeconds.HasValue || durationSeconds.HasValue) {
            string startStr = startTimeSeconds.HasValue ? startTimeSeconds.Value.ToString(CultureInfo.InvariantCulture) : "0";
            string durStr = durationSeconds.HasValue ? durationSeconds.Value.ToString(CultureInfo.InvariantCulture) : "full";
            rangeSuffix = $"-range-{startStr}-{durStr}";
        }
        
        string outputFile = overwrite ? Path.Combine(destFolder, $"{fileName}-speed-{speedStr}{rangeSuffix}-compressed.mp4") : GetUniqueFilePath(destFolder, $"{fileName}-speed-{speedStr}{rangeSuffix}-compressed", ".mp4");

        // 1. Video Filter zusammenbauen
        // [AI Context] fps=1 is optimal for lectures; AI doesn't need 30fps to read a blackboard.
        // setpts adjusts the video timestamps so it stays in perfect sync with the sped-up audio.
        string videoFilter = $"fps={fps}";
        if (speedMultiplier != 1.0) {
            double ptsMultiplier = 1.0 / speedMultiplier;
            videoFilter = $"setpts={ptsMultiplier.ToString(CultureInfo.InvariantCulture)}*PTS,{videoFilter}";
        }
        if (scaleTo720p) {
            videoFilter += ",scale=-2:720";
        }

        // 2. Audio Parameter zusammenbauen
        string audioArgs = "-c:a copy";
        string audioFilter = "";

        // Wenn wir Speed ändern, in Mono konvertieren oder die Samplerate ändern wollen, müssen wir recoden (aac)
        if (downmixToMono || speedMultiplier != 1.0 || audioSampleRate.HasValue) {
            audioArgs = "-c:a aac -b:a 96k";

            if (downmixToMono) {
                audioArgs += " -ac 1";
                // Forces the container metadata to correctly report 'Mono' to prevent players like VLC 
                // or AI APIs from misinterpreting it as stereo.
                audioFilter += "aformat=channel_layouts=mono";
            }
            if (speedMultiplier != 1.0) {
                if (!string.IsNullOrEmpty(audioFilter)) audioFilter += ",";
                // [AI Context] atempo speeds up the audio WITHOUT changing the pitch (chipmunk effect), 
                // which is absolutely crucial for the AI's speech recognition to keep working reliably.
                audioFilter += $"atempo={speedMultiplier.ToString(CultureInfo.InvariantCulture)}";
            }
            if (audioSampleRate.HasValue) audioArgs += $" -ar {audioSampleRate.Value}";
            if (!string.IsNullOrEmpty(audioFilter)) audioArgs += $" -af \"{audioFilter}\"";
        }

        string rangeArgs = "";
        if (startTimeSeconds.HasValue) {
            rangeArgs += $"-ss {startTimeSeconds.Value.ToString(CultureInfo.InvariantCulture)} ";
        }
        if (durationSeconds.HasValue) {
            rangeArgs += $"-t {durationSeconds.Value.ToString(CultureInfo.InvariantCulture)} ";
        }

        // [AI Context] -g 30 allows for efficient inter-frame compression.
        // -crf 28, -preset veryslow and -tune stillimage drastically reduce file size for static lecture recordings.
        string ffmpegArgs = $"{rangeArgs}-i \"{inputFile}\" -vf \"{videoFilter}\" -c:v libx264 -preset {preset} -crf 28 -tune stillimage -g 120 {audioArgs} -r {fps} \"{outputFile}\"";

        Console.WriteLine($"\n  [FFmpegToolkit] Processing AI Video ({speedMultiplier}x Speed, {fps} FPS, Preset={preset}): {Path.GetFileName(inputFile)}...");
        if (startTimeSeconds.HasValue || durationSeconds.HasValue) {
            Console.WriteLine($"                  Time Range: Start={startTimeSeconds ?? 0}s, Duration={durationSeconds?.ToString() ?? "Remainder"}s");
        }
        
        if (await RunFfmpegAsync(ffmpegArgs)) {
            Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
            return outputFile;
        }
        return null;
    }

    /// <summary>
    /// Legacy/Hardcoded fast 720p profile for standard batch processing with strict bitrates.
    /// [Human] Alter, fester Code von früher. Eher für den menschlichen Gebrauch als für die KI gedacht.
    /// </summary>
    public static async Task<bool> LegacyCodeProcessFast720pVideoAsync(string inputFile, string destFolder) {
        if (!File.Exists(inputFile)) {
            Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-speed-1.5-720p-compressed", ".mp4");

        // Hardcodierte Parameter für 720p, 1.5x Speed und 1 FPS
        string ffmpegArgs = $"-i \"{inputFile}\" -vf \"setpts=0.666667*PTS,scale=1280:720,fps=1\" -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -g 1 -c:a aac -b:a 192k -ac 1 -ar 48000 -af \"aformat=channel_layouts=mono,atempo=1.5\" -r 1 \"{outputFile}\"";

        Console.WriteLine($"\n  [FFmpegToolkit] Processing (Fast 720p): {Path.GetFileName(inputFile)}...");

        if (await RunFfmpegAsync(ffmpegArgs)) {
            Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Executes custom, raw FFmpeg commands supplied directly by the user.
    /// [Human] Führt komplett frei von dir eingetippte FFmpeg-Parameter aus.
    /// </summary>
    public static async Task<bool> ProcessCustomVideoAsync(string inputFile, string destFolder, string commandTemplate, string outputExtension) {
        if (!File.Exists(inputFile)) {
            Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-custom", outputExtension);
        string ffmpegArgs = string.Format(commandTemplate, inputFile, outputFile);

        Console.WriteLine($"\n  [FFmpegToolkit] Processing (Custom): {Path.GetFileName(inputFile)}...");

        if (await RunFfmpegAsync(ffmpegArgs)) {
            Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// [AI Context] Extracts only the audio track as a highly compressed AAC. Useful for purely audio-based AI models 
    //  (e.g., standard Whisper) or to provide the user with a standalone podcast version of the lecture.
    /// [Human] Extrahiert die reine Tonspur als AAC. Perfekt, wenn man sich die Vorlesung nur anhören möchte (Podcast-Style) oder reine Audio-KIs nutzt.
    /// </summary>
    public static async Task<bool> ExtractAudioAsAacAsync(string inputFile, string destFolder) {
        if (!File.Exists(inputFile)) {
            Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_audio", ".aac");
        string arguments = $"-y -i \"{inputFile}\" -vn -c:a aac -b:a 96k -ac 1 -ar 48000 \"{outputFile}\"";

        Console.WriteLine($"\n  [FFmpegToolkit] Extracting AAC: {Path.GetFileName(inputFile)}...");

        if (await RunFfmpegAsync(arguments)) {
            Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Generates a unique file path by appending '-copy-X' if a file with the same name already exists.
    /// Protects user data from being accidentally overwritten.
    /// </summary>
    private static string GetUniqueFilePath(string destFolder, string baseName, string extension) {
        string fullPath = Path.Combine(destFolder, $"{baseName}{extension}");
        int copyIndex = 1;

        while (File.Exists(fullPath)) {
            fullPath = Path.Combine(destFolder, $"{baseName}-copy-{copyIndex}{extension}");
            copyIndex++;
        }

        return fullPath;
    }

    /// <summary>
    /// Uses ffprobe to securely extract the precise duration of the media file in seconds.
    /// </summary>
    public static async Task<double> GetVideoDurationAsync(string filePath) {
        if (!File.Exists(filePath)) {
            Console.WriteLine($"\n  [ffprobe error] File not found: '{filePath}'");
            return -1;
        }

        var startInfo = new ProcessStartInfo {
            FileName = "ffprobe",
            Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try {
            using var process = Process.Start(startInfo);
            if (process == null) return -1;

            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double duration)) {
                return duration;
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2) // Error code 2: ERROR_FILE_NOT_FOUND
            {
                Console.WriteLine("  [ffprobe error] 'ffprobe' konnte nicht gefunden werden.");
                Console.WriteLine("  Bitte stellen Sie sicher, dass FFmpeg (inkl. ffprobe) installiert und im System-PATH konfiguriert ist.");
            }
            else {
                Console.WriteLine($"  [ffprobe error] Ein Fehler ist beim Ausführen von ffprobe aufgetreten.");
            }
        }
        return -1;
    }

    /// <summary>
    /// Wraps the execution of the FFmpeg process.
    /// Silences normal output but captures and reports the StandardError stream if a crash occurs.
    /// </summary>
    private static async Task<bool> RunFfmpegAsync(string arguments) {
        var processInfo = new ProcessStartInfo {
            FileName = "ffmpeg",
            Arguments = $"-y -nostdin -hide_banner -loglevel warning -stats {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true, // Wir leiten um und leeren den Puffer aktiv
            UseShellExecute = false,
            CreateNoWindow = true
        };

        StringBuilder outputBuilder = new();
        StringBuilder errorBuilder = new();

        // Store the full FFmpeg command for debugging purposes
        string debugCmd = $"ffmpeg -y -nostdin -hide_banner -loglevel warning -stats {arguments}";
        Console.WriteLine($"  [DEBUG CMD] {debugCmd}");

        try {
            using var process = new Process { StartInfo = processInfo };

            // Attach event handlers for asynchronous output reading
            process.OutputDataReceived += (sender, e) => {
                // FFmpeg's -stats output often uses carriage returns to overwrite the same line.
                // We don't want to log every single progress update line for general output,
                // but we collect everything for the full log if there's an error.
                if (!string.IsNullOrEmpty(e.Data)) {
                    outputBuilder.AppendLine(e.Data);
                    // For real-time progress, you might print e.Data here, but it can be noisy.
                    // Console.Write($"\r{e.Data.TrimEnd()}"); // Example for progress updates
                }
            };
            process.ErrorDataReceived += (sender, e) => {
                // FFmpeg writes actual errors and some progress (e.g., about codecs) to StandardError.
                if (!string.IsNullOrEmpty(e.Data)) {
                    errorBuilder.AppendLine(e.Data);
                    // FFmpeg -stats output (progress) is typically sent to stderr.
                    // Progress lines usually contain 'frame=', 'speed=', or 'time='.
                    // We suppress these verbose progress updates for a cleaner console.
                    // If specific non-progress stderr messages (warnings/errors) are desired in real-time,
                    // they can be selectively printed here, but for now, only non-progress specific
                    // output or final errors will be shown.
                    if (!(e.Data.Contains("frame=") || e.Data.Contains("speed=") || e.Data.Contains("time="))) {
                        // Print other stderr output (warnings/errors) with a newline
                        Console.WriteLine($"  [FFmpeg STDERR] {e.Data}");
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine(); // Start asynchronous reading of StandardOutput
            process.BeginErrorReadLine();  // Start asynchronous reading of StandardError

            await process.WaitForExitAsync(); // Wait for the process to complete

            Console.WriteLine("  [FFmpeg] Process finished.");

            if (process.ExitCode != 0) {
                Console.WriteLine($"\n  [FFmpeg Error] FFmpeg wurde mit Fehlercode {process.ExitCode} beendet.");
                if (errorBuilder.Length > 0) {
                    Console.WriteLine("  [FFmpeg STDERR Full Log]");
                    Console.WriteLine(errorBuilder.ToString());
                }
                if (outputBuilder.Length > 0) {
                    Console.WriteLine("  [FFmpeg STDOUT Full Log]");
                    Console.WriteLine(outputBuilder.ToString());
                }
                return false;
            }
            return true;
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2) // Error code 2: ERROR_FILE_NOT_FOUND
            {
                Console.WriteLine("  [FFmpeg error] 'ffmpeg' konnte nicht gefunden werden.");
                Console.WriteLine("  Bitte stellen Sie sicher, dass FFmpeg installiert und im System-PATH konfiguriert ist.");
            }
            else {
                Console.WriteLine($"  [FFmpeg error] Ein Fehler ist beim Starten von FFmpeg aufgetreten.");
            }
            return false;
        }
    }
}


/*

Komprimieren auf 1 frame pro sekunde
ffmpeg -i "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\monday-part-2.mp4" -vf "setpts=0.66*PTS,scale=1280:720,fps=1" -r 1 -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -c:a aac -b:a 192k -ac 1 -af "atempo=2" "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\monday-part-2-compressed.mp4"

Nochmal komprimieren wie oben, andere datei:
ffmpeg -i "D:\gemin-upload-folder\2-16-monday-full.mp4" -vf "setpts=0.66*PTS,scale=1280:720,fps=1" -r 1 -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -c:a aac -b:a 192k -ac 1 -af "atempo=2" "D:\gemin-upload-folder\2-16-monday-compressed.mp4"

Audio 96k herausholen:
ffmpeg -i "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\monday-part-2.mp4" -vn -c:a aac -b:a 96k -ac 1 -af "atempo=2.5" "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\96k-monday-part-2.aac"

Audio 128k herausholen:
ffmpeg -i "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\monday-part-2.mp4" -vn -c:a aac -b:a 128k -ac 1 -af "atempo=2" "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\128k-monday-part-2.aac"


ffmpeg -i "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\monday-part-2.mp4" -vf "setpts=0.5*PTS,scale=1280:720,fps=1" -r 1 -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -c:a aac -b:a 192k -ac 1 -af "atempo=2" "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\net10.0\runtimes\win\lib\net7.0\monday-part-2-compressed.mp4"

*/