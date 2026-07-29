using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Provides a centralized, resilient execution wrapper for Google GenAI API calls.
/// Implements linear backoff, server-suggested delay parsing, and user-cancellable waits.
/// [Human] Diese Klasse schützt das Programm vor API-Ausfällen und Ratelimits. Sie wiederholt fehlgeschlagene Google-Anfragen intelligent.
/// </summary>
public static partial class ApiRetryPolicy {
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
                    Ui.Warn($"{contextMsg} Sende Anfrage neu (Versuch {attempt}/{maxRetries}). Puffer wird zurückgesetzt...", "API Retry");
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
                Ui.Error($"{ex.GetType().Name}: {ex.Message}", "API");

                if (IsTransientError(ex) && attempt < maxRetries) {
                    var (WaitSuccess, NewBackoff) = await HandleBackoffAsync(ex, attempt, maxRetries, backoff, retryContext);
                    backoff = NewBackoff;
                    if (!WaitSuccess) {
                        return false; // User cancelled the wait
                    }
                }
                else {
                    Ui.Error($"Unrecoverable error after {attempt} attempts.", "API Failure");
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
                    Ui.Detail($"{contextMsg} Sending request (Attempt {attempt}/{maxRetries})...", "API Retry");
                }
                return await apiCall();
            }
            catch (Exception ex) when (ex is OperationCanceledException || ex.InnerException is OperationCanceledException) {
                Ui.Warn("Operation cancelled by user.", "API");
                return null;
            }
            catch (Exception ex) {
                Ui.Error($"{ex.GetType().Name}: {ex.Message}", "API");

                if (IsTransientError(ex) && attempt < maxRetries) {
                    var (WaitSuccess, NewBackoff) = await HandleBackoffAsync(ex, attempt, maxRetries, backoff, retryContext);
                    backoff = NewBackoff;
                    if (!WaitSuccess) {
                        return null; // User cancelled the wait
                    }
                }
                else {
                    Ui.Error($"Unrecoverable error after {attempt} attempts.", "API Failure");
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

        // Explicit rate limit, schema, or server error HTTP status codes should be handled by regular backoff or fail fast, not network pause.
        if (msg.Contains("429") || msg.Contains("503") || msg.Contains("502") || msg.Contains("500") ||
            msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Only text is supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Thinking level is not supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase)) {
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
        string msg = ex.Message;
        string exStr = ex.ToString();

        // Non-recoverable API schema errors must fail fast and not trigger retries
        if (msg.Contains("Only text is supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Thinking level is not supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (IsNetworkConnectionError(ex)) return true;

        return msg.Contains("429") || msg.Contains("503") || msg.Contains("502") || msg.Contains("500") ||
               exStr.Contains("ServerError") || exStr.Contains("ClientError") ||
               msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("QuotaFailure", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("high demand", StringComparison.OrdinalIgnoreCase);
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
            Ui.Warn($"{contextMsg} Verbindung zum Google-Server unterbrochen ({ex.GetType().Name}: {ex.Message}).", "Netzwerk-Fehler");
            Ui.Detail("Keine Panik! Du hast jetzt 300 Sekunden (5 Minuten) Zeit, um deinen Hotspot oder deine Internetverbindung zu reparieren...");
            Ui.Detail($"--> Sobald die Verbindung wieder steht, drücke ENTER, um sofort weiterzumachen! (Versuch {attempt + 1}/{maxRetries})");
            delayMessage = "Warte auf Wiederherstellung der Internetverbindung / Hotspot...";
            nextBackoff = currentBackoff;
        }
        else if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
            waitTime = 180; // 3 Minuten
            Ui.Warn($"{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)", "Hohe Auslastung");
            nextBackoff = waitTime;
        }
        else {
            // On the very first failure, check for a server-suggested delay.
            if (attempt == 1) {
                var retryMatch = MyRegex().Match(ex.Message);
                if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                    waitTime = serverSuggestedDelay + 20;
                    Ui.Warn($"{contextMsg} API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)", "Rate Limit");
                }
                else {
                    waitTime = currentBackoff; // Use the initial backoff from the caller
                    Ui.Warn($"{contextMsg} Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)", "Rate Limit / Überlastung");
                }
                nextBackoff = waitTime;
            }
            else {
                waitTime = currentBackoff + 30;
                Ui.Warn($"{contextMsg} Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {attempt + 1}/{maxRetries}) (Oder drücke Enter für sofortigen Retry)", "Rate Limit");
                nextBackoff = waitTime;
            }
        }

        bool waitSuccess = await InteractiveDelay.SmartDelayAsync(waitTime, delayMessage);
        return (waitSuccess, nextBackoff);
    }

    [GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)s""")]
    private static partial Regex MyRegex();
}