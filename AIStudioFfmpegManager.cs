using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

public class AIStudioFfmpegManager
{
  private string DefaultSourceFolder = @"C:\VideoConverter\Input";
  private string DefaultDestinationFolder = @"C:\VideoConverter\Output";

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
    // ====================================================================
    foreach (string inputFile in filesToProcess)
    {
      if (mode == "3" || mode == "4")
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
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey();
  }

  private bool SetupDirectories(out string sourceFolder, out string destFolder)
  {
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
    Console.WriteLine("1. Single File: Fixed 720p, 1.5x Speed, 1 FPS (Hardcoded Bitrates)");
    Console.WriteLine("2. Single File: Universal AI Format (Visually Lossless/Orig Res, 1.5x Speed, 1 FPS, 256k Mono)");
    Console.WriteLine("3. Single File: Universal AI Format + Split into 3 parts (3-minute overlap)");
    Console.WriteLine("4. Batch Folder: Universal AI Format + Split into 3 parts (ALL FILES IN SOURCE)");
    Console.WriteLine("5. Custom: Provide your own specific FFmpeg parameters (Single File)");
    Console.Write("\nChoose an option (1, 2, 3, 4, or 5)?: ");

    return Console.ReadLine()?.Trim() ?? "";
  }

  private bool IsValidMode(string mode)
  {
    return mode == "1" || mode == "2" || mode == "3" || mode == "4" || mode == "5";
  }

  private string GetCommandTemplate(string mode, out string outputExtension)
  {
    outputExtension = ".mp4";
    string template = "";

    switch (mode)
    {
      case "1":
        template = "-i \"{0}\" -vf \"setpts=0.666667*PTS,scale=1280:720,fps=1\" -c:v libx264 -b:v 150k -maxrate 150k -bufsize 300k -c:a aac -b:a 192k -ac 1 -af \"atempo=1.5\" -r 1 \"{1}\"";
        break;
      case "2":
        template = "-i \"{0}\" -vf \"setpts=0.666667*PTS,fps=1\" -c:v libx264 -crf 18 -c:a aac -b:a 256k -ac 1 -af \"atempo=1.5\" -r 1 \"{1}\"";
        break;
      case "5":
        Console.WriteLine("\nEnter custom parameters.");
        Console.WriteLine("Tip: Use {0} as the placeholder for the input file path, and {1} for the output file path.");
        Console.WriteLine("Example: -i \"{0}\" -vcodec libx264 \"{1}\"");
        Console.Write("Custom FFmpeg arguments: ");
        template = Console.ReadLine() ?? "";

        Console.Write("Set destination file extension (e.g., .mp4, .mkv, .avi): ");
        outputExtension = Console.ReadLine() ?? ".mp4";
        if (!outputExtension.StartsWith(".")) outputExtension = "." + outputExtension;
        break;
        // Modes 3 & 4 handle their templates actively during execution, so we skip standard template binding here
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
    if (mode == "1" || mode == "2" || mode == "3" || mode == "5")
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
    if (mode == "4")
    {
      Console.WriteLine($"\nFound {inputFiles.Length} file(s) to process in batch mode.");
      return inputFiles;
    }

    return Array.Empty<string>();
  }

  private void ProcessSplitVideo(string inputFile, string destFolder)
  {
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
      Console.WriteLine("  Warning: Video is too short to meaningfully split into 3 parts with a 3-minute overlap. Processing as a single file.");
      string outputFile = Path.Combine(destFolder, $"{fileName}-compressed.mp4");
      string ffmpegArgs = $"-i \"{inputFile}\" -vf \"setpts=0.666667*PTS,fps=1\" -c:v libx264 -crf 18 -c:a aac -b:a 256k -ac 1 -af \"atempo=1.5\" -r 1 \"{outputFile}\"";
      RunFFmpeg(ffmpegArgs);
    }
    else
    {
      double segmentLength = (duration + 2 * overlap) / 3.0;

      for (int i = 0; i < 3; i++)
      {
        double start = i * (segmentLength - overlap);
        double end = start + segmentLength;
        if (end > duration) end = duration;

        string outputFile = Path.Combine(destFolder, $"{fileName}_part{i + 1}-compressed.mp4");
        string ffmpegArgs = $"-ss {start:F2} -to {end:F2} -i \"{inputFile}\" -vf \"setpts=0.666667*PTS,fps=1\" -c:v libx264 -crf 18 -c:a aac -b:a 256k -ac 1 -af \"atempo=1.5\" -r 1 \"{outputFile}\"";

        Console.WriteLine($"\n  Part {i + 1}/3: Start={start:F2}s, End={end:F2}s");
        Console.WriteLine($"  Running FFmpeg...");
        RunFFmpeg(ffmpegArgs);
      }
    }
  }

  private void ProcessStandardVideo(string inputFile, string destFolder, string commandTemplate, string outputExtension)
  {
    string fileName = Path.GetFileNameWithoutExtension(inputFile);
    string outputFile = Path.Combine(destFolder, $"{fileName}-compressed{outputExtension}");
    string ffmpegArgs = string.Format(commandTemplate, inputFile, outputFile);

    Console.WriteLine($"\nProcessing: {Path.GetFileName(inputFile)}...");
    Console.WriteLine($"Running FFmpeg with arguments: {ffmpegArgs}");

    RunFFmpeg(ffmpegArgs);
  }

  private double GetVideoDuration(string filePath)
  {
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
      using (Process process = Process.Start(startInfo))
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

  private void RunFFmpeg(string arguments)
  {
    ProcessStartInfo startInfo = new ProcessStartInfo
    {
      FileName = "ffmpeg",
      Arguments = arguments,
      UseShellExecute = false,
      CreateNoWindow = false // Set true to hide the ffmpeg console window
    };

    try
    {
      using (Process process = Process.Start(startInfo))
      {
        process?.WaitForExit();
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Error running FFmpeg. Make sure it is installed and added to PATH.\nDetails: {ex.Message}");
    }
  }
}