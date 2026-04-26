using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FfmpegUtilities
{
  /// <summary>
  /// Core FFmpeg toolset. Independent of any console/interactive logic.
  /// Can be safely called from background tasks, DirectAIInteraction, or APIs.
  /// [Human] Hier passiert die wahre Magie! Diese Klasse baut die exakten FFmpeg-Befehle zusammen und führt sie aus.
  /// </summary>
  public class FfmpegToolkit
  {
    /// <summary>
    /// [AI Context] Splits long lecture videos into smaller segments with overlapping audio/video.
    /// This ensures the AI model doesn't miss any spoken sentences or context right at the cut points.
    /// [Human] Schneidet große Videos in Stücke, lässt aber die Enden "überlappen", damit die KI beim Wechsel keinen Satz verpasst.
    /// </summary>
    public async Task<List<string>> ProcessSplitVideoAsync(string inputFile, string destFolder, int parts = 3, double overlapSeconds = 180, bool downmixToMono = false, bool streamCopy = false)
    {
      var generatedFiles = new List<string>();

      if (!File.Exists(inputFile))
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
        return generatedFiles;
      }

      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      double duration = await GetVideoDurationAsync(inputFile);

      if (duration <= 0)
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Could not determine video duration for '{fileName}'.");
        return generatedFiles;
      }

      Console.WriteLine($"\n  [FFmpegToolkit] Splitting into {parts} parts: {Path.GetFileName(inputFile)} (Total Duration: {duration:F2}s)");

      // [AI Context] Mono audio effectively halves the bandwidth and token size for speech-to-text models
      // without losing any transcription accuracy. The 'aformat' filter enforces correct metadata.
      // [Human] KI-Spracherkennung braucht kein Stereo. Mono spart uns gigantische Mengen an Tokens, Geld und Upload-Zeit.
      string audioArgs = downmixToMono ? "-c:a aac -b:a 256k -ac 1 -ar 48000 -af \"aformat=channel_layouts=mono\"" : "-c:a copy";

      if (duration <= overlapSeconds * 2 || parts <= 1)
      {
        Console.WriteLine("  Warning: Video is too short to meaningfully split (or parts=1). Processing as a single file.");
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-compressed", ".mp4");
        string ffmpegArgs = streamCopy ? $"-i \"{inputFile}\" -c copy \"{outputFile}\"" : $"-i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -preset fast -crf 18 -g 1 {audioArgs} -r 1 \"{outputFile}\"";

        if (await RunFfmpegAsync(ffmpegArgs)) generatedFiles.Add(outputFile);
        return generatedFiles;
      }

      double segmentLength = (duration + (parts - 1) * overlapSeconds) / parts;

      for (int i = 0; i < parts; i++)
      {
        double start = i * (segmentLength - overlapSeconds);
        double end = start + segmentLength;
        if (end > duration) end = duration;

        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_part{i + 1}-compressed", ".mp4");
        string ffmpegArgs = streamCopy ? $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -c copy \"{outputFile}\"" : $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -preset fast -crf 18 -g 1 {audioArgs} -r 1 \"{outputFile}\"";

        Console.WriteLine($"\n  [FFmpegToolkit] Part {i + 1}/{parts}: Start={start:F2}s, End={end:F2}s");
        if (!await RunFfmpegAsync(ffmpegArgs))
        {
          Console.WriteLine($"  [FAILED] Error processing Part {i + 1}.");
        }
        else
        {
          Console.WriteLine($"  [SUCCESS] Part {i + 1} completed => {outputFile}");
          generatedFiles.Add(outputFile);
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
    public async Task<string?> ProcessGeneralVideoAsync(string inputFile, string destFolder, double speedMultiplier = 1.0, int fps = 1, bool downmixToMono = true, int? audioSampleRate = 48000, bool scaleTo720p = false)
    {
      if (!File.Exists(inputFile))
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
        return null;
      }

      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string speedStr = speedMultiplier.ToString(CultureInfo.InvariantCulture);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-speed-{speedStr}-compressed", ".mp4");

      // 1. Video Filter zusammenbauen
      // [AI Context] fps=1 is optimal for lectures; AI doesn't need 30fps to read a blackboard.
      // setpts adjusts the video timestamps so it stays in perfect sync with the sped-up audio.
      string videoFilter = $"fps={fps}";
      if (speedMultiplier != 1.0)
      {
        double ptsMultiplier = 1.0 / speedMultiplier;
        videoFilter = $"setpts={ptsMultiplier.ToString(CultureInfo.InvariantCulture)}*PTS,{videoFilter}";
      }
      if (scaleTo720p)
      {
        videoFilter += ",scale=-2:720";
      }

      // 2. Audio Parameter zusammenbauen
      string audioArgs = "-c:a copy";
      string audioFilter = "";

      // Wenn wir Speed ändern, in Mono konvertieren oder die Samplerate ändern wollen, müssen wir recoden (aac)
      if (downmixToMono || speedMultiplier != 1.0 || audioSampleRate.HasValue)
      {
        audioArgs = "-c:a aac -b:a 256k";

        if (downmixToMono)
        {
          audioArgs += " -ac 1";
          // Forces the container metadata to correctly report 'Mono' to prevent players like VLC 
          // or AI APIs from misinterpreting it as stereo.
          audioFilter += "aformat=channel_layouts=mono";
        }
        if (speedMultiplier != 1.0)
        {
          if (!string.IsNullOrEmpty(audioFilter)) audioFilter += ",";
          // [AI Context] atempo speeds up the audio WITHOUT changing the pitch (chipmunk effect), 
          // which is absolutely crucial for the AI's speech recognition to keep working reliably.
          audioFilter += $"atempo={speedMultiplier.ToString(CultureInfo.InvariantCulture)}";
        }
        if (audioSampleRate.HasValue) audioArgs += $" -ar {audioSampleRate.Value}";
        if (!string.IsNullOrEmpty(audioFilter)) audioArgs += $" -af \"{audioFilter}\"";
      }

      // [AI Context] -g {fps} sets the Group of Pictures (GOP) size to exactly 1 second.
      // This forces a keyframe every second, which is absolutely critical to make `streamCopy: true` 
      // perfectly frame-accurate during the splitting phase.
      string ffmpegArgs = $"-i \"{inputFile}\" -vf \"{videoFilter}\" -c:v libx264 -preset fast -crf 18 -g {fps} {audioArgs} -r {fps} \"{outputFile}\"";

      Console.WriteLine($"\n  [FFmpegToolkit] Processing AI Video ({speedMultiplier}x Speed, {fps} FPS): {Path.GetFileName(inputFile)}...");

      // [DEBUG] Ausgabe des exakten Befehls, falls FFmpeg manuell getestet werden muss
      Console.WriteLine($"  [DEBUG CMD] ffmpeg -y -nostdin {ffmpegArgs}");

      if (await RunFfmpegAsync(ffmpegArgs))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return outputFile;
      }
      return null;
    }

    /// <summary>
    /// Legacy/Hardcoded fast 720p profile for standard batch processing with strict bitrates.
    /// [Human] Alter, fester Code von früher. Eher für den menschlichen Gebrauch als für die KI gedacht.
    /// </summary>
    public async Task<bool> LegacyCodeProcessFast720pVideoAsync(string inputFile, string destFolder)
    {
      if (!File.Exists(inputFile))
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
        return false;
      }

      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-speed-1.5-720p-compressed", ".mp4");

      // Hardcodierte Parameter für 720p, 1.5x Speed und 1 FPS
      string ffmpegArgs = $"-i \"{inputFile}\" -vf \"setpts=0.666667*PTS,scale=1280:720,fps=1\" -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -g 1 -c:a aac -b:a 192k -ac 1 -ar 48000 -af \"aformat=channel_layouts=mono,atempo=1.5\" -r 1 \"{outputFile}\"";

      Console.WriteLine($"\n  [FFmpegToolkit] Processing (Fast 720p): {Path.GetFileName(inputFile)}...");

      if (await RunFfmpegAsync(ffmpegArgs))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return true;
      }
      return false;
    }

    /// <summary>
    /// Executes custom, raw FFmpeg commands supplied directly by the user.
    /// [Human] Führt komplett frei von dir eingetippte FFmpeg-Parameter aus.
    /// </summary>
    public async Task<bool> ProcessCustomVideoAsync(string inputFile, string destFolder, string commandTemplate, string outputExtension)
    {
      if (!File.Exists(inputFile))
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
        return false;
      }

      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-custom", outputExtension);
      string ffmpegArgs = string.Format(commandTemplate, inputFile, outputFile);

      Console.WriteLine($"\n  [FFmpegToolkit] Processing (Custom): {Path.GetFileName(inputFile)}...");

      if (await RunFfmpegAsync(ffmpegArgs))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return true;
      }
      return false;
    }

    /// <summary>
    /// Extracts only the audio track as an MP3, useful for purely audio-based AI models (e.g., standard Whisper).
    /// </summary>
    public async Task<bool> ExtractAudioAsMp3Async(string inputFile, string destFolder)
    {
      if (!File.Exists(inputFile))
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Input file not found: '{inputFile}'");
        return false;
      }

      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_audio", ".mp3");
      string arguments = $"-y -i \"{inputFile}\" -vn -acodec libmp3lame -q:a 2 -ar 48000 \"{outputFile}\"";

      Console.WriteLine($"\n  [FFmpegToolkit] Extracting MP3: {Path.GetFileName(inputFile)}...");

      if (await RunFfmpegAsync(arguments))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return true;
      }
      return false;
    }

    /// <summary>
    /// Generates a unique file path by appending '-copy-X' if a file with the same name already exists.
    /// Protects user data from being accidentally overwritten.
    /// </summary>
    private string GetUniqueFilePath(string destFolder, string baseName, string extension)
    {
      string fullPath = Path.Combine(destFolder, $"{baseName}{extension}");
      int copyIndex = 1;

      while (File.Exists(fullPath))
      {
        fullPath = Path.Combine(destFolder, $"{baseName}-copy-{copyIndex}{extension}");
        copyIndex++;
      }

      return fullPath;
    }

    /// <summary>
    /// Uses ffprobe to securely extract the precise duration of the media file in seconds.
    /// </summary>
    private async Task<double> GetVideoDurationAsync(string filePath)
    {
      if (!File.Exists(filePath))
      {
        Console.WriteLine($"\n  [ffprobe error] File not found: '{filePath}'");
        return -1;
      }

      var startInfo = new ProcessStartInfo
      {
        FileName = "ffprobe",
        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      try
      {
        using var process = Process.Start(startInfo);
        if (process == null) return -1;

        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double duration))
        {
          return duration;
        }
      }
      catch (Exception ex)
      {
        // [AI Context] RULE: Always include the original exception message (ex.Message or ex.ToString()) in error outputs to aid debugging.
        Console.WriteLine($"  [ffprobe error: {ex.Message}]");
      }
      return -1;
    }

    /// <summary>
    /// Wraps the execution of the FFmpeg process.
    /// Silences normal output but captures and reports the StandardError stream if a crash occurs.
    /// </summary>
    private async Task<bool> RunFfmpegAsync(string arguments)
    {
      var processInfo = new ProcessStartInfo
      {
        FileName = "ffmpeg",
        Arguments = $"-y -nostdin {arguments} -hide_banner -stats",
        RedirectStandardOutput = true,
        RedirectStandardError = true, // Wir leiten um und leeren den Puffer aktiv
        UseShellExecute = false,
        CreateNoWindow = true
      };

      try
      {
        using var process = Process.Start(processInfo);
        if (process == null) return false;

        // Aktives, paralleles Auslesen der Ausgabeströme. 
        // Verhindert das Einfrieren von FFmpeg durch volle I/O-Puffer des Betriebssystems.
        var readErrorTask = Task.Run(async () =>
        {
          var buffer = new char[256];
          int bytesRead;
          while ((bytesRead = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
          {
            Console.Write(new string(buffer, 0, bytesRead));
          }
        });

        var readOutputTask = Task.Run(async () =>
        {
          var buffer = new char[256];
          int bytesRead;
          while ((bytesRead = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length)) > 0)
          {
            Console.Write(new string(buffer, 0, bytesRead));
          }
        });

        // Wir warten, bis der Prozess beendet ist UND unsere Reader-Schleifen alles ausgelesen haben.
        await Task.WhenAll(process.WaitForExitAsync(), readErrorTask, readOutputTask);

        if (process.ExitCode != 0)
        {
          Console.WriteLine($"\n  [FFmpeg Error] FFmpeg wurde mit Fehlercode {process.ExitCode} beendet.");
          return false;
        }
        return true;
      }
      catch (Exception ex)
      {
        // [AI Context] RULE: Always include the original exception message (ex.Message or ex.ToString()) in error outputs to aid debugging.
        Console.WriteLine($"  [Error] Failed to start FFmpeg: {ex.Message}");
        return false;
      }
    }
  }
}