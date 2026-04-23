using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace FfmpegUtilities
{
  /// <summary>
  /// [AI Context] Manages FFmpeg preprocessing tasks for video/audio files before feeding them to the AI.
  /// Interactive console menu that acts as a frontend for the FfmpegToolkit.
  /// </summary>
  public class FfmpegInteractiveMenu
  {
    // [AI Context] Configurable default fallback paths.
    private string DefaultSourceFolder;
    private string DefaultDestinationFolder;
    private readonly FfmpegToolkit _toolkit;

    public FfmpegInteractiveMenu(string sourceFolder, string destinationFolder)
    {
      DefaultSourceFolder = sourceFolder;
      DefaultDestinationFolder = destinationFolder;
      _toolkit = new FfmpegToolkit();
    }

    public async Task StartAsync()
    {
      PrintHeader();

      // Phase 1: Setup and Validation
      if (!SetupDirectories(out string sourceFolder, out string destFolder)) return;

      // Phase 2: Preset Selection
      string mode = ShowMenuAndGetMode();
      if (string.IsNullOrEmpty(mode) || !IsValidMode(mode))
      {
        Console.WriteLine("Invalid conversion option selected. Exiting.");
        return;
      }

      // Phase 3: Construct Commands
      string customCommandTemplate = "";
      string customOutputExtension = ".mp4";
      if (mode == "6" || mode == "12")
      {
        customCommandTemplate = GetCustomCommandTemplate(out customOutputExtension);
      }

      // Phase 4: Target Selection
      string[] filesToProcess = SelectFilesToProcess(sourceFolder, mode);
      if (filesToProcess == null || filesToProcess.Length == 0) return;

      // ====================================================================
      // Human: The main processing loop!
      // Cleanly routes to dedicated semantic methods in the toolkit.
      // ====================================================================
      foreach (string inputFile in filesToProcess)
      {
        switch (mode)
        {
          case "1":
          case "7":
            await _toolkit.ProcessFast720pVideoAsync(inputFile, destFolder); break;
          case "2":
          case "8":
            await _toolkit.ProcessGeneralVideoAsync(inputFile, destFolder, speedMultiplier: 1.5, fps: 1, downmixToMono: true); break;
          case "3":
          case "9":
            await _toolkit.ProcessGeneralVideoAsync(inputFile, destFolder, speedMultiplier: 1.2, fps: 1, downmixToMono: true, audioSampleRate: 48000); break;
          case "4":
          case "10":
            await _toolkit.ProcessGeneralVideoAsync(inputFile, destFolder, speedMultiplier: 1.0, fps: 1, downmixToMono: true); break;
          case "5":
          case "11":
            await _toolkit.ProcessSplitVideoAsync(inputFile, destFolder, downmixToMono: true); break;
          case "6":
          case "12":
            await _toolkit.ProcessCustomVideoAsync(inputFile, destFolder, customCommandTemplate, customOutputExtension); break;
        }
      }
      // ====================================================================

      PrintFooter();
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
      Console.WriteLine("\n--- Single File Options ---");
      Console.WriteLine("1. Fixed 720p, 1.5x Speed, 1 FPS (Hardcoded Bitrates)");
      Console.WriteLine("2. Universal AI Format (1.5x Speed, 1 FPS, 256k Mono) [Okay Quality]");
      Console.WriteLine("3. High-Fidelity AI Format (1.2x Speed, 1 FPS, 256k Mono) [Good Quality]");
      Console.WriteLine("4. Gold Standard AI Format (1.0x Speed, 1 FPS, 256k Mono) [Best Quality]");
      Console.WriteLine("5. Split into 3 parts (1 FPS, 256k Mono, 3-min overlap)");
      Console.WriteLine("6. Custom: Provide your own specific FFmpeg parameters");

      Console.WriteLine("\n--- Batch Folder Options ---");
      Console.WriteLine("7.  Fixed 720p, 1.5x Speed, 1 FPS (Hardcoded Bitrates)");
      Console.WriteLine("8.  Universal AI Format (1.5x Speed, 1 FPS, 256k Mono) [Okay Quality]");
      Console.WriteLine("9.  High-Fidelity AI Format (1.2x Speed, 1 FPS, 256k Mono) [Good Quality]");
      Console.WriteLine("10. Gold Standard AI Format (1.0x Speed, 1 FPS, 256k Mono) [Best Quality]");
      Console.WriteLine("11. Split into 3 parts (1 FPS, 256k Mono, 3-min overlap)");
      Console.WriteLine("12. Custom: Provide your own specific FFmpeg parameters");
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

    private string GetCustomCommandTemplate(out string outputExtension)
    {
      Console.WriteLine("\nEnter custom parameters.");
      Console.WriteLine("Tip: Use {0} as the placeholder for the input file path, and {1} for the output file path.");
      Console.WriteLine("Example: -i \"{0}\" -vcodec libx264 \"{1}\"");
      Console.Write("Custom FFmpeg arguments: ");
      string template = Console.ReadLine() ?? "";

      Console.Write("Set destination file extension (e.g., .mp4, .mkv, .avi): ");
      outputExtension = Console.ReadLine() ?? ".mp4";
      if (!outputExtension.StartsWith(".")) outputExtension = "." + outputExtension;

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

      // We safely parse since IsValidMode already verified it's an integer between 1-12.
      int modeNum = int.Parse(mode);

      // Interactive single-file selection (Options 1-6)
      if (modeNum >= 1 && modeNum <= 6)
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

      // Batch mode (Options 7-12)
      if (modeNum >= 7 && modeNum <= 12)
      {
        Console.WriteLine($"\nFound {inputFiles.Length} file(s) to process in batch mode.");
        return inputFiles;
      }

      return Array.Empty<string>();
    }
  }
}