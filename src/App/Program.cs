using System;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Main application entry point. Just Main() and top-level exception handling — the menu
/// loop moved to MainMenu, the session wiring to SessionFactory (Phase 6).
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird.
/// </summary>
public class Program {
    static async Task Main() {
        try {
            await MainMenu.RunAsync();
        }
        catch (OperationCanceledException) {
            Ui.Warn("Execution cancelled by user. Exiting cleanly.", "System");
        }
        catch (Exception ex) {
            Ui.Error($"The application encountered an unhandled exception and must close.\nType: {ex.GetType().Name}\nMessage: {ex.Message}\nStack Trace:\n{ex.StackTrace}", "FATAL ERROR");

            Ui.Info("Press any key to exit...");
            if (!Console.IsInputRedirected) Console.ReadKey(true);
        }
        finally {
            Ui.Info("Session ended.", "System");
        }
    }
}
