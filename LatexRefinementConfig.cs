using Config;

namespace Config;

/// <summary>
/// [AI Context] Configuration specifically for the post-processing phase. TargetFolder specifies where the compiled, polished .tex/.pdf will be dropped.
/// </summary>
public class LatexRefinementConfig {
  public string GeminiMdPath { get; set; } = AppConfig.SystemInstructionPath;
  public string Model { get; set; } = AppConfig.RefinementModel;
  public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
  public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;
  public string TargetFolder { get; set; } = AppConfig.LatexRefinementTargetFolder;
  public string SourceFolder { get; set; } = AppConfig.LatexRefinementSourceFolder;
}