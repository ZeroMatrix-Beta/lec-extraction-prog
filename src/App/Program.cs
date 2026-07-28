using System;
using System.Threading.Tasks;

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
            Console.WriteLine("\n[System] Execution cancelled by user. Exiting cleanly.");
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[FATAL ERROR] The application encountered an unhandled exception and must close.");
            Console.WriteLine($"Type: {ex.GetType().Name}");
            Console.WriteLine($"Message: {ex.Message}");
            Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");

            // Keep the console open so the user can actually read the fatal error
            Console.WriteLine("\nPress any key to exit...");
            if (!Console.IsInputRedirected) Console.ReadKey(true);
        }
        finally {
            Console.WriteLine("\n[System] Session ended.");
        }
    }
}

