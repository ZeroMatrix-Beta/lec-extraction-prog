using Config;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Configuration for the enterprise Vertex AI tier.
/// Binds to a specific GCP Project and Region, requiring an active billing account and a dedicated GCS bucket for multimodal payloads.
/// [Human] Konfiguration für den professionellen Google Cloud Modus. Erfordert ein eingerichtetes Rechnungskonto und Cloud Storage.
/// </summary>
public class VertexAutoExtractionConfig {
  // [AI Context] The Google Cloud Platform (GCP) Project ID associated with the billing account.
  public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
  // [AI Context] Region for Vertex AI execution. Must support the requested Gemini models.
  public string Location { get; set; } = "global";
  // [AI Context] Crucial: The designated Google Cloud Storage bucket used exclusively for Vertex AI multimodal attachments.
  public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
  public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
  public string LogFolder { get; set; } = AppConfig.LogFolder;
  public string Model { get; set; } = "gemini-3-flash-preview";
  public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
  public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
  public double SpeedMultiplier { get; set; } = 1.2;
}