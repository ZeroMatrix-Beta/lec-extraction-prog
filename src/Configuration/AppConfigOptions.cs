namespace LectureExtraction.Configuration;

// [AI Context] The DTO that directly maps to the structure of the appsettings.json file.
public class AppConfigOptions {
    public string BaseLectureFolder { get; set; } = @"D:\lecture-videos";
    public string UploadFolder { get; set; } = @"D:\gemini-upload-folder";
    public string LogFolder { get; set; } = @"D:\gemini-logs";
    public string[] HistoryPreloadPaths { get; set; } = [];
    public string SystemInstructionPath { get; set; } = @"";
    public string VertexProjectId { get; set; } = "vertex-ai-experiments-494320";
    public string VertexLocation { get; set; } = "global";
    public string VertexGcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";

    // [AI Context] Master kill switch for Google Cloud Vertex AI to prevent accidental billing/costs.
    public bool IsVertexAiEnabled { get; set; } = false;
}
