using System;
using Google.GenAI;
using Google.GenAI.Types;

namespace GoogleGenAi;

/// <summary>
/// [AI Context] Encapsulates the configuration, credential resolution, and instantiation of the Google GenAI SDK Client.
/// Bypasses the SDK for raw REST API diagnostics when necessary.
/// [Human] Kümmert sich komplett um die Verbindung zu Google (API-Keys laden, Client bauen, Modelle abfragen).
/// </summary>
public static class GoogleAiClientBuilder
{
  /// <summary>
  /// [AI Context] Environment variable parser for robust credential loading across OS environments.
  /// [Human] Sucht deinen API Key in den Windows Umgebungsvariablen. So musst du ihn nicht unsicher in den Code schreiben.
  /// Unterstützt bis zu drei verschiedene Schlüssel (z.B. privat, Uni, Arbeit), zwischen denen du im Code wechseln kannst.
  /// </summary>
  public static string? ResolveApiKey(int activeKeyProfile = 1)
  {
    // [AI Context] Checks Process, User, and Machine environment variables to handle Windows permission contexts gracefully.
    // Required because User/Machine level environment variables are not always automatically inherited by the running Process unless explicitly reloaded or restarted.
    // [AI Context] Dynamically resolves the environment variable name based on the requested profile index. No hardcoded switch needed.
    string envVarName = $"API_KEY-ai-studio-test-project-{activeKeyProfile}";

    string? apiKey = System.Environment.GetEnvironmentVariable(envVarName)
                  ?? System.Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.User)
                  ?? System.Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.Machine);

    Console.WriteLine($"  [INFO] Verwende {envVarName} (Projekt {activeKeyProfile})");

    if (string.IsNullOrEmpty(apiKey))
    {
      Console.WriteLine($"Fehler: Der API-Key '{envVarName}' wurde in den Umgebungsvariablen nicht gefunden.");
      return null;
    }

    return apiKey;
  }

  /// <summary>
  /// [AI Context] Initializes the GenAI Client for Google AI Studio (Developer API).
  /// </summary>
  public static Client BuildAiStudioClient(string apiKey)
  {
    var options = new HttpOptions
    {
      Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
    };
    Console.WriteLine("  [INFO] Verbinde mit Google AI Studio API...");
    return new Client(apiKey: apiKey, httpOptions: options);
  }

  /// <summary>
  /// [AI Context] Initializes the GenAI Client for Google Cloud Vertex AI (Enterprise API).
  /// </summary>
  public static Client BuildVertexClient(string projectId, string location)
  {
    var options = new HttpOptions
    {
      Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
    };
    Console.WriteLine($"  [INFO] Verbinde mit Google Cloud Vertex AI (Projekt: {projectId})...");
    return new Client(
        vertexAI: true,
        project: projectId,
        location: location,
        httpOptions: options
    );
  }
}