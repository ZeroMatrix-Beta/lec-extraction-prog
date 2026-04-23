using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// [AI Context] Manages FFmpeg preprocessing tasks for video/audio files before feeding them to the AI.
/// Handles time-stretching (atempo), downscaling (1 fps), and overlapping splits to optimize AI token usage.
/// </summary>
public class AIStudioFfmpegManager
{
  // [AI Context] Configurable default fallback paths.
  private string DefaultSourceFolder;
  private string DefaultDestinationFolder;

  public AIStudioFfmpegManager(string sourceFolder, string destinationFolder)
  {
    DefaultSourceFolder = sourceFolder;
    DefaultDestinationFolder = destinationFolder;
  }

  public Task StartAsync()
  {
    PrintHeader();

    // Phase 1: Setup and Validation
    if (!SetupDirectories(out string sourceFolder, out string destFolder)) return Task.CompletedTask;

    // Phase 2: Preset Selection
    string mode = ShowMenuAndGetMode();
    if (string.IsNullOrEmpty(mode) || !IsValidMode(mode))
    {
      Console.WriteLine("Invalid conversion option selected. Exiting.");
      return Task.CompletedTask;
    }

    // Phase 3: Construct Commands
    string commandTemplate = GetCommandTemplate(mode, out string outputExtension);

    // Phase 4: Target Selection
    string[] filesToProcess = SelectFilesToProcess(sourceFolder, mode);
    if (filesToProcess == null || filesToProcess.Length == 0) return Task.CompletedTask;

    // ====================================================================
    // Human: Behold! The tiny, clean main processing loop!
    // It dynamically routes to either the split processor or the standard one.
    // ====================================================================
    foreach (string inputFile in filesToProcess)
    {
      if (mode == "9" || mode == "10")
      {
        ProcessSplitVideo(inputFile, destFolder);
      }
      else
      {
        ProcessStandardVideo(inputFile, destFolder, commandTemplate, outputExtension);
      }
    }
    // ====================================================================

    PrintFooter();

    return Task.CompletedTask;
  }

  // ========================================================================
  // Sub-Methods (Refactored Logic)
  // ========================================================================

  private void PrintHeader()
  {
    Console.WriteLine("======================================");
    Console.WriteLine("   FFmpeg Console Video Converter");
    Console.WriteLine("======================================");
  }

  private void PrintFooter()
  {
    Console.WriteLine("\n======================================");
    Console.WriteLine("All files processed successfully.");
    Console.WriteLine("Press Enter to exit...");
    Console.ReadLine();
  }

  private bool SetupDirectories(out string sourceFolder, out string destFolder)
  {
    // [Human] Interactive prompt to easily override the default hardcoded paths on the fly.
    sourceFolder = DefaultSourceFolder;
    destFolder = DefaultDestinationFolder;

    Console.WriteLine($"\nDefault Source Folder: {DefaultSourceFolder}");
    Console.WriteLine($"Default Destination Folder: {DefaultDestinationFolder}");
    Console.Write("\nDo you want to use the designated destination and source folders? (Y/N): ");
    string? useDefault = Console.ReadLine()?.Trim().ToUpper();

    if (useDefault != "Y")
    {
      Console.Write("Set custom Source folder: ");
      sourceFolder = Console.ReadLine() ?? sourceFolder;

      Console.Write("Set custom Destination folder: ");
      destFolder = Console.ReadLine() ?? destFolder;
    }

    if (!Directory.Exists(sourceFolder))
    {
      Console.WriteLine($"Error: Source folder '{sourceFolder}' does not exist.");
      return false;
    }

    if (!Directory.Exists(destFolder))
    {
      Console.WriteLine($"Creating destination folder '{destFolder}'...");
      Directory.CreateDirectory(destFolder);
    }

    return true;
  }

  private string ShowMenuAndGetMode()
  {
    Console.WriteLine("\nConversion Options:");
    Console.WriteLine("1.  Single File: Fixed 720p, 1.5x Speed, 1 FPS (Hardcoded Bitrates)");
    Console.WriteLine("2.  Batch Folder: Fixed 720p, 1.5x Speed, 1 FPS (Hardcoded Bitrates)");
    Console.WriteLine("3.  Single File: Universal AI Format (1.5x Speed, 1 FPS, 256k Mono) [Okay Quality]");
    Console.WriteLine("4.  Batch Folder: Universal AI Format (1.5x Speed, 1 FPS, 256k Mono) [Okay Quality]");
    Console.WriteLine("5.  Single File: High-Fidelity AI Format (1.2x Speed, 1 FPS, 256k Mono) [Good Quality]");
    Console.WriteLine("6.  Batch Folder: High-Fidelity AI Format (1.2x Speed, 1 FPS, 256k Mono) [Good Quality]");
    Console.WriteLine("7.  Single File: Gold Standard AI Format (1.0x Speed, 1 FPS, Original Audio) [Best Quality]");
    Console.WriteLine("8.  Batch Folder: Gold Standard AI Format (1.0x Speed, 1 FPS, Original Audio) [Best Quality]");
    Console.WriteLine("9.  Single File: Split into 3 parts (1 FPS, Original Audio, 3-min overlap)");
    Console.WriteLine("10. Batch Folder: Split into 3 parts (1 FPS, Original Audio, 3-min overlap)");
    Console.WriteLine("11. Custom: Provide your own specific FFmpeg parameters (Single File)");
    Console.WriteLine("12. Custom: Provide your own specific FFmpeg parameters (Batch Folder)");
    Console.Write("\nChoose an option (1-12)?: ");

    return Console.ReadLine()?.Trim() ?? "";
  }

