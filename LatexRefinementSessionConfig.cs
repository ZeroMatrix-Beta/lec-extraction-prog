using System;

namespace Config;

public class BackendParameters {
    public float Temperature { get; set; } = 0.0f;
    public float TopP { get; set; } = 1.0f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 65535;
    public string Model { get; set; } = "gemini-3.5-flash";
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;
}

public class RefinementStepConfig {
    public bool Enabled { get; set; } = true;
    public string[] SystemInstructionPaths { get; set; } = [];
    public string[] HistoryPreloadPaths { get; set; } = [];

    public BackendParameters AiStudio { get; set; } = new BackendParameters { Model = "gemini-3.5-flash" };
    public BackendParameters Vertex { get; set; } = new BackendParameters { Model = "gemini-2.5-pro" };
}

public class LatexRefinementSessionConfig {
    public bool Enabled { get; set; } = false;
    public bool UseVertex { get; set; } = false;

    // AI Studio Config
    public string AiStudioApiKeyEnvName { get; set; } = "API_KEY-latex-refinement";
    public int AiStudioActiveApiProfile { get; set; } = 3; // 0 = Dedicated, 1-3 = General Keys

    // Vertex Config
    public string VertexProjectId { get; set; } = "vertex-ai-experiments-494320";
    public string VertexLocation { get; set; } = "global";
    public string VertexGcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";

    public string TargetFolder { get; set; } = "";
    public string SourceFolder { get; set; } = "";

    public RefinementStepConfig Step1MergeAndTimestamp { get; set; } = new RefinementStepConfig {
        SystemInstructionPaths = [@"C:\Users\miche\latex\prompt-engineering\merge-instructions\latex-part-merge-instruction.md"]
    };

    public RefinementStepConfig Step2SpeechRefinement { get; set; } = new RefinementStepConfig {
        SystemInstructionPaths = [@"C:\Users\miche\latex\prompt-engineering\speech-refinement\speech-refinement.md"]
    };

    public RefinementStepConfig Step3LastRefinement { get; set; } = new RefinementStepConfig {
        SystemInstructionPaths = [@"C:\Users\miche\latex\prompt-engineering\last-refinement\last-refinement.md"]
    };
}
