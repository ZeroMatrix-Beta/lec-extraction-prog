using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Media;

/// <summary>
/// Core FFmpeg toolset. Independent of any console/interactive logic.
/// Can be safely called from background tasks, DirectAIInteraction, or APIs.
/// [Human] Hier passiert die wahre Magie! Diese Klasse baut die exakten FFmpeg-Befehle zusammen und führt sie aus.
/// </summary>
public static class FfmpegToolkit {
    public static async Task<List<VideoSegment>> ProcessSplitVideoAsync(string inputFile, string destFolder, int parts = 3, double overlapSeconds = 180, bool downmixToMono = false, bool streamCopy = false, bool overwrite = false, string? cacheFileNamePrefix = null, string preset = "fast") {
        var generatedFiles = new List<VideoSegment>();

        if (!File.Exists(inputFile)) {
            Ui.Error($"Input file not found: '{inputFile}'", "FFmpegToolkit");
            return generatedFiles;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        double duration = await GetVideoDurationAsync(inputFile);

        if (duration <= 0) {
            Ui.Error($"Could not determine video duration for '{fileName}'.", "FFmpegToolkit");
            return generatedFiles;
        }

        Ui.Info($"Splitting into {parts} parts: {Path.GetFileName(inputFile)} (Total Duration: {duration:F2}s)", "FFmpegToolkit");

        string audioArgs = downmixToMono ? "-c:a aac -b:a 96k -ac 1 -ar 48000 -af \"aformat=channel_layouts=mono\"" : "-c:a copy";

        if (duration <= overlapSeconds * 2 || parts <= 1) {
            Ui.Warn("Video is too short to meaningfully split (or parts=1). Processing as a single file.", "FFmpegToolkit");
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

            string outputBaseName = cacheFileNamePrefix ?? fileName;
            string outputFile = overwrite ? Path.Combine(destFolder, $"{outputBaseName}-part{i + 1}.mp4") : GetUniqueFilePath(destFolder, $"{outputBaseName}-part{i + 1}", ".mp4");
            string ffmpegArgs = streamCopy ? $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -c copy \"{outputFile}\"" : $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -preset {preset} -crf 28 -tune stillimage -g 120 {audioArgs} -r 1 \"{outputFile}\"";

            Ui.Info($"Part {i + 1}/{parts}: Start={start:F2}s, End={end:F2}s", "FFmpegToolkit");
            if (!await RunFfmpegAsync(ffmpegArgs)) {
                Ui.Error($"Error processing Part {i + 1}.", "FFmpegToolkit");
            }
            else {
                Ui.Success($"Part {i + 1} completed => {outputFile}", "FFmpegToolkit");
                generatedFiles.Add(new VideoSegment(outputFile, start));
            }
        }
        return generatedFiles;
    }

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
            Ui.Error($"Input file not found: '{inputFile}'", "FFmpegToolkit");
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

        string videoFilter = $"fps={fps}";
        if (speedMultiplier != 1.0) {
            double ptsMultiplier = 1.0 / speedMultiplier;
            videoFilter = $"setpts={ptsMultiplier.ToString(CultureInfo.InvariantCulture)}*PTS,{videoFilter}";
        }
        if (scaleTo720p) {
            videoFilter += ",scale=-2:720";
        }

        string audioArgs = "-c:a copy";
        string audioFilter = "";

        if (downmixToMono || speedMultiplier != 1.0 || audioSampleRate.HasValue) {
            audioArgs = "-c:a aac -b:a 96k";

            if (downmixToMono) {
                audioArgs += " -ac 1";
                audioFilter += "aformat=channel_layouts=mono";
            }
            if (speedMultiplier != 1.0) {
                if (!string.IsNullOrEmpty(audioFilter)) audioFilter += ",";
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

        string ffmpegArgs = $"{rangeArgs}-i \"{inputFile}\" -vf \"{videoFilter}\" -c:v libx264 -preset {preset} -crf 28 -tune stillimage -g 120 {audioArgs} -r {fps} \"{outputFile}\"";

        Ui.Info($"Processing AI Video ({speedMultiplier}x Speed, {fps} FPS, Preset={preset}): {Path.GetFileName(inputFile)}...", "FFmpegToolkit");
        if (startTimeSeconds.HasValue || durationSeconds.HasValue) {
            Ui.Detail($"Time Range: Start={startTimeSeconds ?? 0}s, Duration={durationSeconds?.ToString() ?? "Remainder"}s");
        }
        
        if (await RunFfmpegAsync(ffmpegArgs)) {
            Ui.Success($"=> TO: {outputFile}", "FFmpegToolkit");
            return outputFile;
        }
        return null;
    }

    public static async Task<bool> LegacyCodeProcessFast720pVideoAsync(string inputFile, string destFolder) {
        if (!File.Exists(inputFile)) {
            Ui.Error($"Input file not found: '{inputFile}'", "FFmpegToolkit");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-speed-1.5-720p-compressed", ".mp4");

        string ffmpegArgs = $"-i \"{inputFile}\" -vf \"setpts=0.666667*PTS,scale=1280:720,fps=1\" -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -g 1 -c:a aac -b:a 192k -ac 1 -ar 48000 -af \"aformat=channel_layouts=mono,atempo=1.5\" -r 1 \"{outputFile}\"";

        Ui.Info($"Processing (Fast 720p): {Path.GetFileName(inputFile)}...", "FFmpegToolkit");

        if (await RunFfmpegAsync(ffmpegArgs)) {
            Ui.Success($"=> TO: {outputFile}", "FFmpegToolkit");
            return true;
        }
        return false;
    }

    public static async Task<bool> ProcessCustomVideoAsync(string inputFile, string destFolder, string commandTemplate, string outputExtension) {
        if (!File.Exists(inputFile)) {
            Ui.Error($"Input file not found: '{inputFile}'", "FFmpegToolkit");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-custom", outputExtension);
        string ffmpegArgs = string.Format(commandTemplate, inputFile, outputFile);

        Ui.Info($"Processing (Custom): {Path.GetFileName(inputFile)}...", "FFmpegToolkit");

        if (await RunFfmpegAsync(ffmpegArgs)) {
            Ui.Success($"=> TO: {outputFile}", "FFmpegToolkit");
            return true;
        }
        return false;
    }

    public static async Task<bool> ExtractAudioAsAacAsync(string inputFile, string destFolder) {
        if (!File.Exists(inputFile)) {
            Ui.Error($"Input file not found: '{inputFile}'", "FFmpegToolkit");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_audio", ".aac");
        string arguments = $"-y -i \"{inputFile}\" -vn -c:a aac -b:a 96k -ac 1 -ar 48000 \"{outputFile}\"";

        Ui.Info($"Extracting AAC: {Path.GetFileName(inputFile)}...", "FFmpegToolkit");

        if (await RunFfmpegAsync(arguments)) {
            Ui.Success($"=> TO: {outputFile}", "FFmpegToolkit");
            return true;
        }

        return false;
    }

    private static string GetUniqueFilePath(string destFolder, string baseName, string extension) {
        string fullPath = Path.Combine(destFolder, $"{baseName}{extension}");
        int copyIndex = 1;

        while (File.Exists(fullPath)) {
            fullPath = Path.Combine(destFolder, $"{baseName}-copy-{copyIndex}{extension}");
            copyIndex++;
        }

        return fullPath;
    }

    public static async Task<double> GetVideoDurationAsync(string filePath) {
        if (!File.Exists(filePath)) {
            Ui.Error($"File not found: '{filePath}'", "ffprobe");
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
            Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}");
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                Ui.Error("'ffprobe' konnte nicht gefunden werden. Bitte stellen Sie sicher, dass FFmpeg (inkl. ffprobe) installiert und im System-PATH konfiguriert ist.", "ffprobe");
            }
            else {
                Ui.Error("Ein Fehler ist beim Ausführen von ffprobe aufgetreten.", "ffprobe");
            }
        }
        return -1;
    }

    private static async Task<bool> RunFfmpegAsync(string arguments) {
        var processInfo = new ProcessStartInfo {
            FileName = "ffmpeg",
            Arguments = $"-y -nostdin -hide_banner -loglevel warning -stats {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        StringBuilder outputBuilder = new();
        StringBuilder errorBuilder = new();

        string debugCmd = $"ffmpeg -y -nostdin -hide_banner -loglevel warning -stats {arguments}";
        Ui.Detail($"[DEBUG CMD] {debugCmd}");

        try {
            using var process = new Process { StartInfo = processInfo };

            process.OutputDataReceived += (sender, e) => {
                if (!string.IsNullOrEmpty(e.Data)) {
                    outputBuilder.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (sender, e) => {
                if (!string.IsNullOrEmpty(e.Data)) {
                    errorBuilder.AppendLine(e.Data);
                    if (!(e.Data.Contains("frame=") || e.Data.Contains("speed=") || e.Data.Contains("time="))) {
                        Ui.Detail(e.Data, "FFmpeg STDERR");
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            Ui.Detail("Process finished.", "FFmpeg");

            if (process.ExitCode != 0) {
                Ui.Error($"FFmpeg wurde mit Fehlercode {process.ExitCode} beendet.", "FFmpeg Error");
                if (errorBuilder.Length > 0) {
                    Ui.Detail(errorBuilder.ToString(), "FFmpeg STDERR Full Log");
                }
                if (outputBuilder.Length > 0) {
                    Ui.Detail(outputBuilder.ToString(), "FFmpeg STDOUT Full Log");
                }
                return false;
            }
            return true;
        }
        catch (Exception ex) {
            Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}");
            if (ex is System.ComponentModel.Win32Exception win32Ex && win32Ex.NativeErrorCode == 2)
            {
                Ui.Error("'ffmpeg' konnte nicht gefunden werden. Bitte stellen Sie sicher, dass FFmpeg installiert und im System-PATH konfiguriert ist.", "FFmpeg error");
            }
            else {
                Ui.Error("Ein Fehler ist beim Starten von FFmpeg aufgetreten.", "FFmpeg error");
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