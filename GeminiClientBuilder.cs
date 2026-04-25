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
    string? apiKey1 = System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-1")
                   ?? System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-1", EnvironmentVariableTarget.User)
                   ?? System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-1", EnvironmentVariableTarget.Machine);
    string? apiKey2 = System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-2")
                   ?? System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-2", EnvironmentVariableTarget.User)
                   ?? System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-2", EnvironmentVariableTarget.Machine);
    string? apiKey3 = System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-3")
                   ?? System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-3", EnvironmentVariableTarget.User)
                   ?? System.Environment.GetEnvironmentVariable("API_KEY-ai-studio-test-project-3", EnvironmentVariableTarget.Machine);

    string apiKey = "";

    switch (activeKeyProfile)
    {
      case 3:
        apiKey = apiKey3 ?? "";
        Console.WriteLine("  [INFO] Verwende API_KEY-ai-studio-test-project-3 (Projekt 3)");
        break;
      case 2:
        apiKey = apiKey2 ?? "";
        Console.WriteLine("  [INFO] Verwende API_KEY-ai-studio-test-project-2 (Projekt 2)");
        break;
      case 1:
      default:
        apiKey = apiKey1 ?? "";
        Console.WriteLine("  [INFO] Verwende API_KEY-ai-studio-test-project-1 (Projekt 1)");
        break;
    }

    if (string.IsNullOrEmpty(apiKey))
    {
      Console.WriteLine("Fehler: Keiner der API-Keys (Projekt 1, 2 oder 3) wurde in den Umgebungsvariablen gefunden.");
      return null;
    }

    return apiKey;
  }

  /// <summary>
  /// [AI Context] Initializes the GenAI Client with appropriate endpoints based on user configuration.
  /// Includes custom timeout extensions for large file uploads.
  /// [Human] Baut den eigentlichen "Client", also die Verbindungsschnittstelle zu Google, auf.
  /// </summary>
  public static Client BuildClient(bool useVertexAi, string selectedModel, string apiKey, out bool isAiStudio)
  {
    // [AI Context] Increases HttpClient timeout to 20 minutes to prevent socket closures during large video file polling.
    // Der Standard-Timeout (100s) ist für große Datei-Uploads/Verarbeitung zu kurz.
    var options = new HttpOptions
    {
      Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
    };

    // [AI Context] Gemma models are primarily served via AI Studio developer endpoints.
    // [Human] Open-Source Modelle wie 'gemma' zwingen wir ins kostenlose AI Studio, da sie auf Vertex AI oft anders heißen oder fehlen.
    isAiStudio = !useVertexAi || selectedModel.Contains("gemma", StringComparison.OrdinalIgnoreCase);

    if (isAiStudio)
    {
      Console.WriteLine("  [INFO] Verbinde mit Google AI Studio API (Free-Tier API-Keys aktiv)...");
      return new Client(apiKey: apiKey, httpOptions: options);
    }
    else
    {
      Console.WriteLine("  [INFO] Verbinde mit Google Cloud Vertex AI (Projekt: en-linalg-biran-gemini)...");
      return new Client(
          vertexAI: true,
          project: "en-linalg-biran-gemini",
          location: "us-central1", // Geändert, da neueste Modelle oft nicht sofort in europe-west6 verfügbar sind
          httpOptions: options
      );
    }
  }
}