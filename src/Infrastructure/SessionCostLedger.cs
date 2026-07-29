using System;
using System.Collections.Generic;
using System.Threading;

namespace LectureExtraction.Infrastructure;

/// <summary>
/// [AI Context] Counts what a run actually spends, in the currency that actually limits this app.
///
/// <para>Every other report here counts tokens. Tokens are not the binding constraint — <b>requests
/// per minute</b> are, which is why <c>VideoPartDelaySeconds</c> is 130 and
/// <c>HistoryRateLimitDelaySeconds</c> is 120. A warm-up that hits the prefix cache perfectly still
/// spends one request and a 120-second delay, and the token report shows neither: with
/// <c>HistoryBatchCount: 3</c> that is roughly six minutes of wall clock before the first video is
/// touched, invisible in the output. This ledger makes that visible (review finding F10).</para>
///
/// <para>Static because the things it measures are spread across the extraction session, the
/// refinement pipeline, the uploader and the retry policy, none of which share an object. It is
/// <see cref="Reset"/> at the start of each session from the main menu, so "session" means one trip
/// through one menu entry. All counters are interlocked: uploads run in parallel with generation.</para>
/// [Human] Zählt Anfragen und echte Wartezeit — die Größen, die diese App wirklich begrenzen,
/// im Gegensatz zu den überall gemeldeten Tokens.
/// </summary>
public static class SessionCostLedger {
    private static int _generationRequests;
    private static int _supportRequests;
    private static int _retriedAttempts;
    private static long _waitedTicks;
    private static int _delaysSkipped;
    private static long _startedAtTicks;

    /// <summary>Streaming generation calls — the expensive ones, one per model response attempt.</summary>
    public static int GenerationRequests => Volatile.Read(ref _generationRequests);

    /// <summary>Uploads, file-status polls and token counts: real requests, but not model output.</summary>
    public static int SupportRequests => Volatile.Read(ref _supportRequests);

    /// <summary>Attempts beyond the first, i.e. requests spent on retries rather than progress.</summary>
    public static int RetriedAttempts => Volatile.Read(ref _retriedAttempts);

    /// <summary>Wall-clock time spent inside rate-limit delays.</summary>
    public static TimeSpan TimeWaited => TimeSpan.FromTicks(Volatile.Read(ref _waitedTicks));

    /// <summary>Delays the user cut short by pressing Enter.</summary>
    public static int DelaysSkipped => Volatile.Read(ref _delaysSkipped);

    public static bool HasActivity => GenerationRequests > 0 || SupportRequests > 0 || TimeWaited > TimeSpan.Zero;

    public static void Reset() {
        Interlocked.Exchange(ref _generationRequests, 0);
        Interlocked.Exchange(ref _supportRequests, 0);
        Interlocked.Exchange(ref _retriedAttempts, 0);
        Interlocked.Exchange(ref _waitedTicks, 0);
        Interlocked.Exchange(ref _delaysSkipped, 0);
        Interlocked.Exchange(ref _startedAtTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// [AI Context] Records one request actually sent. Called per <i>attempt</i>, not per logical
    /// call, because that is what the quota counts: a call that succeeds on its third attempt spent
    /// three requests.
    /// [Human] Zählt jeden tatsächlich gesendeten Versuch, nicht jeden logischen Aufruf.
    /// </summary>
    public static void RecordRequest(bool isGeneration, int attempt) {
        if (isGeneration) {
            Interlocked.Increment(ref _generationRequests);
        }
        else {
            Interlocked.Increment(ref _supportRequests);
        }

        if (attempt > 1) {
            Interlocked.Increment(ref _retriedAttempts);
        }
    }

    public static void RecordWait(TimeSpan elapsed, bool skippedByUser) {
        Interlocked.Add(ref _waitedTicks, elapsed.Ticks);
        if (skippedByUser) {
            Interlocked.Increment(ref _delaysSkipped);
        }
    }

    /// <summary>
    /// [AI Context] The rows for the session-end table. Elapsed wall clock is included so the
    /// waiting share is readable as a proportion — "4 von 6 Minuten gewartet" is the number that
    /// answers whether a delay setting is worth its cost.
    /// [Human] Zeilen für die Zusammenfassung am Sitzungsende.
    /// </summary>
    public static IEnumerable<(string Key, string Value)> Summary() {
        var elapsed = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - Volatile.Read(ref _startedAtTicks)));
        var waited = TimeWaited;

        yield return ("Generierungs-Anfragen", $"{GenerationRequests:N0}");
        yield return ("Sonstige Anfragen (Upload / Status / Token-Zählung)", $"{SupportRequests:N0}");

        if (RetriedAttempts > 0) {
            yield return ("davon Wiederholungen", $"{RetriedAttempts:N0}");
        }

        yield return ("Wartezeit (Rate-Limits)", Format(waited));
        yield return ("Gesamtdauer der Sitzung", Format(elapsed));

        if (elapsed > TimeSpan.Zero && waited > TimeSpan.Zero) {
            yield return ("Anteil Wartezeit", $"{waited.TotalSeconds / elapsed.TotalSeconds:P0}");
        }

        if (DelaysSkipped > 0) {
            yield return ("Übersprungene Wartezeiten", $"{DelaysSkipped:N0}");
        }
    }

    private static string Format(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}min {span.Seconds}s"
            : span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}min {span.Seconds}s"
                : $"{span.TotalSeconds:F0}s";
}
