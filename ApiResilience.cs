using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoExtraction; // For ExtractionHelpers
using Google.GenAI.Types;

namespace Infrastructure;

/// <summary>
/// [AI Context] Provides a centralized, resilient execution wrapper for Google GenAI API calls.
/// Implements linear backoff, server-suggested delay parsing, and user-cancellable waits.
/// [Human] Diese Klasse schützt das Programm vor API-Ausfällen und Ratelimits. Sie wiederholt fehlgeschlagene Google-Anfragen intelligent.
/// </summary>
public static partial class ApiResilience {
    /// <summary>
    /// [AI Context] Executes a streaming API call with a robust retry mechanism.
    /// On each retry, the optional <paramref name="onRetry"/> callback is invoked BEFORE the new attempt
    /// so callers can reset their accumulation buffers (e.g. <c>chunkResp = ""</c>) to prevent the
    /// partial-stream leak that occurs when a transient 503 mid-stream causes duplicate/corrupt output.
    /// [Human] Führt eine Google API Streaming-Anfrage (für fließenden Text) mit automatischen Wiederholungen durch.
    /// Der optionale onRetry-Callback erlaubt es dem Aufrufer, seinen Textpuffer vor jedem neuen Versuch zurückzusetzen.
    /// </summary>
    /// <param name="streamFactory">A function that creates the IAsyncEnumerable stream from the API.</param>
    /// <param name="onChunkReceived">An async action to process each received chunk from the stream.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="initialBackoff">Initial delay in seconds for the first retry.</param>
    /// <param name="retryContext">Human-readable label printed in retry log messages.</param>
    /// <param name="onRetry">Optional callback invoked before every retry (attempt > 1). Use it to clear accumulation buffers.</param>
    /// <returns>True if the stream completed successfully, false if it was cancelled. Throws on unrecoverable errors.</returns>
    public static async Task<bool> ExecuteStreamWithRetryAsync(
        Func<IAsyncEnumerable<GenerateContentResponse>> streamFactory,
        Func<GenerateContentResponse, Task> onChunkReceived,
        CancellationToken cancellationToken,
        int maxRetries = 8,
        int initialBackoff = 130,
        string retryContext = "",
        Action? onRetry = null) {
        int backoff = initialBackoff;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            try {
                if (attempt > 1) {
                    string contextMsg = string.IsNullOrWhiteSpace(retryContext) ? "" : $" [Current Step: {retryContext}]";
                    Console.WriteLine($"\n[API Retry]{contextMsg} Sende Anfrage neu (Versuch {attempt}/{maxRetries}). Puffer wird zurückgesetzt...");
                    onRetry?.Invoke();
                }

                var responseStream = streamFactory();
                await foreach (var chunk in responseStream.WithCancellation(cancellationToken)) {
                    if (cancellationToken.IsCancellationRequested) break;
                    await onChunkReceived(chunk);
                }

                return !cancellationToken.IsCancellationRequested;
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException) {
                return false; // User cancelled
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception Caught] Type: {ex.GetType().Name}");
                Console.WriteLine($"Original Error: {ex.Message}");

                if (IsTransientError(ex) && attempt < maxRetries) {
                    var (WaitSuccess, NewBackoff) = await HandleBackoffAsync(ex, attempt, maxRetries, backoff, retryContext);
                    backoff = NewBackoff;
                    if (!WaitSuccess) {
                        return false; // User cancelled the wait
                    }
                }
                else {
                    Console.WriteLine($"\n[API Failure] Unrecoverable error after {attempt} attempts.");
                    throw; // Re-throw for the caller to handle
                }
            }
        }
        return false; // All retries failed
    }

    /// <summary>
    /// [AI Context] Executes a non-streaming, single-response API call with a robust retry mechanism.
    /// [Human] Führt eine einmalige API-Anfrage (für strukturierte Daten) mit automatischen Wiederholungen aus.
    /// </summary>
    public static async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T>> apiCall,
        int maxRetries = 8,
        int initialBackoff = 45,
        string retryContext = "") where T : class {
        int backoff = initialBackoff;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            try {
                if (attempt > 1) {
                    string contextMsg = string.IsNullOrWhiteSpace(retryContext) ? "" : $" [Current Step: {retryContext}]";
                    Console.WriteLine($"\n[API Retry]{contextMsg} Sending request (Attempt {attempt}/{maxRetries})...");
                }
                return await apiCall();
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException) {
                Console.WriteLine("\n[API] Operation cancelled by user.");
                return null;
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[Exception Caught] Type: {ex.GetType().Name}");
                Console.WriteLine($"Original Error: {ex.Message}");

                if (IsTransientError(ex) && attempt < maxRetries) {
                    var (WaitSuccess, NewBackoff) = await HandleBackoffAsync(ex, attempt, maxRetries, backoff, retryContext);
                    backoff = NewBackoff;
                    if (!WaitSuccess) {
                        return null; // User cancelled the wait
                    }
                }
                else {
                    Console.WriteLine($"\n[API Failure] Unrecoverable error after {attempt} attempts.");
                    return null;
                }
            }
        }
        return null; // All retries failed
    }

    /// <summary>
    /// [AI Context] Identifies network connectivity drops (e.g. Wi-Fi disconnection, mobile hotspot drop).
    /// [Human] Erkennt, ob die Internetverbindung unterbrochen wurde (z.B. Hotspot ausgefallen).
    /// </summary>
    public static bool IsNetworkConnectionError(Exception ex) {
        string msg = ex.Message;
        string exStr = ex.ToString();

        // Explicit rate limit or server error HTTP status codes should be handled by regular backoff, not network pause.
        if (msg.Contains("429") || msg.Contains("503") || msg.Contains("502") || msg.Contains("500") ||
            msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return ex is System.Net.Http.HttpRequestException ||
               ex is System.Net.Sockets.SocketException ||
               ex is System.IO.IOException ||
               ex is TimeoutException ||
               ex.InnerException is System.Net.Sockets.SocketException ||
               ex.InnerException is System.Net.Http.HttpRequestException ||
               ex.InnerException is TimeoutException ||
               msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Error while copying content to a stream", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("A connection attempt failed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("An error occurred while sending the request", StringComparison.OrdinalIgnoreCase) ||
               exStr.Contains("SocketException");
    }

    /// <summary>
    /// [AI Context] Determines if an exception is likely recoverable via retry.
    /// [Human] Erkennt, ob ein Fehler nur vorübergehend ist (z.B. Netzwerk-Wackler, Server überlastet) oder ob wir wirklich abbrechen müssen.
    /// </summary>
    public static bool IsTransientError(Exception ex) {
        if (IsNetworkConnectionError(ex)) return true;
        string msg = ex.Message;
        string exStr = ex.ToString();
        return msg.Contains("429") || msg.Contains("503") || msg.Contains("502") || msg.Contains("500") ||
               exStr.Contains("ServerError") || msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || msg.Contains("high demand", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [AI Context] Implements a specific linear backoff strategy.
    /// On the first failure, reads server-suggested wait time and adds a 20s buffer. 
    /// On subsequent failures, increases wait time linearly by 30 seconds.
    /// [Human] Wenn die API überlastet ist oder das Netzwerk abgreißt, berechnet diese Methode, wie lange wir warten müssen.
    /// </summary>
    private static async Task<(bool WaitSuccess, int NewBackoff)> HandleBackoffAsync(Exception ex, int attempt, int maxRetries, int currentBackoff, string retryContext) {
        int waitTime;
        int nextBackoff;

        string contextMsg = string.IsNullOrWhiteSpace(retryContext) ? "" : $" [Current Step: {retryContext}]";
        string delayMessage = "Still waiting for the acknowledgment / processing...";

        if (IsNetworkConnectionError(ex)) {
            waitTime = 300; // 5 Minuten
            Console.WriteLine($"\n[Netzwerk-Fehler]{contextMsg} Verbindung zum Google-Server unterbrochen ({ex.GetType().Name}: {ex.Message}).");
            Console.WriteLine($"  Keine Panik! Du hast jetzt 300 Sekunden (5 Minuten) Zeit, um deinen Hotspot oder deine Internetverbindung zu reparieren...");
            Console.WriteLine($"  --> Sobald die Verbindung wieder steht, drücke ENTER, um sofort weiterzumachen! (Versuch {attempt + 1}/{maxRetries})");
            delayMessage = "Warte auf Wiederherstellung der Internetverbindung / Hotspot...";
            nextBackoff = currentBackoff;
        }
        else if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
            waitTime = 180; // 3 Minuten
            Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
            nextBackoff = waitTime;
        }
        else {
            // On the very first failure, check for a server-suggested delay.
            if (attempt == 1) {
                var retryMatch = MyRegex().Match(ex.Message);
                if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                    waitTime = serverSuggestedDelay + 20;
                    Console.WriteLine($"\n[Rate Limit]{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                }
                else {
                    waitTime = currentBackoff; // Use the initial backoff from the caller
                    Console.WriteLine($"\n[Rate Limit / Überlastung]{contextMsg} Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                }
                nextBackoff = waitTime;
            }
            else {
                waitTime = currentBackoff + 30;
                Console.WriteLine($"\n[Rate Limit]{contextMsg} Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)");
                nextBackoff = waitTime;
            }
        }

        bool waitSuccess = await ExtractionHelpers.SmartDelayAsync(waitTime, delayMessage);
        return (waitSuccess, nextBackoff);
    }

    [GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)s""")]
    private static partial Regex MyRegex();
}