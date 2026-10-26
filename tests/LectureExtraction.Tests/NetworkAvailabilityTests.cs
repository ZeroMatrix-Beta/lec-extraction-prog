using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins what counts as "the internet is down" (DNS fails, no route) and how the wait for it ends.
///
/// <para>On 2026-10-19 a short DNS outage during the last refinement used up an attempt at once and then
/// waited a flat 300 s. A 503 or a stream break must keep its normal counted retry, so the classifier
/// is deliberately narrow. The wait runs on a fake clock and a fake delay: no test sleeps.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class NetworkAvailabilityTests : IDisposable {
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;

    public NetworkAvailabilityTests() {
        AnsiConsole.Console = new TestConsole();
    }

    public void Dispose() {
        AnsiConsole.Console = _previousConsole;
    }

    public static HttpRequestException DnsFailure() => new(
        "Der angegebene Host ist unbekannt. (generativelanguage.googleapis.com:443)",
        new SocketException((int)SocketError.HostNotFound));

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.HostUnreachable)]
    public void A_socket_error_for_a_lost_connection_is_a_network_error(SocketError error) {
        var ex = new HttpRequestException("An error occurred while sending the request.", new SocketException((int)error));

        Assert.True(NetworkAvailability.IsDnsOrNoRouteError(ex));
    }

    [Fact]
    public void The_german_host_unknown_message_alone_is_a_network_error() {
        Assert.True(NetworkAvailability.IsDnsOrNoRouteError(new HttpRequestException("Der angegebene Host ist unbekannt. (generativelanguage.googleapis.com:443)")));
        Assert.True(NetworkAvailability.IsDnsOrNoRouteError(new HttpRequestException("No such host is known.")));
    }

    [Fact]
    public void A_name_resolution_error_is_found_through_a_wrapper() {
        var wrapped = new InvalidOperationException("outer", DnsFailure());

        Assert.True(NetworkAvailability.IsDnsOrNoRouteError(wrapped));
    }

    [Fact]
    public void Server_answers_and_other_transport_errors_are_not_network_errors() {
        Assert.False(NetworkAvailability.IsDnsOrNoRouteError(new ServerError("high demand", 503, "UNAVAILABLE")));
        Assert.False(NetworkAvailability.IsDnsOrNoRouteError(new ClientError("rate limited", 429, "RESOURCE_EXHAUSTED")));
        Assert.False(NetworkAvailability.IsDnsOrNoRouteError(new IOException("The response ended prematurely.")));
        Assert.False(NetworkAvailability.IsDnsOrNoRouteError(new TimeoutException("timed out")));
        Assert.False(NetworkAvailability.IsDnsOrNoRouteError(new HttpRequestException("reset", new SocketException((int)SocketError.ConnectionReset))));
    }

    [Fact]
    public void A_server_error_that_mentions_the_host_is_not_a_network_error() {
        // The text of an API error must never look like an outage.
        Assert.False(NetworkAvailability.IsDnsOrNoRouteError(new ServerError("Host ist unbekannt", 503, "UNAVAILABLE")));
    }

    [Fact]
    public async Task It_returns_true_after_the_second_poll_when_the_first_one_fails() {
        var clock = new FakeClock();
        int polls = 0;

        bool result = await NetworkAvailability.WaitUntilHostReachableAsync(
            "example.test", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30), CancellationToken.None,
            probe: (_, _) => Task.FromResult(++polls >= 2),
            delay: clock.DelayAsync,
            utcNow: clock.Now);

        Assert.True(result);
        Assert.Equal(2, polls);
        Assert.Equal([TimeSpan.FromSeconds(15)], clock.Delays);
    }

    [Fact]
    public async Task A_host_that_resolves_at_once_is_not_waited_for() {
        var clock = new FakeClock();

        bool result = await NetworkAvailability.WaitUntilHostReachableAsync(
            "example.test", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30), CancellationToken.None,
            probe: (_, _) => Task.FromResult(true), delay: clock.DelayAsync, utcNow: clock.Now);

        Assert.True(result);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task It_gives_up_once_the_maximum_wait_has_passed() {
        var clock = new FakeClock();
        int polls = 0;

        bool result = await NetworkAvailability.WaitUntilHostReachableAsync(
            "example.test", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), CancellationToken.None,
            probe: (_, _) => { polls++; return Task.FromResult(false); },
            delay: clock.DelayAsync,
            utcNow: clock.Now);

        Assert.False(result);
        // t = 0, 15, 30, 45, 60 s: the lookup at the deadline is the last one.
        Assert.Equal(5, polls);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.Elapsed);
    }

    [Fact]
    public async Task A_cancelled_wait_returns_false() {
        using var cts = new CancellationTokenSource();
        var clock = new FakeClock();

        bool result = await NetworkAvailability.WaitUntilHostReachableAsync(
            "example.test", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30), cts.Token,
            probe: (_, _) => Task.FromResult(false),
            delay: (span, token) => { cts.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            utcNow: clock.Now);

        Assert.False(result);
    }

    [Fact]
    public async Task At_most_one_status_line_a_minute_is_printed() {
        var console = new TestConsole();
        AnsiConsole.Console = console;
        var clock = new FakeClock();
        int polls = 0;

        await NetworkAvailability.WaitUntilHostReachableAsync(
            "example.test", TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30), CancellationToken.None,
            probe: (_, _) => Task.FromResult(++polls > 17), // back after 4 min 15 s
            delay: clock.DelayAsync, utcNow: clock.Now);

        string output = console.Output;
        int statusLines = output.Split("Warte auf Verbindung").Length - 1;
        Assert.Equal(5, statusLines); // t = 0, 1, 2, 3, 4 min
        Assert.Contains("wieder erreichbar", output);
    }

    /// <summary>A clock that only moves when the wait sleeps.</summary>
    private sealed class FakeClock {
        private readonly DateTime _start = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        public TimeSpan Elapsed { get; private set; }
        public System.Collections.Generic.List<TimeSpan> Delays { get; } = [];

        public DateTime Now() => _start + Elapsed;

        public Task DelayAsync(TimeSpan span, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Delays.Add(span);
            Elapsed += span;
            return Task.CompletedTask;
        }
    }
}