  private bool IsValidMode(string mode)
  {
    if (int.TryParse(mode, out int m))
    {
      return m >= 1 && m <= 12;
    }
    return false;
  }

  private string GetCommandTemplate(string mode, out string outputExtension)
  {
    // [AI Context] Maps user selection to specific FFmpeg filtergraphs.
    // - setpts: Adjusts video framerate/speed to maintain sync.
    // - atempo: Changes audio speed WITHOUT changing pitch (vital for AI transcription).
    // - aformat=channel_layouts=mono: Mixes stereo to mono BEFORE atempo to prevent phase artifacts.
    outputExtension = ".mp4";
    string template = "";

    switch (mode)
    {
      case "1":
      case "2":
        template = "-i \"{0}\" -vf \"setpts=0.666667*PTS,scale=1280:720,fps=1\" -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -c:a aac -b:a 192k -ac 1 -af \"atempo=1.5\" -r 1 \"{1}\"";
        break;
      case "3":
      case "4":
        template = "-i \"{0}\" -vf \"setpts=0.666667*PTS,fps=1\" -c:v libx264 -crf 18 -c:a aac -b:a 256k -ac 1 -af \"atempo=1.5\" -r 1 \"{1}\"";
        break;
      case "5":
      case "6":
        template = "-i \"{0}\" -vf \"setpts=0.833333*PTS,fps=1\" -c:v libx264 -crf 18 -c:a aac -b:a 256k -ar 48000 -af \"aformat=channel_layouts=mono,atempo=1.2\" -r 1 \"{1}\"";
        break;
      case "7":
      case "8":
        template = "-i \"{0}\" -vf \"fps=1\" -c:v libx264 -crf 18 -c:a copy -r 1 \"{1}\"";
        break;
      case "11":
      case "12":
        Console.WriteLine("\nEnter custom parameters.");
        Console.WriteLine("Tip: Use {0} as the placeholder for the input file path, and {1} for the output file path.");
        Console.WriteLine("Example: -i \"{0}\" -vcodec libx264 \"{1}\"");
        Console.Write("Custom FFmpeg arguments: ");
        template = Console.ReadLine() ?? "";

        Console.Write("Set destination file extension (e.g., .mp4, .mkv, .avi): ");
        outputExtension = Console.ReadLine() ?? ".mp4";
        if (!outputExtension.StartsWith(".")) outputExtension = "." + outputExtension;
        break;
        // Modes 9 & 10 handle their templates actively during execution, so we skip standard template binding here
    }

    return template;
  }

  private string[] SelectFilesToProcess(string sourceFolder, string mode)
  {
    string[] inputFiles = Directory.GetFiles(sourceFolder);

    if (inputFiles.Length == 0)
    {
      Console.WriteLine("No files found in the source folder.");
      return Array.Empty<string>();
    }

    // Interactive single-file selection
    if (mode == "1" || mode == "3" || mode == "5" || mode == "7" || mode == "9" || mode == "11")
    {
      Console.WriteLine("\nAvailable files in Source folder:");
      for (int i = 0; i < inputFiles.Length; i++)
      {
        Console.WriteLine($"{i + 1}. {Path.GetFileName(inputFiles[i])}");
      }

      Console.Write("\nSelect a file to process (enter the number): ");
      if (int.TryParse(Console.ReadLine(), out int fileIndex) && fileIndex > 0 && fileIndex <= inputFiles.Length)
      {
        Console.WriteLine($"\nSelected Target: {Path.GetFileName(inputFiles[fileIndex - 1])}");
        return new string[] { inputFiles[fileIndex - 1] };
      }
      else
      {
        Console.WriteLine("Invalid selection.");
        return Array.Empty<string>();
      }
    }

    // Batch mode
    if (mode == "2" || mode == "4" || mode == "6" || mode == "8" || mode == "10" || mode == "12")
    {
      Console.WriteLine($"\nFound {inputFiles.Length} file(s) to process in batch mode.");
      return inputFiles;
    }

    return Array.Empty<string>();
  }

