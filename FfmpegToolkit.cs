using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace FfmpegUtilities
{
  /// <summary>
  /// Core FFmpeg toolset. Independent of any console/interactive logic.
  /// Can be safely called from background tasks, ChatSession, or APIs.
  /// </summary>
  public class FfmpegToolkit
  {
    public async Task<bool> ProcessSplitVideoAsync(string inputFile, string destFolder, int parts = 3, double overlapSeconds = 180, bool downmixToMono = false)
    {
      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      double duration = await GetVideoDurationAsync(inputFile);

      if (duration <= 0)
      {
        Console.WriteLine($"\n  [FFmpegToolkit] Error: Could not determine video duration for '{fileName}'.");
        return false;
      }

      Console.WriteLine($"\n  [FFmpegToolkit] Splitting into {parts} parts: {Path.GetFileName(inputFile)} (Total Duration: {duration:F2}s)");

      string audioArgs = downmixToMono ? "-c:a aac -b:a 256k -ac 1 -af \"aformat=channel_layouts=mono\"" : "-c:a copy";

      if (duration <= overlapSeconds * 2 || parts <= 1)
      {
        Console.WriteLine("  Warning: Video is too short to meaningfully split (or parts=1). Processing as a single file.");
        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-compressed", ".mp4");
        string ffmpegArgs = $"-i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -crf 18 {audioArgs} -r 1 \"{outputFile}\"";

        return await RunFfmpegAsync(ffmpegArgs);
      }

      bool allSuccess = true;
      double segmentLength = (duration + (parts - 1) * overlapSeconds) / parts;

      for (int i = 0; i < parts; i++)
      {
        double start = i * (segmentLength - overlapSeconds);
        double end = start + segmentLength;
        if (end > duration) end = duration;

        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_part{i + 1}-compressed", ".mp4");
        string ffmpegArgs = $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -crf 18 {audioArgs} -r 1 \"{outputFile}\"";

        Console.WriteLine($"\n  [FFmpegToolkit] Part {i + 1}/{parts}: Start={start:F2}s, End={end:F2}s");
        if (!await RunFfmpegAsync(ffmpegArgs))
        {
          Console.WriteLine($"  [FAILED] Error processing Part {i + 1}.");
          allSuccess = false;
        }
        else
        {
          Console.WriteLine($"  [SUCCESS] Part {i + 1} completed => {outputFile}");
        }
      }
      return allSuccess;
    }

    /// <summary>
    /// Eine generische Methode, die Video-Geschwindigkeit, FPS und Audio-Parameter dynamisch anpasst.
    /// </summary>
    public async Task<bool> ProcessGeneralVideoAsync(string inputFile, string destFolder, double speedMultiplier = 1.0, int fps = 1, bool downmixToMono = true, int? audioSampleRate = null)
    {
      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-compressed", ".mp4");

      // 1. Video Filter zusammenbauen
      string videoFilter = $"fps={fps}";
      if (speedMultiplier != 1.0)
      {
        double ptsMultiplier = 1.0 / speedMultiplier;
        videoFilter = $"setpts={ptsMultiplier.ToString(CultureInfo.InvariantCulture)}*PTS,{videoFilter}";
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
          audioFilter += "aformat=channel_layouts=mono";
        }
        if (speedMultiplier != 1.0)
        {
          if (!string.IsNullOrEmpty(audioFilter)) audioFilter += ",";
          audioFilter += $"atempo={speedMultiplier.ToString(CultureInfo.InvariantCulture)}";
        }
        if (audioSampleRate.HasValue) audioArgs += $" -ar {audioSampleRate.Value}";
        if (!string.IsNullOrEmpty(audioFilter)) audioArgs += $" -af \"{audioFilter}\"";
      }

      string ffmpegArgs = $"-i \"{inputFile}\" -vf \"{videoFilter}\" -c:v libx264 -crf 18 {audioArgs} -r {fps} \"{outputFile}\"";

      Console.WriteLine($"\n  [FFmpegToolkit] Processing AI Video ({speedMultiplier}x Speed, {fps} FPS): {Path.GetFileName(inputFile)}...");

      if (await RunFfmpegAsync(ffmpegArgs))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return true;
      }
      return false;
    }

    public async Task<bool> ProcessFast720pVideoAsync(string inputFile, string destFolder)
    {
      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-compressed", ".mp4");

      // Hardcodierte Parameter für 720p, 1.5x Speed und 1 FPS
      string ffmpegArgs = $"-i \"{inputFile}\" -vf \"setpts=0.666667*PTS,scale=1280:720,fps=1\" -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -c:a aac -b:a 192k -ac 1 -af \"aformat=channel_layouts=mono,atempo=1.5\" -r 1 \"{outputFile}\"";

      Console.WriteLine($"\n  [FFmpegToolkit] Processing (Fast 720p): {Path.GetFileName(inputFile)}...");

      if (await RunFfmpegAsync(ffmpegArgs))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return true;
      }
      return false;
    }

    public async Task<bool> ProcessCustomVideoAsync(string inputFile, string destFolder, string commandTemplate, string outputExtension)
    {
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

    public async Task<bool> ExtractAudioAsMp3Async(string inputFile, string destFolder)
    {
      string fileName = Path.GetFileNameWithoutExtension(inputFile);
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_audio", ".mp3");
      string arguments = $"-y -i \"{inputFile}\" -vn -acodec libmp3lame -q:a 2 \"{outputFile}\"";

      Console.WriteLine($"\n  [FFmpegToolkit] Extracting MP3: {Path.GetFileName(inputFile)}...");

      if (await RunFfmpegAsync(arguments))
      {
        Console.WriteLine($"  [SUCCESS] => TO: {outputFile}");
        return true;
      }
      return false;
    }

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

    private async Task<double> GetVideoDurationAsync(string filePath)
    {
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
        Console.WriteLine($"  [ffprobe error: {ex.Message}]");
      }
      return -1;
    }

    private async Task<bool> RunFfmpegAsync(string arguments)
    {
      var processInfo = new ProcessStartInfo
      {
        FileName = "ffmpeg",
        Arguments = arguments + " -v error",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      };

      try
      {
        using var process = Process.Start(processInfo);
        if (process == null) return false;

        var readErrorTask = process.StandardError.ReadToEndAsync();
        var readOutputTask = process.StandardOutput.ReadToEndAsync(); // Good practice to prevent deadlocks

        await process.WaitForExitAsync();
        string errorOutput = await readErrorTask;

        if (process.ExitCode != 0)
        {
          Console.WriteLine($"  [FFmpeg Error] {errorOutput.Trim()}");
          return false;
        }
        return true;
      }
      catch (Exception ex)
      {
        Console.WriteLine($"  [Error] Failed to start FFmpeg: {ex.Message}");
        return false;
      }
    }
  }
}