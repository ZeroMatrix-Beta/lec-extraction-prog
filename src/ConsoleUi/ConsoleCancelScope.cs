using System;
using System.Threading;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] One Ctrl+C scope: while it lives, Ctrl+C cancels <see cref="Token"/> instead of
/// ending the process. Disposing it unsubscribes the handler, so with <c>using var</c> the handler
/// cannot outlive the operation even when it throws - several hand-written copies of this pattern
/// unsubscribed outside any <c>finally</c>.
///
/// <para>The handler runs on the signal thread. <see cref="CancellationTokenSource.Cancel()"/> can
/// throw there in two ways: <see cref="ObjectDisposedException"/> when Ctrl+C races with Dispose
/// (nothing left to cancel), and <see cref="AggregateException"/> when a registered callback throws.
/// Both are caught - an exception escaping a signal handler would end the process - and only the
/// second is reported, on stderr, because Spectre's live displays may be running.</para>
/// [Human] Bündelt die Ctrl+C-Behandlung: Ctrl+C bricht den Token ab statt das Programm zu beenden.
/// </summary>
public sealed class ConsoleCancelScope : IDisposable {
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    public ConsoleCancelScope() {
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    public CancellationToken Token => _cts.Token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e) {
        e.Cancel = true;
        try {
            _cts.Cancel();
        }
        catch (ObjectDisposedException) {
            // Ctrl+C arrived while the scope was being disposed; the operation is already over.
        }
        catch (AggregateException ex) {
            Console.Error.WriteLine($"[Ctrl+C] {ex.Describe()}");
        }
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }
        Console.CancelKeyPress -= OnCancelKeyPress;
        _cts.Dispose();
    }
}
