using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] DTO for Vertex AI specific configurations.
/// Requires valid GCP ProjectId and Location for IAM authentication.
/// </summary>
public class DirectAiChatSessionVertexConfig {
    // [AI Context] The Google Cloud Platform (GCP) Project ID associated with the billing account.
    public string ProjectId { get; set; } = AppConfig.VertexProjectId;
    // [AI Context] Region for Vertex AI execution. Must support the requested Gemini models.
    public string Location { get; set; } = AppConfig.VertexLocation;
    public string UploadFolder { get; set; } = AppConfig.UploadFolder;
    public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
    public string LogFolder { get; set; } = AppConfig.LogFolder;
    // [AI Context] Crucial: The designated Google Cloud Storage bucket used exclusively for Vertex AI multimodal attachments.
    public string GcsBucketName { get; set; } = AppConfig.VertexGcsBucketName;
    public string SystemInstructionPath { get; set; } = AppConfig.SystemInstructionPath;
    public string[] IncludePaths { get; set; } = [
    @"D:\lecture-videos\d-und-a/",
    @"D:\lecture-videos\d-und-a/new"
  ];
    public string[] Model { get; set; } = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"];
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
    public DirectAiChatSessionVertexAIConfig AI { get; set; } = new DirectAiChatSessionVertexAIConfig();
}