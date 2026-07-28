using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Latex;

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

        Ui.Blank();
        Ui.Detail($"Starte PDF-Kompilierung für {fileName}...", "LatexToolkit");

        int maxRuns = 3;
        string finalOutput = "";

        for (int run = 1; run <= maxRuns; run++) {
            Ui.Detail($"Durchlauf {run} von {maxRuns}...", "LatexToolkit");

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
                    Ui.Error($"pdflatex hat das Zeitlimit von {timeoutSeconds}s überschritten und wurde beendet.", "LatexToolkit");
                    try {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception killEx) {
                        Ui.Warn($"Prozess konnte nicht beendet werden. Art der Exception: {killEx.GetType().Name}, Fehler: {killEx.Message}", "LatexToolkit");
                    }
                    string partialOutput = await outputTask;
                    return (false, $"Compilation timed out after {timeoutSeconds} seconds.\nPartial Output:\n{partialOutput}");
                }

                string output = await outputTask;
                string error = await errorTask;
                finalOutput = output + "\n" + error;

                if (process.ExitCode != 0) {
                    Ui.Error($"pdflatex hat Fehler gemeldet (ExitCode {process.ExitCode}) in Durchlauf {run}.", "LatexToolkit");
                    return (false, finalOutput);
                }

                if (run < maxRuns) {
                    if (!output.Contains("Rerun to get cross-references right") &&
                        !output.Contains("Rerun to get citations correct") &&
                        !output.Contains("Rerun LaTeX") &&
                        !output.Contains("Rerun to get")) 
                    {
                        Ui.Detail("Keine weiteren Durchläufe nötig.", "LatexToolkit");
                        break;
                    }
                    Ui.Detail("Referenzen benötigen einen weiteren Durchlauf.", "LatexToolkit");
                }
            }
            catch (Exception ex) {
                Ui.Error($"pdflatex konnte nicht ausgeführt werden. Ist LaTeX (z.B. MiKTeX oder TeX Live) installiert? Art der Exception: {ex.GetType().Name}, Fehler: {ex.Message}", "LatexToolkit");
                return (false, ex.Message);
            }
        }

        Ui.Success("PDF erfolgreich generiert!", "LatexToolkit");
        return (true, finalOutput);
    }
}