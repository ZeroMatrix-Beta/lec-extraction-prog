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
            bool isOutputRedirected = false;
            try { isOutputRedirected = Console.IsOutputRedirected || Console.IsErrorRedirected; } catch (InvalidOperationException) { }

            if (!isUnattended && !isOutputRedirected) {
                Ui.Detail("(Tipp: Du kannst jederzeit [Enter] drücken, um die Wartezeit sofort zu überspringen.)");
            }
            using var cts = new CancellationTokenSource();
            // Ctrl+C only raises the flag; the delay loop sees it within 100 ms and returns false.
            // Cancelling cts here instead would make Task.Delay throw out of SmartDelayAsync.
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
                // If output is redirected or running in unattended mode, avoid AnsiConsole.Status
                // because non-TTY/redirected streams cannot rewrite lines in place, causing hundreds
                // of duplicate status lines in log files.
                if (isUnattended || isOutputRedirected) {
                    Ui.Info($"Warte {seconds}s: {message}", "Delay");
                    int elapsedSeconds = 0;
                    while (elapsedSeconds < seconds) {
                        if (delayCanceled || cts.Token.IsCancellationRequested) return false;
                        int step = Math.Min(1, seconds - elapsedSeconds);
                        try {
                            await Task.Delay(step * 1000, cts.Token);
                        }
                        catch (OperationCanceledException) {
                            return false;
                        }
                        elapsedSeconds += step;
                    }
                    return true;
                }

                int lastRemaining = -1;
                bool completed = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse("yellow"))
                    .StartAsync($"Warte {seconds}s: {message}", async ctx => {
                        var delayTask = Task.Run(async () => {
                            int delaySteps = seconds * 10;
                            for (int i = 0; i < delaySteps; i++) {
                                if (delayCanceled || cts.Token.IsCancellationRequested) return false;
                                int remaining = seconds - (i / 10);
                                if (remaining != lastRemaining) {
                                    lastRemaining = remaining;
                                    ctx.Status($"Warte {remaining}s: {message}");
                                }
                                try {
                                    await Task.Delay(100, cts.Token);
                                }
                                catch (OperationCanceledException) {
                                    // cts is cancelled once the input task has decided; this result is then unused.
                                    return false;
                                }
                                if (!isUnattended) {
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

                        // A real console is polled for Enter by the delay loop above; only redirected
                        // input needs a reader. Either way this task ends early only for an Enter.
                        var inputTask = Task.Run(async () => {
                            bool isRedirected = false;
                            try { isRedirected = Console.IsInputRedirected; } catch (InvalidOperationException) { }
                            if (isRedirected) {
                                return await WaitForEnterAsync(Console.In, cts.Token);
                            }
                            try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
                            return false;
                        });

                        var completedTask = await Task.WhenAny(delayTask, inputTask);
                        cts.Cancel();

                        if (completedTask == inputTask && await inputTask) {
                            Ui.Info("Wartezeit durch Benutzer (Enter) übersprungen.", "Skip");
                            return true;
                        }

                        return await delayTask;
                    });

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
