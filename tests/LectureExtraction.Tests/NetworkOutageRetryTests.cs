using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// An internet outage (DNS cannot resolve the Google host) is waited out with TCP connect probes and does not
/// use up a retry attempt; any other error keeps its counted retry. The 2026-10-19 log showed the old
/// behaviour: attempt 2 failed at once with the same DNS error, then a flat 300 s wait.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class NetworkOutageRetryTests : IDisposable {
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;
    private readonly Func<int, string, Task<bool>> _previousDelay = ApiRetryPolicy.DelayAsync;
    private readonly int _previousMaxWait = NetworkAvailability.MaxWaitMinutes;
    private readonly List<int> _waits = [];

    public NetworkOutageRetryTests() {
        AnsiConsole.Console = new TestConsole();
        ApiRetryPolicy.DelayAsync = (seconds, _) => {
            _waits.Add(seconds);
            return Task.FromResult(true);
        };
        NetworkAvailability.Delay = (_, _) => Task.CompletedTask;
        NetworkAvailability.Probe = (_, _) => Task.FromResult(true);
    }

    public void Dispose() {
        AnsiConsole.Console = _previousConsole;
        ApiRetryPolicy.DelayAsync = _previousDelay;
        NetworkAvailability.MaxWaitMinutes = _previousMaxWait;
        NetworkAvailability.Delay = null;
        NetworkAvailability.Probe = null;
    }

    [Fact]
    public async Task A_no_route_error_waits_for_the_probe_and_skips_the_discard_message() {
        // No route, but the name would still resolve from the cache: only the probe tells when it is back.
        var console = new TestConsole();
        AnsiConsole.Console = console;
        int attempts = 0;
        int probes = 0;
        NetworkAvailability.Probe = (_, _) => Task.FromResult(++probes >= 3);

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts switch {
                    1 => Stream([], new ServerError("high demand", 503, "UNAVAILABLE")),
                    2 => Stream([], new HttpRequestException("unreachable", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.NetworkUnreachable))),
                    _ => Stream(["done"], null)
                };
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 2);

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Equal(3, probes);
        // Attempt 2 after the 503 says it discards; the resend after the outage does not.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(console.Output, "verworfen"));
    }

    [Fact]
    public async Task Dns_failures_do_not_use_up_attempts_and_are_resent_at_once() {
        int attempts = 0;
        int lookups = 0;
        NetworkAvailability.Probe = (_, _) => Task.FromResult(++lookups % 2 == 0); // down on the first lookup of each outage

        // maxRetries 2 would give up after the second failure if the DNS failures counted.
        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts <= 2
                    ? Stream([], NetworkAvailabilityTests.DnsFailure())
                    : Stream(["done"], null);
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 2);

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Empty(_waits); // no 300 s (or any other) backoff
    }

    [Fact]
    public async Task A_dns_failure_after_an_ordinary_retry_keeps_the_attempt_number() {
        int attempts = 0;
        int retries = 0;

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts switch {
                    1 => Stream([], new ServerError("high demand", 503, "UNAVAILABLE")),
                    2 => Stream([], NetworkAvailabilityTests.DnsFailure()),
                    _ => Stream(["done"], null)
                };
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 2,
            onRetry: () => retries++);

        // Attempt 1 (503) counted, attempt 2 (DNS) did not: the resend is attempt 2 again, so maxRetries 2 holds.
        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task An_outage_that_outlasts_the_maximum_wait_fails_like_an_exhausted_retry() {
        int attempts = 0;
        NetworkAvailability.MaxWaitMinutes = 0;
        NetworkAvailability.Probe = (_, _) => Task.FromResult(false);

        await Assert.ThrowsAsync<HttpRequestException>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], NetworkAvailabilityTests.DnsFailure()); },
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task A_503_still_counts_against_the_retry_limit() {
        int attempts = 0;

        await Assert.ThrowsAsync<ServerError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ServerError("high demand", 503, "UNAVAILABLE")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3));

        Assert.Equal(3, attempts);
        Assert.Equal(2, _waits.Count);
    }

    [Fact]
    public async Task A_dns_failure_in_a_single_response_call_is_not_counted_either() {
        int calls = 0;

        var result = await ApiRetryPolicy.ExecuteWithRetryAsync(
            () => {
                calls++;
                if (calls <= 3) throw NetworkAvailabilityTests.DnsFailure();
                return Task.FromResult("answer");
            },
            maxRetries: 2);

        Assert.Equal("answer", result);
        Assert.Equal(4, calls);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Endless_dns_failures_right_after_dns_answers_still_end() {
        // The resolver says "up" every time, the request says "no such host" every time: not an
        // outage but a broken resolver path. After ten uncounted resends the normal limit applies.
        int attempts = 0;

        await Assert.ThrowsAsync<HttpRequestException>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], NetworkAvailabilityTests.DnsFailure()); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3));

        Assert.Equal(10 + 3, attempts);
    }

    private static async IAsyncEnumerable<GenerateContentResponse> Stream(string[] texts, Exception? thenThrow, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        foreach (string text in texts) {
            await Task.Yield();
            yield return new GenerateContentResponse {
                Candidates = [new Candidate { Content = new Content { Role = "model", Parts = [new Part { Text = text }] } }]
            };
        }
        if (thenThrow != null) {
            await Task.Yield();
            throw thenThrow;
        }
    }
}
