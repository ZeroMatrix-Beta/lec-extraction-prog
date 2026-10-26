using System;
using Microsoft.Extensions.Configuration;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Centralized global paths and feature flags loaded from appsettings.json. The paths are
/// the defaults the chat configs and the session logger start from; every other config sets its own.
/// [Human] Globale Pfade und Schalter aus appsettings.json.
/// </summary>
public static class AppConfig {
    private static readonly AppConfigOptions _options;

    static AppConfig() {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        _options = new AppConfigOptions();
        ConfigLayerBinder.Bind(configuration.GetSection("AppConfig"), _options);
    }

    // --- Basis-Verzeichnisse (Directories) ---
    public static string UploadFolder => _options.UploadFolder;
    public static string LogFolder => _options.LogFolder;
    public static string[] HistoryPreloadPaths => _options.HistoryPreloadPaths;
    public static string SystemInstructionPath => _options.SystemInstructionPath;

    // --- Feature-Schalter (Feature Flags) ---
    public static bool IsVertexAiEnabled => _options.IsVertexAiEnabled;

    /// <summary>How long a model request may stay silent before GenerationLoop sends it again (FirstChunkTimeoutMinutes).</summary>
    public static TimeSpan FirstChunkTimeout => TimeSpan.FromMinutes(Math.Max(0, _options.FirstChunkTimeoutMinutes));
}
