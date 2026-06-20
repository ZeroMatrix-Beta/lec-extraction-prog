using System;

namespace Config;

public class RefinementStepConfig {
    public bool Enabled { get; set; } = true;
    public float Temperature { get; set; } = 0.0f;
    public float TopP { get; set; } = 0.8f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 65535;
    public string Model { get; set; } = "gemini-3.5-flash";
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;
    public string[] SystemInstructionPaths { get; set; } = Array.Empty<string>();
    public string[] HistoryPreloadPaths { get; set; } = Array.Empty<string>();
}

public class LatexRefinementSessionConfig {
    public bool Enabled { get; set; } = false;
    public string ApiKeyEnvName { get; set; } = "API_KEY-latex-refinement";
    public string TargetFolder { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    
    public RefinementStepConfig Step1MergeAndTimestamp { get; set; } = new RefinementStepConfig {
        SystemInstructionPaths = new[] { @"C:\Users\miche\latex\prompt-engineering\merge-instructions\latex-part-merge-instruction.md" }
    };

    public RefinementStepConfig Step2SpeechRefinement { get; set; } = new RefinementStepConfig {
        SystemInstructionPaths = new[] { @"C:\Users\miche\latex\prompt-engineering\speech-refinement\speech-refinement.md" }
    };

    public RefinementStepConfig Step3LastRefinement { get; set; } = new RefinementStepConfig {
        SystemInstructionPaths = new[] { @"C:\Users\miche\latex\prompt-engineering\last-refinement\last-refinement.md" }
    };
}
