using Config;

namespace DirectChatAiInteraction.AiStudio;

/// <summary>
/// [AI Context] Localized generation parameters for the Direct AI Chat Session (AI Studio).
/// Dictates the deterministic vs. creative output distribution of the LLM.
/// </summary>
public class DirectAiChatSessionAiStudioGenerationConfig {
  // [AI Context] Temperature (0.0 - 2.0). 0.0 = purely deterministic (best for strict code/math/transcripts). 1.0+ = highly creative (risk of hallucinations).
  public float Temperature { get; set; } = 0.1f;
  // [AI Context] TopP (Nucleus Sampling). 0.0 - 1.0. Lower values restrict vocabulary to the most probable tokens, cutting off the "long tail" of creative/random words.
  public float TopP { get; set; } = 0.9f;
  // [AI Context] TopK. Limits the vocabulary to the top K most likely next tokens. TopK=1 is greedy decoding (perfect for LaTeX generation).
  public int TopK { get; set; } = 10;
  // [AI Context] Hard cutoff limit for output generation. Does NOT affect verbosity, only truncates if exceeded. Set to maximum (65535) for large LaTeX scripts.
  public int MaxOutputTokens { get; set; } = 65535;
  // [AI Context] "Thinking" params introduced for the latest Gemini 2.5 and 3.x models. Strictly required for the 2.5 series.
  public int? ThinkingBudget { get; set; } = 4096;
  // [AI Context] Controls the internal reasoning time for the Gemini 3.x series (e.g., MINIMAL, LOW, MEDIUM, HIGH).
  public string? ThinkingLevel { get; set; } = "HIGH";
}

/// <summary>
/// [AI Context] DTO for Direct AI Chat Session (AI Studio) specific configurations.
/// Separated from VertexAI to prevent accidental contamination of free-tier and enterprise logic.
/// </summary>
public class DirectAiChatSessionAiStudioConfig {
  // [AI Context] Selects the environment variable API key profile to use (1-3).
  public int ActiveApiProfile { get; set; } = int.TryParse(System.Environment.GetEnvironmentVariable("ACTIVE_GEMINI_PROFILE", EnvironmentVariableTarget.User), out int val) ? val : 1;
  public string UploadFolder { get; set; } = AppConfig.UploadFolder;
  public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
  public string LogFolder { get; set; } = AppConfig.LogFolder;
  public string GcsBucketName { get; set; } = "biran-linalg-source-material";
  public string SystemInstructionPath { get; set; } = AppConfig.SystemInstructionPath;
  public string[] IncludePaths { get; set; } = new[] {
        @"D:\lecture-videos\d-und-a/",
        @"D:\lecture-videos\d-und-a/new"
    };
  public DirectAiChatSessionAiStudioGenerationConfig AI { get; set; } = new DirectAiChatSessionAiStudioGenerationConfig();
}