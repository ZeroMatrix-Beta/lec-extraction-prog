namespace LectureExtraction.Configuration;

public class RefinementStepConfig {
    public bool Enabled { get; set; } = true;
    public bool AttachAudio { get; set; } = true;
    public int RateLimitDelaySeconds { get; set; } = 130;
    public string[] SystemInstructionPaths { get; set; } = [];
    public string[] HistoryPreloadPaths { get; set; } = [];

    public BackendParameters AiStudio { get; set; } = new BackendParameters { Model = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"] };
    public BackendParameters Vertex { get; set; } = new BackendParameters { Model = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"] };
}
