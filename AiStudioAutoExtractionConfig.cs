using Config;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Configuration DTO for unattended batch processing using AI Studio endpoints.
/// Defines source/target directories and the critical extraction prompt.
/// [Human] Konfiguration für den automatisierten Extraktions-Modus mit dem kostenlosen AI Studio.
/// </summary>
public class AiStudioAutoExtractionConfig {
  // [AI Context] Selects the environment variable API key profile to use (1-3).
  // If 0, uses the dedicated API_KEY-automated-content-extraction.
  public int ActiveApiProfile { get; set; } = int.TryParse(System.Environment.GetEnvironmentVariable("ACTIVE_GEMINI_PROFILE", EnvironmentVariableTarget.User), out int val) ? val : 1;
  // [AI Context] Directory containing the raw, unprocessed lecture .mp4 files.
  public string SourceFolder { get; set; } = @"D:\lecture-videos\analysis2";
  // [AI Context] Directory where intermediate video chunks and final .tex files will be saved.
  public string TargetFolder { get; set; } = @"D:\lecture-videos\analysis2\destination2";
  // [AI Context] Absolute path to the overarching Director's Cut persona and instruction markdown.
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  // [AI Context] Centralized fallback paths for loading historical reference materials into the context window.
  public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
  public string LogFolder { get; set; } = @"D:\gemini-logs";
  // [AI Context] Default model selection for developer-tier batch processing.
  public string Model { get; set; } = "gemini-3-flash-preview";
  // [AI Context] The core prompt template dynamically appended to every video chunk.
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}