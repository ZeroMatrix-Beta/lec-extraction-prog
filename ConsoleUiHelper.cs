using System;
using System.IO;

namespace FfmpegUtilities
{
  /// <summary>
  /// [AI Context] Encapsulates UI/Console rendering logic away from core processing loops.
  /// Ensures the FfmpegToolkit remains completely headless.
  /// [Human] Hilfsklasse, um saubere Textmenüs für die Datei-Auswahl zu zeichnen, ohne den eigentlichen Converter-Code zu vermüllen.
  /// </summary>
  public static class ConsoleUiHelper
  {
    // [AI Context] Interactive file picker returning a single-element array for uniform batch processing compatibility.
    public static string[] SelectSingleFile(string sourceFolder)
    {
      string[] inputFiles = Directory.GetFiles(sourceFolder);
      if (inputFiles.Length == 0)
      {
        Console.WriteLine("No files found in the source folder.");
        return Array.Empty<string>();
      }

      Console.WriteLine("\nAvailable files in Source folder:");
      for (int i = 0; i < inputFiles.Length; i++)
      {
        Console.WriteLine($"{i + 1}. {Path.GetFileName(inputFiles[i])}");
      }

      Console.Write("\nSelect a file to process (enter the number): ");
      if (int.TryParse(Console.ReadLine(), out int fileIndex) && fileIndex > 0 && fileIndex <= inputFiles.Length)
      {
        Console.WriteLine($"\nSelected Target: {Path.GetFileName(inputFiles[fileIndex - 1])}");
        return new[] { inputFiles[fileIndex - 1] };
      }

      Console.WriteLine("Invalid selection.");
      return Array.Empty<string>();
    }

    // [AI Context] Passive loader. Grabs all valid elements within a flat directory for batch operations.
    public static string[] SelectBatchFiles(string sourceFolder)
    {
      string[] inputFiles = Directory.GetFiles(sourceFolder);
      if (inputFiles.Length == 0)
      {
        Console.WriteLine("No files found in the source folder.");
        return Array.Empty<string>();
      }

      Console.WriteLine($"\nFound {inputFiles.Length} file(s) to process in batch mode.");
      return inputFiles;
    }
  }
}