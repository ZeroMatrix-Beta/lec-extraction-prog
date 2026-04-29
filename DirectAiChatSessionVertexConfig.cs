using Config;
using DirectChatAiInteraction.Vertex;

namespace DirectChatAiInteraction.Vertex;

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
  public string[] IncludePaths { get; set; } = new[] {
    @"D:\lecture-videos\d-und-a/",
    @"D:\lecture-videos\d-und-a/new"
  };
  public DirectAiChatSessionVertexAIConfig AI { get; set; } = new DirectAiChatSessionVertexAIConfig();
}