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
      int initialBackoff = 30) {
    int backoff = initialBackoff;

    for (int attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        if (attempt > 1) {
          Console.WriteLine($"\n[API Retry] Sending request (Attempt {attempt}/{maxRetries})...");
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
          var backoffResult = await HandleBackoffAsync(ex, attempt, maxRetries, backoff);
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
      int initialBackoff = 30) where T : class {
    int backoff = initialBackoff;

    for (int attempt = 1; attempt <= maxRetries; attempt++) {
      try {
        if (attempt > 1) {
          Console.WriteLine($"\n[API Retry] Sending request (Attempt {attempt}/{maxRetries})...");
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
          var backoffResult = await HandleBackoffAsync(ex, attempt, maxRetries, backoff);
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

  private static async Task<(bool WaitSuccess, int NewBackoff)> HandleBackoffAsync(Exception ex, int attempt, int maxRetries, int backoff) {
    var retryMatch = Regex.Match(ex.Message, @"""retryDelay""\s*:\s*""(\d+)s""");
    bool waitSuccess;
    if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
      int waitTime = serverSuggestedDelay + 10; // Add a buffer
      Console.WriteLine($"\n[Rate Limit] API suggests waiting. Waiting {waitTime} seconds... (Attempt {attempt}/{maxRetries})");
      waitSuccess = await ExtractionHelpers.SmartDelayAsync(waitTime);
    }
    else {
      Console.WriteLine($"\n[Rate Limit / Overload] Waiting {backoff} seconds... (Attempt {attempt}/{maxRetries})");
      waitSuccess = await ExtractionHelpers.SmartDelayAsync(backoff);
    }

    backoff *= 2; // Always increase backoff for next time
    return (waitSuccess, backoff);
  }
}