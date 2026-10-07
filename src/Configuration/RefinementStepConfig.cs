namespace LectureExtraction.Configuration;

public class RefinementStepConfig {
    public bool Enabled { get; set; } = true;
    public bool AttachAudio { get; set; } = true;
    public int RateLimitDelaySeconds { get; set; } = 130;
    /// <summary>Upper bound on requests for this step, the first one included (continues re-send the whole history).</summary>
    public int MaxContinueRequests { get; set; } = 10;
    public string[] SystemInstructionPaths { get; set; } = [];
    public string[] HistoryPreloadPaths { get; set; } = [];

    public BackendParameters AiStudio { get; set; } = new() { ModelSelection = new() { Available = ["gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"] } };
    public BackendParameters Vertex { get; set; } = new() { ModelSelection = new() { Available = ["gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"] } };
}
