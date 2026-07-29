using System;
using System.Linq;
using System.Threading.Tasks;
using LectureExtraction.Infrastructure;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the accounting behind review finding F10.
///
/// <para>The number that matters is <i>requests actually sent</i>, not logical calls: a generation
/// that succeeds on its third attempt spent three requests against the quota, and reporting it as
/// one would reproduce exactly the blind spot F10 describes. These tests exist because that
/// distinction is invisible at the call site.</para>
///
/// <para>The ledger is static and interlocked because uploads run in parallel with generation, so
/// the concurrency test is not theoretical.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class SessionCostLedgerTests {
    [Fact]
    public void Counts_each_attempt_not_each_logical_call() {
        SessionCostLedger.Reset();

        // One logical generation that needed three attempts.
        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 1);
        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 2);
        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 3);

        Assert.Equal(3, SessionCostLedger.GenerationRequests);
        Assert.Equal(2, SessionCostLedger.RetriedAttempts);
    }

    [Fact]
    public void Separates_generation_from_upload_and_status_traffic() {
        SessionCostLedger.Reset();

        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 1);
        SessionCostLedger.RecordRequest(isGeneration: false, attempt: 1);
        SessionCostLedger.RecordRequest(isGeneration: false, attempt: 1);

        Assert.Equal(1, SessionCostLedger.GenerationRequests);
        Assert.Equal(2, SessionCostLedger.SupportRequests);
        Assert.Equal(0, SessionCostLedger.RetriedAttempts);
    }

    [Fact]
    public void Accumulates_wait_time_and_counts_only_the_skipped_ones() {
        SessionCostLedger.Reset();

        SessionCostLedger.RecordWait(TimeSpan.FromSeconds(130), skippedByUser: false);
        SessionCostLedger.RecordWait(TimeSpan.FromSeconds(120), skippedByUser: false);
        SessionCostLedger.RecordWait(TimeSpan.FromSeconds(5), skippedByUser: true);

        Assert.Equal(TimeSpan.FromSeconds(255), SessionCostLedger.TimeWaited);
        Assert.Equal(1, SessionCostLedger.DelaysSkipped);
    }

    [Fact]
    public void Reset_clears_everything_so_one_menu_entry_is_one_session() {
        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 2);
        SessionCostLedger.RecordWait(TimeSpan.FromSeconds(130), skippedByUser: true);

        SessionCostLedger.Reset();

        Assert.Equal(0, SessionCostLedger.GenerationRequests);
        Assert.Equal(0, SessionCostLedger.SupportRequests);
        Assert.Equal(0, SessionCostLedger.RetriedAttempts);
        Assert.Equal(TimeSpan.Zero, SessionCostLedger.TimeWaited);
        Assert.Equal(0, SessionCostLedger.DelaysSkipped);
        Assert.False(SessionCostLedger.HasActivity);
    }

    [Fact]
    public void A_config_menu_visit_reports_nothing() {
        SessionCostLedger.Reset();
        Assert.False(SessionCostLedger.HasActivity);

        SessionCostLedger.RecordRequest(isGeneration: false, attempt: 1);
        Assert.True(SessionCostLedger.HasActivity);
    }

    [Fact]
    public void Summary_reports_the_waiting_share_which_is_the_point_of_the_finding() {
        SessionCostLedger.Reset();
        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 1);
        SessionCostLedger.RecordWait(TimeSpan.FromSeconds(360), skippedByUser: false);

        var rows = SessionCostLedger.Summary().ToDictionary(r => r.Key, r => r.Value);

        Assert.Equal("1", rows["Generierungs-Anfragen"]);
        Assert.Equal("6min 0s", rows["Wartezeit (Rate-Limits)"]);
        Assert.True(rows.ContainsKey("Anteil Wartezeit"));
    }

    [Fact]
    public void Summary_hides_rows_that_would_only_be_noise() {
        SessionCostLedger.Reset();
        SessionCostLedger.RecordRequest(isGeneration: true, attempt: 1);

        var keys = SessionCostLedger.Summary().Select(r => r.Key).ToList();

        Assert.DoesNotContain("davon Wiederholungen", keys);
        Assert.DoesNotContain("Übersprungene Wartezeiten", keys);
    }

    [Fact]
    public async Task Counters_survive_parallel_uploads_and_generation() {
        SessionCostLedger.Reset();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => {
            for (int n = 0; n < 250; n++) {
                SessionCostLedger.RecordRequest(isGeneration: i % 2 == 0, attempt: 1);
                SessionCostLedger.RecordWait(TimeSpan.FromMilliseconds(1), skippedByUser: false);
            }
        })));

        Assert.Equal(1000, SessionCostLedger.GenerationRequests);
        Assert.Equal(1000, SessionCostLedger.SupportRequests);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), SessionCostLedger.TimeWaited);
    }
}
