using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutoExtraction; // For ExtractionHelpers
using Google.GenAI.Types;

namespace Infrastructure;

/// <summary>
/// Provides a centralized, resilient execution wrapper for Google GenAI API calls.
/// Implements exponential backoff, server-suggested delay parsing, and user-cancellable waits.
/// </summary>
public static class ApiResilience {
  /// <summary>
  /// Executes a streaming API call with a robust retry mechanism.
  /// </summary>
  /// <param name="streamFactory">A function that creates the IAsyncEnumerable stream from the API.</param>
  /// <param name="onChunkReceived">An async action to process each received chunk from the stream.</param>
  /// <param name="cancellationToken">A token to cancel the operation.</param>
  /// <param name="maxRetries">Maximum number of retry attempts.</param>
  /// <param name="initialBackoff">Initial delay in seconds for the first retry.</param>
  /// <returns>True if the stream completed successfully, false if it was cancelled. Throws on unrecoverable errors.</returns>
  public static async Task<bool> ExecuteStreamWithRetryAsync(
      Func<IAsyncEnumerable<GenerateContentResponse>> streamFactory,
      Func<GenerateContentResponse, Task> onChunkReceived,
      CancellationToken cancellationToken,
      int maxRetries = 8,
      int initialBackoff = 45,
      string retryContext = "") {
    int backoff = initialBackoff;

    for (int attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        if (attempt > 1) {
          string contextMsg = string.IsNullOrWhiteSpace(retryContext) ? "" : $" [{retryContext}]";
          Console.WriteLine($"\n[API Retry]{contextMsg} Sending request (Attempt {attempt}/{maxRetries})...");
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
          var backoffResult = await HandleBackoffAsync(ex, attempt, maxRetries, backoff, retryContext);
          backoff = backoffResult.NewBackoff;
          if (!backoffResult.WaitSuccess) {
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
  /// Executes a non-streaming, single-response API call with a robust retry mechanism.
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
          string contextMsg = string.IsNullOrWhiteSpace(retryContext) ? "" : $" [{retryContext}]";
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
          var backoffResult = await HandleBackoffAsync(ex, attempt, maxRetries, backoff, retryContext);
          backoff = backoffResult.NewBackoff;
          if (!backoffResult.WaitSuccess) {
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

  private static bool IsTransientError(Exception ex) {
    string msg = ex.Message;
    string exStr = ex.ToString();
    return msg.Contains("429") || msg.Contains("503") || msg.Contains("502") || msg.Contains("500") ||
           exStr.Contains("ServerError") || msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
           msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) || msg.Contains("high demand", StringComparison.OrdinalIgnoreCase);
  }

  // [AI Context] Implementiert eine spezifische, lineare Backoff-Strategie.
  // Beim ersten Fehler (attempt == 1) wird eine eventuell vom Server vorgeschlagene Wartezeit ausgelesen und ein Puffer von 20s addiert.
  // Bei allen nachfolgenden Fehlern wird die vorherige Wartezeit linear um 30 Sekunden erhöht.
  // Dies vermeidet exponentielles Backoff, das zu exzessiv langen Wartezeiten führen kann.
  private static async Task<(bool WaitSuccess, int NewBackoff)> HandleBackoffAsync(Exception ex, int attempt, int maxRetries, int currentBackoff, string retryContext) {
    int waitTime;
    int nextBackoff;

    string contextMsg = string.IsNullOrWhiteSpace(retryContext) ? "" : $" [{retryContext}]";

    // [Human] Sonderbehandlung für "high demand"-Fehler: Feste Wartezeit von 3 Minuten.
    if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
      waitTime = 180; // 3 Minuten
      Console.WriteLine($"\n[Hohe Auslastung]{contextMsg} Das Modell ist stark nachgefragt. Warte pauschal 3 Minuten... (Versuch {attempt + 1}/{maxRetries}) (Oder drücke减drücke Enter für sofortigen Retry)");
      nextBackoff = waitTime; // Behält diesen Zustand für den nächsten Versuch bei, falls der Fehler ein anderer ist.
    }
    else {
      // On the very first failure, check for a server-suggested delay.
      if (attempt == 1) {
        var retryMatch = Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
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

    bool waitSuccess = await ExtractionHelpers.SmartDelayAsync(waitTime);
    return (waitSuccess, nextBackoff);
  }
}