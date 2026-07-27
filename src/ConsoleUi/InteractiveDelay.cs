using System;
using System.Threading;
using System.Threading.Tasks;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Implements an interactive delay with user cancellation, and tracks whether one is
/// currently active so other input-intercepting tasks (e.g. in a REPL) can pause around it.
/// </summary>
public static class InteractiveDelay {
    // [AI Context] Globale Flag, um Input-Intercepting-Tasks (z.B. im REPL) während eines Delays zu pausieren
    // Fixed IDE warning: Non-constant fields should not be visible. Converted to a property with a volatile backing field for thread safety.
    private static volatile bool _isInSmartDelay = false;
    public static bool IsInSmartDelay {
        get => _isInSmartDelay;
        set => _isInSmartDelay = value;
    }

    // [AI Context] Tracks the UTC timestamp when the last model completion / file generation finished across any extraction or refinement step.
    public static DateTime LastGenerationCompletionTimeUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Implements an interactive delay with user cancellation. Allows interrupting long backoff periods.
    /// </summary>
    public static async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...") {
        Console.WriteLine($"\n⏳ [SmartDelay] Warte {seconds} Sekunden: {message}");
        Console.WriteLine("   (Tipp: Du kannst jederzeit [Enter] drücken, um die Wartezeit sofort zu überspringen.)");
        bool delayCanceled = false;
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; delayCanceled = true; }
        Console.CancelKeyPress += cancelHandler;
        IsInSmartDelay = true;
        using var cts = new CancellationTokenSource();
        try {
            var delayTask = Task.Run(async () => {
                int delaySteps = seconds * 10;
                for (int i = 0; i < delaySteps; i++) {
                    if (delayCanceled || cts.Token.IsCancellationRequested) return false;
                    await Task.Delay(100, cts.Token);
                    try {
                        if (!Console.IsInputRedirected && Console.KeyAvailable) {
                            bool enterPressed = false;
                            while (Console.KeyAvailable) {
                                var keyInfo = Console.ReadKey(intercept: true);
                                if (keyInfo.Key == ConsoleKey.Enter) enterPressed = true;
                            }
                            if (enterPressed) {
                                Console.WriteLine("\n[Skip] Wartezeit durch Benutzer (Enter) übersprungen.");
                                return true;
                            }
                            Console.WriteLine($"\n[AI-Model] {message} (Oder drücke Enter für sofortigen Retry/Skip)");
                        }
                    }
                    catch { }
                }
                return true;
            }, cts.Token);

            var inputTask = Task.Run(async () => {
                try {
                    while (!cts.Token.IsCancellationRequested) {
                        bool isRedirected = false;
                        try { isRedirected = Console.IsInputRedirected; } catch { }

                        if (!isRedirected) {
                            await Task.Delay(200, cts.Token);
                            continue;
                        }

                        // [AI Context] When running inside redirected consoles (e.g., IDE terminal, pseudo-terminal),
                        // Console.KeyAvailable throws or returns false. We use ReadLineAsync with cancellation.
                        // [Human] In IDE-Terminals (wie VS Code oder Antigravity) ist die Konsole umgeleitet. Damit Enter trotzdem funktioniert, lesen wir hier asynchron die Eingabe.
                        var lineTask = Console.In.ReadLineAsync(cts.Token).AsTask();
                        await lineTask;
                        return true;
                    }
                }
                catch { }
                return false;
            }, cts.Token);

            var completedTask = await Task.WhenAny(delayTask, inputTask);
            cts.Cancel(); // Cancel the other task

            if (completedTask == inputTask && await inputTask) {
                Console.WriteLine("\n[Skip] Wartezeit durch Benutzer (Enter) übersprungen.");
                return true;
            }

            return await delayTask;
        }
        finally {
            IsInSmartDelay = false;
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
