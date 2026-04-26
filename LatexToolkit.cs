using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DocumentUtilities;

/// <summary>
/// Führt lokale LaTeX-Kompilierungen durch.
/// Unabhängig von der KI, ruft direkt pdflatex auf dem System auf.
/// </summary>
public class LatexToolkit
{
  public async Task<(bool success, string outputLog)> CompilePdfAsync(string texFilePath)
  {
    if (!File.Exists(texFilePath))
    {
      return (false, $"File not found: {texFilePath}");
    }

    string workDir = Path.GetDirectoryName(texFilePath) ?? string.Empty;
    string fileName = Path.GetFileName(texFilePath);

    Console.WriteLine($"\n  [LatexToolkit] Starte pdflatex für {fileName}...");

    // -interaction=nonstopmode verhindert, dass pdflatex bei einem Fehler auf Benutzereingaben wartet
    var startInfo = new ProcessStartInfo
    {
      FileName = "pdflatex",
      Arguments = $"-interaction=nonstopmode -halt-on-error \"{fileName}\"",
      WorkingDirectory = workDir,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true
    };

    try
    {
      using var process = Process.Start(startInfo);
      if (process == null) return (false, "Could not start pdflatex process.");

      string output = await process.StandardOutput.ReadToEndAsync();
      string error = await process.StandardError.ReadToEndAsync();

      await process.WaitForExitAsync();

      if (process.ExitCode == 0)
      {
        Console.WriteLine($"  [SUCCESS] PDF erfolgreich generiert!");
        return (true, output);
      }
      else
      {
        Console.WriteLine($"  [FAILED] pdflatex hat Fehler gemeldet.");
        return (false, output + "\n" + error);
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"  [Error] pdflatex konnte nicht ausgeführt werden. Ist LaTeX (z.B. MiKTeX oder TeX Live) installiert? \nFehlermeldung: {ex.Message}");
      return (false, ex.Message);
    }
  }
}