using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace FfmpegUtilities
{
  public class FfmpegSessionConfig
  {
    public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a/";
    public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a/new";
  }

  /// <summary>
  /// [AI Context] Manages FFmpeg preprocessing tasks for video/audio files before feeding them to the AI.
  /// Interactive console menu that acts as a frontend for the FfmpegToolkit.
  /// [Human] Dies ist die Menü-Oberfläche, wenn du im Hauptmenü "13" drückst. Sie regelt nur die Benutzerinteraktion.
  /// </summary>
  public class FfmpegInteractiveSession
  {
    // [AI Context] Configurable default fallback paths.
    private string DefaultSourceFolder;
    private string DefaultDestinationFolder;
    private readonly FfmpegToolkit _toolkit;

    public FfmpegInteractiveSession(string sourceFolder, string destinationFolder)
    {
      DefaultSourceFolder = sourceFolder;
      DefaultDestinationFolder = destinationFolder;
      _toolkit = new FfmpegToolkit();
    }

    public async Task StartAsync()
    {
      Console.WriteLine("======================================");
      Console.WriteLine("   FFmpeg Console Video Converter");
      Console.WriteLine("======================================");

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
      // [AI Context] Validates IO state before execution to prevent partial failure.
      string[] filesToProcess = SelectFilesToProcess(sourceFolder, mode);
      if (filesToProcess == null || filesToProcess.Length == 0) return;

      // ====================================================================
      // Human: The main processing loop!
      // Cleanly routes to dedicated semantic methods in the toolkit.
      // ====================================================================
      foreach (string inputFile in filesToProcess)
      {
        await ExecuteToolkitActionAsync(mode, inputFile, destFolder, customCommandTemplate, customOutputExtension);
      }
      // ====================================================================

      Console.WriteLine("\n======================================");
      Console.WriteLine("All files processed successfully.");
      Console.WriteLine("Press Enter to exit...");
      Console.ReadLine();
    }

    // ========================================================================
    // Sub-Methods (Refactored Logic)
    // ========================================================================

    private bool SetupDirectories(out string sourceFolder, out string destFolder)
    {
      // [Human] Interactive prompt to easily override the default hardcoded paths on the fly.
      // [AI Context] Enables runtime overriding of configured static defaults without recompilation.
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

    /// <summary>
    /// [AI Context] UI Menu rendering for FFmpeg presets.
    /// RULE: If a new preset is added (e.g., option 13), you MUST update four synchronized locations: 
    /// ShowMenuAndGetMode() (UI Text), IsValidMode() (Validation), SelectFilesToProcess() (Routing logic), and ExecuteToolkitActionAsync() (Execution mapping).
    /// </summary>
    private string ShowMenuAndGetMode()
    {
      Console.WriteLine("\nConversion Options:");
      Console.WriteLine("\n--- Single File Options ---");
      Console.WriteLine("1. Fixed 720p, 1.5x Speed, 1 FPS (Legacy Code Hardcoded Bitrates)");
      Console.WriteLine("2. Universal AI Format (1.3x Speed, 1 FPS, 256k Mono) [Okay Quality]");
      Console.WriteLine("3. High-Fidelity AI Format (1.2x Speed, 1 FPS, 256k Mono) [Good Quality]");
      Console.WriteLine("4. Gold Standard AI Format (1.0x Speed, 1 FPS, 256k Mono) [Best Quality]");
      Console.WriteLine("5. Split into 3 parts (1 FPS, 256k Mono, 3-min overlap)");
      Console.WriteLine("6. Custom: Provide your own specific FFmpeg parameters");

      Console.WriteLine("\n--- Batch Folder Options ---");
      Console.WriteLine("7.  Fixed 720p, 1.5x Speed, 1 FPS (Legacy Code Hardcoded Bitrates)");
      Console.WriteLine("8.  Universal AI Format (1.3x Speed, 1 FPS, 256k Mono) [Okay Quality]");
      Console.WriteLine("9.  High-Fidelity AI Format (1.2x Speed, 1 FPS, 256k Mono) [Good Quality]");
      Console.WriteLine("10. Gold Standard AI Format (1.0x Speed, 1 FPS, 256k Mono) [Best Quality]");
      Console.WriteLine("11. Split into 3 parts (1 FPS, 256k Mono, 3-min overlap)");
      Console.WriteLine("12. Custom: Provide your own specific FFmpeg parameters");
      Console.Write("\nChoose an option (1-12)?: ");

      return Console.ReadLine()?.Trim() ?? "";
    }

    private bool IsValidMode(string mode) => int.TryParse(mode, out int m) && m >= 1 && m <= 12;

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
      // We safely parse since IsValidMode already verified it's an integer between 1-12.
      int modeNum = int.Parse(mode);

      // Interactive single-file selection (Options 1-6)
      if (modeNum >= 1 && modeNum <= 6)
      {
        return ConsoleUiHelper.SelectSingleFile(sourceFolder);
      }

      // Batch mode (Options 7-12)
      return ConsoleUiHelper.SelectBatchFiles(sourceFolder);
    }

    private async Task ExecuteToolkitActionAsync(string mode, string inputFile, string destFolder, string customTemplate, string customExt)
    {
      switch (mode)
      {
        case "1":
        case "7":
          await _toolkit.LegacyCodeProcessFast720pVideoAsync(inputFile, destFolder); break;
        case "2":
        case "8":
          await _toolkit.ProcessGeneralVideoAsync(inputFile, destFolder, speedMultiplier: 1.3, fps: 1, downmixToMono: true); break;
        case "3":
        case "9":
          await _toolkit.ProcessGeneralVideoAsync(inputFile, destFolder, speedMultiplier: 1.2, fps: 1, downmixToMono: true); break;
        case "4":
        case "10":
          await _toolkit.ProcessGeneralVideoAsync(inputFile, destFolder, speedMultiplier: 1.0, fps: 1, downmixToMono: true); break;
        case "5":
        case "11":
          await _toolkit.ProcessSplitVideoAsync(inputFile, destFolder, downmixToMono: true); break;
        case "6":
        case "12":
          await _toolkit.ProcessCustomVideoAsync(inputFile, destFolder, customTemplate, customExt); break;
      }
    }
  }
}