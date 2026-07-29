using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Configuration DTO for unattended batch processing using AI Studio endpoints.
/// Composes shared building blocks (ApiKeyProfile, WorkspacePaths, ContextSources, GenerationParameters, ModelSelection).
/// [Human] Konfiguration für den automatisierten Extraktions-Modus mit dem kostenlosen AI Studio.
/// </summary>
public class AiStudioAutoExtractionConfig : IAutoExtractionConfig {
    public ApiKeyProfile ApiKey { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new() {
        SourceFolder = @"D:\lecture-videos\grundstrukturen",
        LogFolder = @"D:\gemini-logs"
    };
    public ContextSources Sources { get; set; } = new() {
        HistoryPreloadPaths = [@"C:\Users\miche\latex\prompt-engineering\transcription\training-history"]
    };
    public GenerationParameters Generation { get; set; } = new() {
        Temperature = 0.36f,
        TopP = 0.8f,
        TopK = 10,
        MaxOutputTokens = 65535
    };
    public ModelSelection ModelSelection { get; set; } = new() {
        Available = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"]
    };

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ActiveApiProfile { get => ApiKey.ActiveProfile; set => ApiKey.ActiveProfile = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] AiStudioApiKeyEnvNames { get => ApiKey.EnvNames; set => ApiKey.EnvNames = value; }
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

    public double SpeedMultiplier { get; set; } = 1.0;
    public bool GenerateOffsetFiles { get; set; } = true;
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;
    public bool InlineHistoryImages { get; set; } = true;
    public int HistoryBatchCount { get; set; } = 0;
    public bool DebugSendReferenceFile { get; set; } = true;
    public bool InlinePrecedingLecTexParts { get; set; } = true;
    public bool DebugHelloRoundtrip { get; set; } = false;
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
    public int VideoPartDelaySeconds { get; set; } = 130;
    public int SystemInstructionDelaySeconds { get; set; } = 65;
    public int FileActivationDelaySeconds { get; set; } = 130;
    public int VideoUploadTimeoutSeconds { get; set; } = 240;
    public int VideoUploadMaxRetries { get; set; } = 10;
    public bool MergeSystemInstructionAndFirstHistoryBatch { get; set; } = false;
    public bool MergeAllConsecutiveHistoryBatches { get; set; } = false;
    public bool SendDummyFileWithEachWarmUpRound { get; set; } = false;
    public int HistoryRateLimitDelaySeconds { get; set; } = 65;
    public bool EnableImplicitPrefixCacheWarmup { get; set; } = true;

    /// <summary>
    /// [AI Context] Sends <c>ThinkingBudget = 0</c> on the cache-warming handshake. The handshake asks
    /// the model to echo one fixed sentence, so it has nothing to reason about - but unlike the real
    /// generation path it sets no ThinkingConfig at all today, meaning it runs at the model's default
    /// thinking behaviour and can bill reasoning tokens for a request that needs none (finding F9).
    ///
    /// <para>Defaults to <c>false</c>: this is a live, paid request, and "Thinking level is not
    /// supported" is a real error mode for some models (ApiRetryPolicy filters for that message), so
    /// the change is opt-in until one run's reported Denk-Tokens show whether it is worth making.
    /// The flag is ignored for models that do not support thinking at all.</para>
    /// [Human] Schaltet das "Nachdenken" beim Cache-Warming-Handshake ab. Standardmässig aus, bis
    /// eine echte Messung zeigt, dass es sich lohnt.
    /// </summary>
    public bool DisableThinkingDuringWarmUp { get; set; } = false;
    public YouTubeTranscriptionTask[] YouTubeTasks { get; set; } = [];
}