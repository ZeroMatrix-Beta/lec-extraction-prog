using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DocumentUtilities;

/// <summary>
/// [AI Context] This class acts as an autonomous local build agent. It does not interact with GenAI models.
/// It assumes a valid LaTeX distribution (like MiKTeX or TeX Live) is available in the host environment's PATH.
/// Implements process timeouts and installer disabling to prevent infinite GUI/stdin deadlocks.
/// [Human] Diese Klasse kümmert sich komplett autonom um das Bauen der finalen PDF-Datei aus dem generierten LaTeX-Code.
/// Führt lokale LaTeX-Kompilierungen durch.
/// Unabhängig von der KI, ruft direkt pdflatex auf dem System auf.
/// </summary>
public class LatexToolkit {
    /// <summary>
    /// [AI Context] Spawns an external pdflatex process to compile a .tex file to a .pdf. Captures standard output and errors for debugging.
    /// Uses a 90-second timeout and -disable-installer to avoid blocking on package installation dialogs or infinite loops.
    /// </summary>
    public static async Task<(bool success, string outputLog)> CompilePdfAsync(string texFilePath, int timeoutSeconds = 90) {
        if (!File.Exists(texFilePath)) {
            return (false, $"File not found: {texFilePath}");
        }

        string workDir = Path.GetDirectoryName(texFilePath) ?? string.Empty;
        string fileName = Path.GetFileName(texFilePath);

        Console.WriteLine($"\n  [LatexToolkit] Starte PDF-Kompilierung für {fileName}...");

        int maxRuns = 3;
        string finalOutput = "";

        for (int run = 1; run <= maxRuns; run++) {
            Console.WriteLine($"  [LatexToolkit] Durchlauf {run} von {maxRuns}...");

            // [AI Context] -interaction=nonstopmode, -halt-on-error, and -disable-installer are critical to prevent pdflatex from hanging on syntax errors or MiKTeX package installation prompts.
            // [Human] -interaction=nonstopmode und -disable-installer verhindern, dass pdflatex bei Syntaxfehlern oder fehlenden MiKTeX-Paketen hängen bleibt.
            var startInfo = new ProcessStartInfo {
                FileName = "pdflatex",
                Arguments = $"-interaction=nonstopmode -halt-on-error -disable-installer \"{fileName}\"",
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try {
                using var process = Process.Start(startInfo);
                if (process == null) return (false, "Could not start pdflatex process.");

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));

                try {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException) {
                    Console.WriteLine($"  [TIMEOUT] pdflatex hat das Zeitlimit von {timeoutSeconds}s überschritten und wurde beendet.");
                    try {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killEx) {
                        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {killEx.GetType().Name}");
                        Console.WriteLine($"Originaler Fehlertext: {killEx.Message}");
                    }
                    string partialOutput = await outputTask;
                    return (false, $"Compilation timed out after {timeoutSeconds} seconds.\nPartial Output:\n{partialOutput}");
                }

                string output = await outputTask;
                string error = await errorTask;
                finalOutput = output + "\n" + error;

                if (process.ExitCode != 0) {
                    Console.WriteLine($"  [FAILED] pdflatex hat Fehler gemeldet (ExitCode {process.ExitCode}) in Durchlauf {run}.");
                    return (false, finalOutput);
                }

                if (run < maxRuns) {
                    if (!output.Contains("Rerun to get cross-references right") &&
                        !output.Contains("Rerun to get citations correct") &&
                        !output.Contains("Rerun LaTeX") &&
                        !output.Contains("Rerun to get")) 
                    {
                        Console.WriteLine($"  [INFO] Keine weiteren Durchläufe nötig.");
                        break;
                    }
                    Console.WriteLine($"  [INFO] Referenzen benötigen einen weiteren Durchlauf.");
                }
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                Console.WriteLine($"  [Error] pdflatex konnte nicht ausgeführt werden. Ist LaTeX (z.B. MiKTeX oder TeX Live) installiert?");
                return (false, ex.Message);
            }
        }

        Console.WriteLine($"  [SUCCESS] PDF erfolgreich generiert!");
        return (true, finalOutput);
    }
}