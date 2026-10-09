using System;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.Infrastructure;
using Spectre.Console;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Implements an interactive delay with user cancellation and Spectre.Console Status spinner,
/// tracking whether one is active so other input-intercepting tasks (e.g. in a REPL) can pause around it.
/// </summary>
public static class InteractiveDelay {
    // [AI Context] Gate to ensure Spectre dynamic displays (AnsiConsole.Status) are never executed concurrently across threads.
    private static readonly SemaphoreSlim _gate = new(1, 1);

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
    public static async Task<bool> SmartDelayAsync(int seconds, string message = "Warte auf Server-Antwort / Verarbeitung...") {
        if (seconds <= 0) return true;

        await _gate.WaitAsync();
        try {
            bool isUnattended = !Ui.PromptSource.IsInteractive;
            if (!isUnattended) {
                Ui.Detail("(Tipp: Du kannst jederzeit [Enter] drücken, um die Wartezeit sofort zu überspringen.)");
            }
            // Ctrl+C only raises the flag; the delay loop sees it within 100 ms and returns false.
            // Cancelling a token here instead would make Task.Delay throw out of SmartDelayAsync.
            bool delayCanceled = false;
            void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; delayCanceled = true; }
            Console.CancelKeyPress += cancelHandler;
            IsInSmartDelay = true;

            // [AI Context] Every rate-limit wait in the app funnels through here, so measuring the real
            // elapsed time at this one point captures all 18 call sites - including the retry policy's
            // backoff, which is the wait users are least aware of paying (finding F10).
            // [Human] Misst die tatsächliche Wartezeit zentral für alle Aufrufer.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            bool skippedByUser = false;

            try {
                bool completed;
                if (Ui.CanRedrawInPlace) {
                    int lastRemaining = -1;
                    completed = await AnsiConsole.Status()
                        .Spinner(Spinner.Known.Dots)
                        .SpinnerStyle(Style.Parse("yellow"))
                        .StartAsync($"Warte {seconds}s: {message}", ctx => WaitOutAsync(seconds, listenForEnter: !isUnattended, () => delayCanceled, remaining => {
                            if (remaining != lastRemaining) {
                                lastRemaining = remaining;
                                ctx.Status($"Warte {remaining}s: {message}");
                            }
                        }));
                }
                else {
                    // Where Spectre cannot redraw - a log file, a pipe, any redirected stream - every
                    // countdown update would become a line of its own, 64 for a 64 s wait. One line
                    // up front says the same.
                    Ui.Info($"Warte {seconds}s: {message}", "Delay");
                    completed = await WaitOutAsync(seconds, listenForEnter: !isUnattended, () => delayCanceled, _ => { });
                }

                // The return value cannot distinguish "waited the full time" from "user pressed Enter" -
                // both are true - so the elapsed time is what tells them apart.
                skippedByUser = completed && stopwatch.Elapsed.TotalSeconds < seconds * 0.9;
                return completed;
            }
            finally {
                IsInSmartDelay = false;
                Console.CancelKeyPress -= cancelHandler;
                SessionCostLedger.RecordWait(stopwatch.Elapsed, skippedByUser);
            }
        }
        finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits <paramref name="seconds"/>, handing each whole second still to go to
    /// <paramref name="showRemaining"/>. Returns false on Ctrl+C, true when the time ran out or -
    /// with <paramref name="listenForEnter"/> - Enter skipped the rest. The display is the caller's
    /// business, so skipping works the same whichever one it chose.
    /// </summary>
    private static async Task<bool> WaitOutAsync(int seconds, bool listenForEnter, Func<bool> ctrlCPressed, Action<int> showRemaining) {
        using var cts = new CancellationTokenSource();
        // Taken once: the losing task can still be running when cts is disposed on return.
        var token = cts.Token;

        var delayTask = Task.Run(async () => {
            int delaySteps = seconds * 10;
            for (int i = 0; i < delaySteps; i++) {
                if (ctrlCPressed() || token.IsCancellationRequested) return false;
                showRemaining(seconds - (i / 10));
                try {
                    await Task.Delay(100, token);
                }
                catch (OperationCanceledException) {
                    // cts is cancelled once the input task has decided; this result is then unused.
                    return false;
                }
                if (listenForEnter) {
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
                    catch (InvalidOperationException) { }
                }
            }
            return true;
        });

        if (!listenForEnter) {
            return await delayTask;
        }

        // A real console is polled for Enter by the delay loop above; only redirected
        // input needs a reader. Either way this task ends early only for an Enter.
        var inputTask = Task.Run(async () => {
            bool isRedirected = false;
            try { isRedirected = Console.IsInputRedirected; } catch (InvalidOperationException) { }
            if (isRedirected) {
                return await WaitForEnterAsync(Console.In, token);
            }
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
            return false;
        });

        var completedTask = await Task.WhenAny(delayTask, inputTask);
        cts.Cancel();

        if (completedTask == inputTask && await inputTask) {
            Ui.Info("Wartezeit durch Benutzer (Enter) übersprungen.", "Skip");
            return true;
        }

        return await delayTask;
    }

    /// <summary>
    /// Waits for one line on <paramref name="reader"/> and returns true when it arrives. At end of
    /// input it keeps waiting until <paramref name="cancellationToken"/> fires and returns false:
    /// closed stdin means nobody can press Enter, so it must neither skip the wait (what EOF used
    /// to do) nor end it early.
    /// </summary>
    public static async Task<bool> WaitForEnterAsync(TextReader reader, CancellationToken cancellationToken) {
        try {
            if (await reader.ReadLineAsync(cancellationToken) != null) {
                return true;
            }
        }
        catch (OperationCanceledException) {
            return false;
        }
        catch (Exception ex) {
            Console.Error.WriteLine($"[{ex.GetType().Name}] {ex.Message}");
        }

        try {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) { }
        return false;
    }
}
