using System;
using System.Threading.Tasks;
using LectureExtraction.Cli;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Main application entry point. Just Main() and top-level exception handling — the menu
/// loop moved to MainMenu, the session wiring to SessionFactory (Phase 6).
///
/// <para>Arguments decide which half of the program runs: none at all means the interactive menu,
/// exactly as before, so the human path is unchanged; anything else is handed to the CLI. Keeping
/// that fork here - rather than inside MainMenu - is what lets the two share every session type
/// without either knowing about the other.</para>
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird. Ohne Argumente
/// startet das gewohnte Menü, mit Argumenten der Kommandozeilen-Modus.
/// </summary>
public class Program {
    static async Task<int> Main(string[] args) {
        bool isInteractive = args.Length == 0;

        try {
            if (isInteractive) {
                await MainMenu.RunAsync();
                return ExitCodes.Success;
            }

            return await CliBootstrapper.RunAsync(args);
        }
        catch (OperationCanceledException) {
            Ui.Warn("Execution cancelled by user. Exiting cleanly.", "System");
            return isInteractive ? ExitCodes.Success : ExitCodes.Unexpected;
        }
        catch (Exception ex) {
            Ui.Error($"The application encountered an unhandled exception and must close.\nType: {ex.GetType().Name}\nMessage: {ex.Message}\nStack Trace:\n{ex.StackTrace}", "FATAL ERROR");

            // Waiting for a keypress is right for a human whose window would otherwise vanish, and
            // wrong for a caller that is reading the exit code.
            if (isInteractive) {
                Ui.Info("Press any key to exit...");
                if (!Console.IsInputRedirected) Console.ReadKey(true);
            }

            return ExitCodes.Unexpected;
        }
        finally {
            if (isInteractive) {
                Ui.Info("Session ended.", "System");
            }
        }
    }
}
