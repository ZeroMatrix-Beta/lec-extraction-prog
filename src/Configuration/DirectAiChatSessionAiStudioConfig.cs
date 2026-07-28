using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] DTO for Direct AI Chat Session (AI Studio) specific configurations.
/// Composes shared building blocks (ApiKeyProfile, WorkspacePaths, ContextSources, ModelSelection).
/// </summary>
public class DirectAiChatSessionAiStudioConfig {
    public ApiKeyProfile ApiKey { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new() {
        UploadFolder = AppConfig.UploadFolder,
        LogFolder = AppConfig.LogFolder
    };
    public ContextSources Sources { get; set; } = new() {
        HistoryPreloadPaths = AppConfig.HistoryPreloadPaths,
        SystemInstructionPaths = string.IsNullOrEmpty(AppConfig.SystemInstructionPath) ? [] : [AppConfig.SystemInstructionPath]
    };
    public ModelSelection ModelSelection { get; set; } = new() {
        Available = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview", "gemini-2.5-flash"]
    };

    public string GcsBucketName { get; set; } = "biran-linalg-source-material";
    public string[] IncludePaths { get; set; } = [
        @"D:\lecture-videos\d-und-a/",
        @"D:\lecture-videos\d-und-a/new"
    ];
    public bool UseGoogleSearch { get; set; } = false;
    public DirectAiChatSessionAiStudioGenerationConfig AI { get; set; } = new();

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ActiveApiProfile { get => ApiKey.ActiveProfile; set => ApiKey.ActiveProfile = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] AiStudioApiKeyEnvNames { get => ApiKey.EnvNames; set => ApiKey.EnvNames = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string UploadFolder { get => Paths.UploadFolder; set => Paths.UploadFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string LogFolder { get => Paths.LogFolder; set => Paths.LogFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] HistoryPreloadPaths { get => Sources.HistoryPreloadPaths; set => Sources.HistoryPreloadPaths = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string SystemInstructionPath { get => Sources.SystemInstructionPaths.Length > 0 ? Sources.SystemInstructionPaths[0] : ""; set => Sources.SystemInstructionPaths = string.IsNullOrEmpty(value) ? [] : [value]; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] Model { get => ModelSelection.Available; set => ModelSelection.Available = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int CurrentModelIndex { get => ModelSelection.CurrentIndex; set => ModelSelection.CurrentIndex = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string CurrentModel { get => ModelSelection.Current; set => ModelSelection.Current = value; }
}
