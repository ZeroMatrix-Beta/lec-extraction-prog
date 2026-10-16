using System;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Which API-key variable a client is counted under in the quota view. It used to be "the key resolved
/// last", so a client built after another key had been read (the uploader's ClientFactory after the
/// refinement key) was counted under the wrong key. Writes to the console, hence the collection.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ClientKeyTaggingTests : IDisposable {
    private const string KeyA = "API_KEY-lecx-test-a";
    private const string KeyB = "API_KEY-lecx-test-b";
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;

    public ClientKeyTaggingTests() {
        AnsiConsole.Console = new TestConsole();
        Environment.SetEnvironmentVariable(KeyA, "test-value-a");
        Environment.SetEnvironmentVariable(KeyB, "test-value-b");
    }

    public void Dispose() {
        Environment.SetEnvironmentVariable(KeyA, null);
        Environment.SetEnvironmentVariable(KeyB, null);
        AnsiConsole.Console = _previousConsole;
    }

    [Fact]
    public void A_client_is_tagged_with_its_own_key_even_when_another_key_was_resolved_since() {
        string a = GoogleAiClientBuilder.ResolveApiKeyByName(KeyA)!;
        string b = GoogleAiClientBuilder.ResolveApiKeyByName(KeyB)!;

        var clientA = GoogleAiClientBuilder.BuildAiStudioClient(a);
        var clientB = GoogleAiClientBuilder.BuildAiStudioClient(b);

        Assert.Equal(KeyA, GoogleAiClientBuilder.GetEnvVarName(clientA));
        Assert.Equal(KeyB, GoogleAiClientBuilder.GetEnvVarName(clientB));
    }
}
