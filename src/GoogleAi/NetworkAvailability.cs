using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Tells a lost internet connection (DNS fails, no route) apart from every other error and
/// waits it out with DNS lookups only. A lookup sends nothing to Google, so polling costs no quota,
/// and a request that failed to resolve its host never left the machine, so
/// <see cref="ApiRetryPolicy"/> does not count it against its retry limit.
/// [Human] Wartet bei Internetausfall (DNS) gezielt auf die Rückkehr der Verbindung, ohne Anfragen
/// an Google zu schicken und ohne Versuche zu verbrauchen.
/// </summary>
public static class NetworkAvailability {
    /// <summary>The host every Gemini request goes to; the one whose DNS entry is polled.</summary>
    public const string GoogleApiHost = "generativelanguage.googleapis.com";

    /// <summary>Seconds between two DNS lookups while waiting. Set from <c>NetworkPollIntervalSeconds</c>.</summary>
    public static int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Minutes to wait for the connection before the request fails as before. Set from <c>NetworkMaxWaitMinutes</c>.</summary>
    public static int MaxWaitMinutes { get; set; } = 30;

    /// <summary>Takes the two values from a config; a value of zero or less keeps the current one.</summary>
    public static void Configure(int pollIntervalSeconds, int maxWaitMinutes) {
        if (pollIntervalSeconds > 0) PollIntervalSeconds = pollIntervalSeconds;
        if (maxWaitMinutes > 0) MaxWaitMinutes = maxWaitMinutes;
    }

    /// <summary>Replaces the DNS lookup of <see cref="ApiRetryPolicy"/>'s outage wait; tests only.</summary>
    public static Func<string, CancellationToken, Task<bool>>? Resolver { get; set; }

    /// <summary>Replaces the pause between two lookups of <see cref="ApiRetryPolicy"/>'s outage wait; tests only.</summary>
    public static Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    /// <summary>How often a status line is printed while waiting.</summary>
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMinutes(1);

    private static readonly SocketError[] LostConnectionErrors = [
        SocketError.HostNotFound,
        SocketError.TryAgain,
        SocketError.NoData,
        SocketError.NetworkUnreachable,
        SocketError.NetworkDown,
        SocketError.HostUnreachable,
    ];

    /// <summary>
    /// [AI Context] True when the request failed because the host could not be resolved or no route to
    /// it exists: an <see cref="HttpRequestException"/> (or any wrapper) whose inner
    /// <see cref="SocketException"/> is host-not-found, try-again, network-unreachable, network-down or
    /// host-unreachable, or whose <see cref="HttpRequestException.HttpRequestError"/> is a name
    /// resolution error. The SDK's API errors (<c>ClientError</c>/<c>ServerError</c>) are answers from
    /// the server and never qualify, whatever their message says.
    /// [Human] Erkennt "Host unbekannt" / "kein Netzwerk"; Server-Antworten wie 503 nie.
    /// </summary>
    public static bool IsDnsOrNoRouteError(Exception ex) {
        for (Exception? current = ex; current != null; current = current.InnerException) {
            if (current is Google.GenAI.ClientError or Google.GenAI.ServerError) {
                return false;
            }
        }

        for (Exception? current = ex; current != null; current = current.InnerException) {
            if (current is SocketException socket && Array.IndexOf(LostConnectionErrors, socket.SocketErrorCode) >= 0) {
                return true;
            }
            if (current is HttpRequestException http && http.HttpRequestError == HttpRequestError.NameResolutionError) {
                return true;
            }
            // Only for the transport exceptions: a model answer that merely mentions a host is no outage.
            if (current is HttpRequestException or SocketException &&
                (current.Message.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase))) {
                return true;
            }
        }
        return false;
    }

    /// <summary>The real lookup: true when <paramref name="host"/> resolves to at least one address.</summary>
    public static async Task<bool> ResolvesAsync(string host, CancellationToken cancellationToken) {
        try {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.Length > 0;
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception) {
            return false; // SocketException, but any failure of the lookup means "not reachable yet"
        }
    }

    /// <summary>
    /// [AI Context] Polls DNS for <paramref name="host"/> every <paramref name="pollInterval"/> until it
    /// resolves (returns true) or <paramref name="maxWait"/> has passed (returns false). Cancelling
    /// <paramref name="cancellationToken"/> also returns false. Prints at most one status line a minute
    /// and one line when the connection is back. The resolver, delay and clock are injectable so tests
    /// run without waiting.
    /// [Human] Fragt alle 15 s per DNS, ob die Verbindung zurück ist (kostet keine Quota).
    /// </summary>
    public static async Task<bool> WaitUntilHostResolvesAsync(
        string host,
        TimeSpan pollInterval,
        TimeSpan maxWait,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<bool>>? resolver = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null) {
        resolver ??= ResolvesAsync;
        delay ??= (span, token) => Task.Delay(span, token);
        utcNow ??= () => DateTime.UtcNow;
        if (pollInterval <= TimeSpan.Zero) pollInterval = TimeSpan.FromSeconds(1);

        DateTime started = utcNow();
        DateTime lastStatus = DateTime.MinValue;
        bool waited = false;

        try {
            while (true) {
                if (await resolver(host, cancellationToken)) {
                    if (waited) {
                        Ui.Info($"DNS für {host} funktioniert wieder (nach {Describe(utcNow() - started)}).", "Netzwerk");
                    }
                    return true;
                }

                TimeSpan elapsed = utcNow() - started;
                if (elapsed >= maxWait) {
                    Ui.Error($"DNS für {host} nach {Describe(elapsed)} nicht wieder da: gebe auf.", "Netzwerk");
                    return false;
                }

                if (!waited || utcNow() - lastStatus >= StatusInterval) {
                    Ui.Warn($"Warte auf DNS für {host} … ({Describe(elapsed)} von höchstens {Describe(maxWait)})", "Netzwerk");
                    lastStatus = utcNow();
                }
                waited = true;

                TimeSpan remaining = maxWait - elapsed;
                await delay(remaining < pollInterval ? remaining : pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) {
            return false;
        }
    }

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalSeconds} s";
}
