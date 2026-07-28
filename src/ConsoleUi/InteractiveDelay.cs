using System;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Implements an interactive delay with user cancellation and Spectre.Console Status spinner,
/// tracking whether one is active so other input-intercepting tasks (e.g. in a REPL) can pause around it.
/// </summary>
public static class InteractiveDelay {
    // [AI Context] Globale Flag, um Input-Intercepting-Tasks (z.B. im REPL) während eines Delays zu pausieren
    private static volatile bool _isInSmartDelay = false;
    public static bool IsInSmartDelay {
        get => _isInSmartDelay;
        set => _isInSmartDelay = value;
    }

    // [AI Context] Tracks the UTC timestamp when the last model completion / file generation finished across any extraction or refinement step.
    public static DateTime LastGenerationCompletionTimeUtc { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Implements an interactive delay with user cancellation using Spectre Status spinner.
    /// </summary>
    public static async Task<bool> SmartDelayAsync(int seconds, string message = "Still waiting for the acknowledgment / processing...") {
        Ui.Detail("(Tipp: Du kannst jederzeit [Enter] drücken, um die Wartezeit sofort zu überspringen.)");
        bool delayCanceled = false;
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; delayCanceled = true; }
        Console.CancelKeyPress += cancelHandler;
        IsInSmartDelay = true;
        using var cts = new CancellationTokenSource();
        try {
            return await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("yellow"))
                .StartAsync($"Warte {seconds}s: {message}", async ctx => {
                    var delayTask = Task.Run(async () => {
                        int delaySteps = seconds * 10;
                        for (int i = 0; i < delaySteps; i++) {
                            if (delayCanceled || cts.Token.IsCancellationRequested) return false;
                            int remaining = seconds - (i / 10);
                            ctx.Status($"⏳ Warte {remaining}s: {message}");
                            await Task.Delay(100, cts.Token);
                            try {
                                if (!Console.IsInputRedirected && Console.KeyAvailable) {
                                    bool enterPressed = false;
                                    while (Console.KeyAvailable) {
                                        var keyInfo = Console.ReadKey(intercept: true);
                                        if (keyInfo.Key == ConsoleKey.Enter) enterPressed = true;
                                    }
                                    if (enterPressed) {
                                        Ui.Info("Wartezeit durch Benutzer (Enter) übersprungen.", "Skip");
                                        return true;
                                    }
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

                                var lineTask = Console.In.ReadLineAsync(cts.Token).AsTask();
                                await lineTask;
                                return true;
                            }
                        }
                        catch { }
                        return false;
                    }, cts.Token);

                    var completedTask = await Task.WhenAny(delayTask, inputTask);
                    cts.Cancel();

                    if (completedTask == inputTask && await inputTask) {
                        Ui.Info("Wartezeit durch Benutzer (Enter) übersprungen.", "Skip");
                        return true;
                    }

                    return await delayTask;
                });
        }
        finally {
            IsInSmartDelay = false;
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
