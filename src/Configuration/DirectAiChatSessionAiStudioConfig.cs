using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

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
    public string[] Model { get; set; } = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview", "gemini-2.5-flash"];
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
