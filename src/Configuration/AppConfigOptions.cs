namespace LectureExtraction.Configuration;

// [AI Context] The DTO that directly maps to the structure of the appsettings.json file.
// We provide default fallback values here just in case the JSON file is missing or malformed.
public class AppConfigOptions {
    public string BaseLectureFolder { get; set; } = @"D:\lecture-videos";
    public string UploadFolder { get; set; } = @"D:\gemini-upload-folder";
    public string LogFolder { get; set; } = @"D:\gemini-logs";
    public string[] HistoryPreloadPaths { get; set; } = [];
    public string SystemInstructionPath { get; set; } = @"";
    public string VertexProjectId { get; set; } = "vertex-ai-experiments-494320";
    public string VertexLocation { get; set; } = "global";
    public string VertexGcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
    public string DefaultModel { get; set; } = "gemini-3.5-flash"; // This is for other sessions
    public string RefinementModel { get; set; } = "gemini-3.5-flash"; // This is for LatexRefinement
    public float DefaultTemperature { get; set; } = 0.35f;
    public float DefaultTopP { get; set; } = 0.90f;
    public int DefaultTopK { get; set; } = 40;
    public int DefaultMaxOutputTokens { get; set; } = 65535;
    public int? DefaultThinkingBudget { get; set; } = 24576;
    public string? DefaultThinkingLevel { get; set; } = "HIGH";
}
