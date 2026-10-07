using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Centralized global paths and feature flags loaded from appsettings.json.
/// </summary>
public static class AppConfig {
    private static readonly AppConfigOptions _options;

    static AppConfig() {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        _options = new AppConfigOptions();
        configuration.GetSection("AppConfig").Bind(_options);
    }

    // --- Basis-Verzeichnisse (Directories) ---
    public static string BaseLectureFolder => _options.BaseLectureFolder;
    public static string UploadFolder => _options.UploadFolder;
    public static string LogFolder => _options.LogFolder;
    public static string[] HistoryPreloadPaths => [@"C:\Users\miche\latex\prompt-engineering\transcription\training-history"];

    // --- Dynamisch zusammengesetzte Pfade ---
    public static string AutoExtractionSourceFolder => Path.Combine(BaseLectureFolder, "analysis2");
    public static string AutoExtractionTargetFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination2");

    public static string VertexAutoExtractionSourceFolder => Path.Combine(BaseLectureFolder, @"d-und-a\new");
    public static string VertexAutoExtractionTargetFolder => Path.Combine(BaseLectureFolder, @"d-und-a\extracted");

    public static string LatexRefinementSourceFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement");
    public static string LatexRefinementTargetFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement\refined");

    public static string FfmpegSourceFolder => Path.Combine(BaseLectureFolder, "d-und-a");
    public static string FfmpegTargetFolder => Path.Combine(BaseLectureFolder, @"d-und-a\new");

    public static string SystemInstructionPath => @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";

    // --- Cloud & API ---
    public static string VertexProjectId => _options.VertexProjectId;
    public static string VertexLocation => _options.VertexLocation;
    public static string VertexGcsBucketName => _options.VertexGcsBucketName;

    // --- Feature-Schalter (Feature Flags) ---
    public static bool IsVertexAiEnabled => _options.IsVertexAiEnabled;
}
