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
    public float DefaultTemperature { get; set; } = 0.35f;
    public float DefaultTopP { get; set; } = 0.90f;
    public int DefaultTopK { get; set; } = 40;
    public int DefaultMaxOutputTokens { get; set; } = 65535;
    public int? DefaultThinkingBudget { get; set; } = 24576;
    public string? DefaultThinkingLevel { get; set; } = "HIGH";

    // [AI Context] Master kill switch for Google Cloud Vertex AI to prevent accidental billing/costs.
    // Was Program.Activate_Vertex (a hardcoded static field requiring a recompile to change) until
    // Phase 6 moved it here so it's editable via appsettings.json instead.
    // [Human] Wenn auf false gesetzt, sind sämtliche Vertex AI Funktionen in der App (Chat, Extraktion,
    // Refinement) strikt deaktiviert.
    public bool IsVertexAiEnabled { get; set; } = false;
}
