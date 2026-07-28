using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Centralized 'Single Point of Truth' for all hardcoded paths and default parameters.
/// Uses the Microsoft.Extensions.Configuration binder to dynamically load values from appsettings.json.
/// </summary>
public static class AppConfig {
    private static readonly AppConfigOptions _options;

    static AppConfig() {
        // [AI Context] Automatically looks for appsettings.json in the compiled output directory.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        _options = new AppConfigOptions();
        configuration.GetSection("AppConfig").Bind(_options);
    }

    // --- Basis-Verzeichnisse (Directories) ---
    public static string BaseLectureFolder => _options.BaseLectureFolder;
    public static string UploadFolder => _options.UploadFolder;
    public static string LogFolder => _options.LogFolder;
    public static string[] HistoryPreloadPaths => _options.HistoryPreloadPaths;

    // --- Dynamisch zusammengesetzte Pfade ---
    public static string AutoExtractionSourceFolder => Path.Combine(BaseLectureFolder, "analysis2");
    public static string AutoExtractionTargetFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination2");

    public static string VertexAutoExtractionSourceFolder => Path.Combine(BaseLectureFolder, @"d-und-a\new");
    public static string VertexAutoExtractionTargetFolder => Path.Combine(BaseLectureFolder, @"d-und-a\extracted");

    public static string LatexRefinementSourceFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement");
    public static string LatexRefinementTargetFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement\refined");

    public static string FfmpegSourceFolder => Path.Combine(BaseLectureFolder, "d-und-a");
    public static string FfmpegTargetFolder => Path.Combine(BaseLectureFolder, @"d-und-a\new");

    // --- Dateien (Files) ---
    public static string SystemInstructionPath => _options.SystemInstructionPath;

    // --- Cloud & API ---
    public static string VertexProjectId => _options.VertexProjectId;
    public static string VertexLocation => _options.VertexLocation;
    public static string VertexGcsBucketName => _options.VertexGcsBucketName;

    // --- Standard KI-Parameter ---
    public static float DefaultTemperature => _options.DefaultTemperature;
    public static float DefaultTopP => _options.DefaultTopP;
    public static int DefaultTopK => _options.DefaultTopK;
    public static int DefaultMaxOutputTokens => _options.DefaultMaxOutputTokens;
    public static int? DefaultThinkingBudget => _options.DefaultThinkingBudget;
    public static string? DefaultThinkingLevel => _options.DefaultThinkingLevel;

    // --- Feature-Schalter (Feature Flags) ---
    public static bool IsVertexAiEnabled => _options.IsVertexAiEnabled;
}
