using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] DTO for Vertex AI specific configurations.
/// Composes shared building blocks (VertexEndpoint, WorkspacePaths, ContextSources, ModelSelection).
/// </summary>
public class DirectAiChatSessionVertexConfig {
    public VertexEndpoint Endpoint { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new() {
        UploadFolder = AppConfig.UploadFolder,
        LogFolder = AppConfig.LogFolder
    };
    public ContextSources Sources { get; set; } = new() {
        HistoryPreloadPaths = AppConfig.HistoryPreloadPaths,
        SystemInstructionPaths = string.IsNullOrEmpty(AppConfig.SystemInstructionPath) ? [] : [AppConfig.SystemInstructionPath]
    };
    public ModelSelection ModelSelection { get; set; } = new() {
        Available = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"]
    };

    public string[] IncludePaths { get; set; } = [
        @"D:\lecture-videos\d-und-a/",
        @"D:\lecture-videos\d-und-a/new"
    ];
    public bool UseGoogleSearch { get; set; } = false;

    /// <summary>Prints full exception objects instead of just their message. Matches the flag of the same name on the extraction configs.</summary>
    public bool VerboseConsoleOutput { get; set; } = false;

    public DirectAiChatSessionVertexAIConfig AI { get; set; } = new();

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string ProjectId { get => Endpoint.ProjectId; set => Endpoint.ProjectId = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string Location { get => Endpoint.Location; set => Endpoint.Location = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string GcsBucketName { get => Endpoint.GcsBucketName; set => Endpoint.GcsBucketName = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string UploadFolder { get => Paths.UploadFolder; set => Paths.UploadFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string LogFolder { get => Paths.LogFolder; set => Paths.LogFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] HistoryPreloadPaths { get => Sources.HistoryPreloadPaths; set => Sources.HistoryPreloadPaths = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string SystemInstructionPath { get => Sources.SystemInstructionPaths.Length > 0 ? Sources.SystemInstructionPaths[0] : ""; set => Sources.SystemInstructionPaths = string.IsNullOrEmpty(value) ? [] : [value]; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] Model { get => ModelSelection.Available; set => ModelSelection.Available = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int CurrentModelIndex { get => ModelSelection.CurrentIndex; set => ModelSelection.CurrentIndex = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string CurrentModel { get => ModelSelection.Current; set => ModelSelection.Current = value; }
}