  private void ProcessSplitVideo(string inputFile, string destFolder)
  {
    // [AI Context] Intelligent splitting logic for long lectures.
    // Adds a 3-minute overlap (180s) between segments so the AI never loses a sentence at the cut.
    string fileName = Path.GetFileNameWithoutExtension(inputFile);
    double duration = GetVideoDuration(inputFile);

    if (duration <= 0)
    {
      Console.WriteLine($"\nError: Could not determine video duration for '{fileName}' using ffprobe. Skipping file.");
      return;
    }

    Console.WriteLine($"\nProcessing: {Path.GetFileName(inputFile)} (Total Duration: {duration:F2}s)");
    double overlap = 180; // 3 minutes

    if (duration <= overlap * 2)
    {
      Console.WriteLine("  Warning: Video is too short to meaningfully split. Processing as a single file.");
      Console.WriteLine("  Action: Extracting video at 1 FPS and copying original audio (No overlap)...");
      string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-compressed", ".mp4");
      string ffmpegArgs = $"-i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -crf 18 -c:a copy -r 1 \"{outputFile}\"";

      if (RunFFmpeg(ffmpegArgs))
      {
        Console.WriteLine($"  [SUCCESS] Single-file processing complete!");
      }
      else
      {
        Console.WriteLine($"  [FAILED] Error processing file.");
      }
    }
    else
    {
      double segmentLength = (duration + 2 * overlap) / 3.0;

      for (int i = 0; i < 3; i++)
      {
        double start = i * (segmentLength - overlap);
        double end = start + segmentLength;
        if (end > duration) end = duration;

        string outputFile = GetUniqueFilePath(destFolder, $"{fileName}_part{i + 1}-compressed", ".mp4");
        string ffmpegArgs = $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -vf \"fps=1\" -c:v libx264 -crf 18 -c:a copy -r 1 \"{outputFile}\"";

        Console.WriteLine($"\n  Part {i + 1}/3: Start={start:F2}s, End={end:F2}s");
        Console.WriteLine($"  Action: Extracting at 1 FPS and copying original audio...");
        Console.WriteLine($"  Running FFmpeg...");

        if (RunFFmpeg(ffmpegArgs))
        {
          Console.WriteLine($"  [SUCCESS] Part {i + 1} completed!");
        }
        else
        {
          Console.WriteLine($"  [FAILED] Error processing Part {i + 1}.");
        }
      }
    }
  }

  private void ProcessStandardVideo(string inputFile, string destFolder, string commandTemplate, string outputExtension)
  {
    string fileName = Path.GetFileNameWithoutExtension(inputFile);
    string outputFile = GetUniqueFilePath(destFolder, $"{fileName}-compressed", outputExtension);
    string ffmpegArgs = string.Format(commandTemplate, inputFile, outputFile);

    Console.WriteLine($"\nProcessing: {Path.GetFileName(inputFile)}...");
    Console.WriteLine($"Running FFmpeg with arguments: {ffmpegArgs}");

    if (RunFFmpeg(ffmpegArgs))
    {
      Console.WriteLine($"\n  [SUCCESS] Extraction and compression complete!");
      Console.WriteLine($"  => FROM: {inputFile}");
      Console.WriteLine($"  => TO:   {outputFile}");
    }
    else
    {
      Console.WriteLine($"\n  [FAILED] An error occurred while processing {Path.GetFileName(inputFile)}.");
    }
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

  private double GetVideoDuration(string filePath)
  {
    // [AI Context] Uses ffprobe to securely extract the precise duration of the media file in seconds.
    ProcessStartInfo startInfo = new ProcessStartInfo
    {
      FileName = "ffprobe",
      Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
      RedirectStandardOutput = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    try
    {
      using (Process? process = Process.Start(startInfo))
      {
        if (process == null) return -1;

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double duration))
        {
          return duration;
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"  [ffprobe error: {ex.Message}]");
    }
    return -1;
  }

  private bool RunFFmpeg(string arguments)
  {
    // [AI Context] Executes FFmpeg silently in the background.
    // Redirects StandardError to capture actual crashes without spamming the console with standard progress buffers.
    ProcessStartInfo startInfo = new ProcessStartInfo
    {
      FileName = "ffmpeg",
      Arguments = arguments + " -v error", // -v error suppresses all progress output spam
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardError = true,
      RedirectStandardOutput = true
    };

    try
    {
      using (Process? process = Process.Start(startInfo))
      {
        if (process == null) return false;

        string errorOutput = "";
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput += e.Data + "\n"; };
        process.BeginErrorReadLine();
        process.StandardOutput.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
          Console.WriteLine($"  [FFmpeg Error] {errorOutput.Trim()}");
          return false;
        }
        return true;
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"  [Error] Failed to start FFmpeg: {ex.Message}");
      return false;
    }
  }
}