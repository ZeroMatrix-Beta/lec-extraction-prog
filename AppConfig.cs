using System;
using System.IO;
using Microsoft.Extensions.Configuration;

using DirectChatAiInteraction.AiStudio;

namespace Config;

/// <summary>
/// Generic configuration loader implementing the hierarchy:
/// corresponding .json > appconfig.json > C# static variable > C# app static
/// </summary>
public static class ConfigLoader<T> where T : class, new() {
  public static T Load(string? sectionName = null) {
    sectionName ??= typeof(T).Name;
    var basePath = AppDomain.CurrentDomain.BaseDirectory;

    // Build a single configuration object with a clear hierarchy.
    // The last source added wins for keys at the same path.
    var configuration = new ConfigurationBuilder()
        .SetBasePath(basePath)
        .AddJsonFile("appsettings.json", optional: true) // 2. Base settings from global file.
        .AddJsonFile($"{typeof(T).Name}.json", optional: true) // 3. Specific file overrides global.
        .Build();

    // 1. Start with a new instance, which will have the C# default values.
    var config = new T();

    // Bind from the "AppConfig:TypeName" section of the combined configuration.
    // This handles values defined within the AppConfig block in appsettings.json.
    configuration.GetSection("AppConfig").GetSection(sectionName).Bind(config);

    // Bind from the root of the combined configuration.
    // This allows the specific {TypeName}.json to have settings at the root level,
    // overriding any values that were previously bound.
    configuration.Bind(config);

    return config;
  }
}


// [AI Context] The DTO that directly maps to the structure of the appsettings.json file.
// We provide default fallback values here just in case the JSON file is missing or malformed.
public class AppConfigOptions {
  public string BaseLectureFolder { get; set; } = @"D:\lecture-videos";
  public string UploadFolder { get; set; } = @"D:\gemini-upload-folder";
  public string LogFolder { get; set; } = @"D:\gemini-logs";
  public string[] HistoryPreloadPaths { get; set; } = new[] {
    @"C:\Users\miche\latex\directors-cut-analysis2\gemini-chat-history",
    @"D:\ETH HS 2025\Analysis I HS 2025\Analysis_I_Skript_I_25-12-22.pdf"
  };
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string VertexProjectId { get; set; } = "vertex-ai-experiments-494320";
  public string VertexLocation { get; set; } = "global";
  public string VertexGcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
  public string DefaultModel { get; set; } = "gemini-3-flash-preview";
  public string RefinementModel { get; set; } = "gemini-2.5-pro";
  public float DefaultTemperature { get; set; } = 0.1f;
  public float DefaultTopP { get; set; } = 0.9f;
  public int DefaultTopK { get; set; } = 10;
  public int DefaultMaxOutputTokens { get; set; } = 65535;
  public int? DefaultThinkingBudget { get; set; } = 4096;
  public string? DefaultThinkingLevel { get; set; } = "HIGH";
  public string AutoExtractionPrompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}

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
  public static string DefaultModel => _options.DefaultModel;
  public static string RefinementModel => _options.RefinementModel;
  public static float DefaultTemperature => _options.DefaultTemperature;
  public static float DefaultTopP => _options.DefaultTopP;
  public static int DefaultTopK => _options.DefaultTopK;
  public static int DefaultMaxOutputTokens => _options.DefaultMaxOutputTokens;
  public static int? DefaultThinkingBudget => _options.DefaultThinkingBudget;
  public static string? DefaultThinkingLevel => _options.DefaultThinkingLevel;
  public static string AutoExtractionPrompt => _options.AutoExtractionPrompt;
}