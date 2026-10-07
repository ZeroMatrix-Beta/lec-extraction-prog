using System;
using System.Collections.Generic;
using System.IO;
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
/// Pins the bounds of <see cref="ApiRetryPolicy.ExecuteStreamWithRetryAsync"/> and which errors it
/// retries at all.
///
/// <para>Every attempt is a billed request (for extraction it re-sends the video), so "retries until
/// it works" is a cost bug, not resilience. The case that motivated these tests: resetting the
/// failure counter whenever a chunk arrived made a stream that keeps dying after its first chunk
/// retry forever. And treating every 4xx as transient meant a wrong model name waited out minutes
/// of backoff eight times before failing.</para>
///
/// <para>The waits are replaced by an instant fake and the console by a <see cref="TestConsole"/>,
/// so the class joins the console collection.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ApiRetryPolicyTests : IDisposable {
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;
    private readonly Func<int, string, Task<bool>> _previousDelay = ApiRetryPolicy.DelayAsync;
    private readonly List<int> _waits = [];

    public ApiRetryPolicyTests() {
        AnsiConsole.Console = new TestConsole();
        ApiRetryPolicy.DelayAsync = (seconds, _) => {
            _waits.Add(seconds);
            return Task.FromResult(true);
        };
    }

    public void Dispose() {
        AnsiConsole.Console = _previousConsole;
        ApiRetryPolicy.DelayAsync = _previousDelay;
    }

    [Fact]
    public async Task A_stream_that_keeps_dying_after_one_chunk_stops_at_the_total_cap() {
        int attempts = 0;

        await Assert.ThrowsAsync<ServerError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream(["x"], new ServerError("unavailable", 503, "UNAVAILABLE")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3));

        // Each attempt made progress, so the consecutive-failure counter never reached 3;
        // the total cap (twice maxRetries) is what ended it.
        Assert.Equal(6, attempts);
    }

    [Fact]
    public async Task Failures_without_any_chunk_stop_at_maxRetries() {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError("rate limited", 429, "RESOURCE_EXHAUSTED")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3));

        Assert.Equal(3, attempts);
        Assert.Equal(2, _waits.Count);
    }

    [Theory]
    [InlineData(400, "FAILED_PRECONDITION")]
    [InlineData(401, "UNAUTHENTICATED")]
    [InlineData(403, "PERMISSION_DENIED")]
    [InlineData(404, "NOT_FOUND")]
    public async Task A_client_error_other_than_408_or_429_fails_on_the_first_attempt(int statusCode, string status) {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError($"models/gemini-x is {status}", statusCode, status)); },
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Resume_keeps_the_partial_text_instead_of_retrying() {
        int attempts = 0;
        int retries = 0;
        string received = "";

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream(["\\section{Gruppen}\n", "Eine Gruppe ist"], new ServerError("unavailable", 503, "UNAVAILABLE")); },
            chunk => { received += chunk.Candidates![0].Content!.Parts![0].Text; return Task.CompletedTask; },
            CancellationToken.None,
            onRetry: () => retries++,
            resumeOnPartialProgress: true);

        Assert.True(result);
        Assert.Equal(1, attempts);
        Assert.Equal(0, retries);
        Assert.Equal("\\section{Gruppen}\nEine Gruppe ist", received);
    }

    [Fact]
    public async Task OnRetry_runs_once_before_each_new_attempt() {
        int attempts = 0;
        int retries = 0;

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts < 3
                    ? Stream([], new ServerError("internal", 500, "INTERNAL"))
                    : Stream(["done"], null);
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            onRetry: () => retries++);

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, retries);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    public void IsTransientError_classifies_client_errors_by_status(int statusCode, bool expected) {
        Assert.Equal(expected, ApiRetryPolicy.IsTransientError(new ClientError("error", statusCode, "STATUS")));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void IsTransientError_retries_server_errors(int statusCode) {
        Assert.True(ApiRetryPolicy.IsTransientError(new ServerError("error", statusCode, "STATUS")));
    }

    [Fact]
    public void An_api_error_is_not_a_lost_connection() {
        // ClientError derives from HttpRequestException; it must not trigger the 5-minute network pause.
        Assert.False(ApiRetryPolicy.IsNetworkConnectionError(new ClientError("not found", 404, "NOT_FOUND")));
        Assert.True(ApiRetryPolicy.IsNetworkConnectionError(new HttpRequestException("No such host is known.")));
    }

    [Fact]
    public void A_number_that_merely_contains_500_is_not_a_server_error() {
        Assert.False(ApiRetryPolicy.IsTransientError(new InvalidDataException("The prompt used 1500 tokens.")));
        Assert.True(ApiRetryPolicy.IsTransientError(new InvalidDataException("Response status code 503 (Service Unavailable).")));
    }

    [Fact]
    public void ApiStatusCode_reads_a_wrapped_error() {
        var wrapped = new InvalidOperationException("outer", new ClientError("inner", 429, "RESOURCE_EXHAUSTED"));

        Assert.Equal(429, ApiRetryPolicy.ApiStatusCode(wrapped));
        Assert.Null(ApiRetryPolicy.ApiStatusCode(new IOException("disk")));
    }

    private static async IAsyncEnumerable<GenerateContentResponse> Stream(string[] texts, Exception? thenThrow, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        foreach (string text in texts) {
            await Task.Yield();
            yield return new GenerateContentResponse {
                Candidates = [new Candidate { Content = new Content { Role = "model", Parts = [new Part { Text = text }] } }]
            };
        }
        if (thenThrow != null) {
            throw thenThrow;
        }
    }
}
