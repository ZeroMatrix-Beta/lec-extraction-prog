using System;
using System.Text.Json.Serialization;
using Config;

namespace DirectChatAiInteraction.AiStudio;

/// <summary>
/// [AI Context] Localized generation parameters for the Direct AI Chat Session (AI Studio).
/// Dictates the deterministic vs. creative output distribution of the LLM.
/// </summary>
public class DirectAiChatSessionAiStudioGenerationConfig {
    // [AI Context] Temperature (0.0 - 2.0). 0.0 = purely deterministic (best for strict code/math/transcripts). 1.0+ = highly creative (risk of hallucinations).
    public float Temperature { get; set; } = AppConfig.DefaultTemperature;
    // [AI Context] TopP (Nucleus Sampling). 0.0 - 1.0. Lower values restrict vocabulary to the most probable tokens, cutting off the "long tail" of creative/random words.
    public float TopP { get; set; } = 0.9f;
    // [AI Context] TopK. Limits the vocabulary to the top K most likely next tokens. TopK=1 is greedy decoding (perfect for LaTeX generation).
    public int TopK { get; set; } = 10;
    // [AI Context] Hard cutoff limit for output generation. Does NOT affect verbosity, only truncates if exceeded. Set to maximum (65535) for large LaTeX scripts.
    public int MaxOutputTokens { get; set; } = 65535;
    // [AI Context] "Thinking" params introduced for the latest Gemini 2.5 and 3.x models. Strictly required for the 2.5 series.
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    // [AI Context] Controls the internal reasoning time for the Gemini 3.x series (e.g., MINIMAL, LOW, MEDIUM, HIGH).
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;

    // [AI Context] If true, system instructions are cached on Google Cloud servers.
    // [Human] Wenn aktiviert, werden System Instructions im Cache gespeichert.
    public bool UseContextCaching { get; set; } = false;
    public int ContextCachingMinutes { get; set; } = 15;
    public int ContextCachingIncrementMinutes { get; set; } = 30;
    public bool UseGoogleSearch { get; set; } = false;
}

/// <summary>
/// [AI Context] DTO for Direct AI Chat Session (AI Studio) specific configurations.
/// Separated from VertexAI to prevent accidental contamination of free-tier and enterprise logic.
/// </summary>
public class DirectAiChatSessionAiStudioConfig {
    // [AI Context] Selects the environment variable API key profile to use (1-3).
    public int ActiveApiProfile { get; set; } = int.TryParse(System.Environment.GetEnvironmentVariable("ACTIVE_GEMINI_PROFILE", EnvironmentVariableTarget.User), out int val) ? val : 1;
    public string[] AiStudioApiKeyEnvNames { get; set; } = [
        "API_KEY-automated-content-extraction",
        "API_KEY-ai-studio-test-project-1",
        "API_KEY-ai-studio-test-project-2",
        "API_KEY-ai-studio-test-project-3"
    ];
    public string UploadFolder { get; set; } = AppConfig.UploadFolder;
    public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
    public string LogFolder { get; set; } = AppConfig.LogFolder;
    public string GcsBucketName { get; set; } = "biran-linalg-source-material";
    public string SystemInstructionPath { get; set; } = AppConfig.SystemInstructionPath;
    public string[] IncludePaths { get; set; } = [
        @"D:\lecture-videos\d-und-a/",
        @"D:\lecture-videos\d-und-a/new"
    ];
    public string[] Model { get; set; } = ["gemini-3.5-flash", "gemini-3-flash-preview"];
    // [AI Context] Zero-based index into Model[] indicating the currently chosen model. Persisted to JSON so the user's selection survives restarts.
    public int CurrentModelIndex { get; set; } = 0;
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string CurrentModel {
        get => Model.Length > 0 ? Model[Math.Clamp(CurrentModelIndex, 0, Model.Length - 1)] : "";
        set {
            int idx = Math.Clamp(CurrentModelIndex, 0, Model.Length > 0 ? Model.Length - 1 : 0);
            if (Model.Length == 0) Model = [value];
            else Model[idx] = value;
        }
    }
    public bool UseGoogleSearch { get; set; } = false;
    public DirectAiChatSessionAiStudioGenerationConfig AI { get; set; } = new DirectAiChatSessionAiStudioGenerationConfig();
}