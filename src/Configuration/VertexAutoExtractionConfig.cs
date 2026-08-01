using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Configuration for the enterprise Vertex AI tier.
/// Composes shared building blocks (VertexEndpoint, WorkspacePaths, ContextSources, GenerationParameters, ModelSelection, ContextCacheSettings).
/// [Human] Konfiguration für den professionellen Google Cloud Modus. Erfordert ein eingerichtetes Rechnungskonto und Cloud Storage.
/// </summary>
public class VertexAutoExtractionConfig : IAutoExtractionConfig {
    public VertexEndpoint Endpoint { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new() {
        SourceFolder = @"D:\lecture-videos\d-und-a\new",
        TargetFolder = @"D:\lecture-videos\d-und-a\extracted",
        LogFolder = @"D:\gemini-logs"
    };
    public ContextSources Sources { get; set; } = new() {
        HistoryPreloadPaths = AppConfig.HistoryPreloadPaths
    };
    public GenerationParameters Generation { get; set; } = new() {
        Temperature = 0.35f,
        TopP = 0.95f,
        TopK = 40,
        MaxOutputTokens = 65535
    };
    public ModelSelection ModelSelection { get; set; } = new() {
        Available = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"]
    };
    public ContextCacheSettings ContextCaching { get; set; } = new() {
        Enabled = true,
        Minutes = 45,
        IncrementMinutes = 45,
        MinimumRemainingMinutes = 15
    };

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string ProjectId { get => Endpoint.ProjectId; set => Endpoint.ProjectId = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string Location { get => Endpoint.Location; set => Endpoint.Location = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string GcsBucketName { get => Endpoint.GcsBucketName; set => Endpoint.GcsBucketName = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string SourceFolder { get => Paths.SourceFolder; set => Paths.SourceFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] PredefinedSourceFolders { get => Paths.PredefinedSourceFolders; set => Paths.PredefinedSourceFolders = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string TargetFolder { get => Paths.TargetFolder; set => Paths.TargetFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string LogFolder { get => Paths.LogFolder; set => Paths.LogFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] SystemInstructionPaths { get => Sources.SystemInstructionPaths; set => Sources.SystemInstructionPaths = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] HistoryPreloadPaths { get => Sources.HistoryPreloadPaths; set => Sources.HistoryPreloadPaths = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float Temperature { get => Generation.Temperature; set => Generation.Temperature = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float TopP { get => Generation.TopP; set => Generation.TopP = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int TopK { get => Generation.TopK; set => Generation.TopK = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int MaxOutputTokens { get => Generation.MaxOutputTokens; set => Generation.MaxOutputTokens = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int? ThinkingBudget { get => Generation.ThinkingBudget; set => Generation.ThinkingBudget = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string? ThinkingLevel { get => Generation.ThinkingLevel; set => Generation.ThinkingLevel = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] Model { get => ModelSelection.Available; set => ModelSelection.Available = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int CurrentModelIndex { get => ModelSelection.CurrentIndex; set => ModelSelection.CurrentIndex = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string CurrentModel { get => ModelSelection.Current; set => ModelSelection.Current = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public bool UseContextCaching { get => ContextCaching.Enabled; set => ContextCaching.Enabled = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingMinutes { get => ContextCaching.Minutes; set => ContextCaching.Minutes = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingIncrementMinutes { get => ContextCaching.IncrementMinutes; set => ContextCaching.IncrementMinutes = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingMinimumRemainingMinutes { get => ContextCaching.MinimumRemainingMinutes; set => ContextCaching.MinimumRemainingMinutes = value; }

    public double SpeedMultiplier { get; set; } = 1.2;
    public bool GenerateOffsetFiles { get; set; } = true;
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;
    public bool DebugSendReferenceFile { get; set; } = true;
    public bool InlinePrecedingLecTexParts { get; set; } = true;
    public bool VerboseConsoleOutput { get; set; } = false;
    public bool GenerateAudioFile { get; set; } = true;
    public bool GoIntoLatexRefinement { get; set; } = true;
    public bool UseChosenModelForRestOfPipeline { get; set; } = true;
    public int NumberOfParts { get; set; } = 3;
    public int OverlapSeconds { get; set; } = 180;
    public bool CreateLogFiles { get; set; } = true;
    public bool EnableParallelFileUploads { get; set; } = true;
    public double? GoogleVideoFps { get; set; }
    public bool UseGoogleSearch { get; set; } = false;
    public string FfmpegPreset { get; set; } = "fast";
    public int RateLimitDelaySeconds { get; set; } = 130;
    public bool EnableImplicitPrefixCacheWarmup { get; set; } = false;

    /// <summary>
    /// [AI Context] Vertex counterpart of the AI Studio flag: sends <c>ThinkingBudget = 0</c> on the
    /// cache-warming handshake, which has one fixed sentence to echo and nothing to reason about.
    /// Opt-in for the same reason - it is a live paid request and some models reject the field.
    /// [Human] Schaltet das "Nachdenken" beim Cache-Warming-Handshake ab (standardmässig aus).
    /// </summary>
    public bool DisableThinkingDuringWarmUp { get; set; } = false;

    /// <summary>
    /// [AI Context] Vertex counterpart of the AI Studio flag: stops session after history loading,
    /// system instruction setup, and prefix cache warming without extracting video files.
    /// [Human] Wenn true, führt die Session nur das Laden der System Instructions & den Cache-Warmup durch und beendet sich dann ohne Videoextraktion (für Debugzwecke).
    /// </summary>
    public bool OnlyDoWarmUp { get; set; } = false;
    public YouTubeTranscriptionTask[] YouTubeTasks { get; set; } = [];
}