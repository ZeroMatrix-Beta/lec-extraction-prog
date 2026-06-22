using Config;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Configuration DTO for unattended batch processing using AI Studio endpoints.
/// Defines source/target directories and the critical extraction prompt.
/// [Human] Konfiguration für den automatisierten Extraktions-Modus mit dem kostenlosen AI Studio.
/// </summary>
public class AiStudioAutoExtractionConfig : IAutoExtractionConfig {
    // [AI Context] Selects the environment variable API key profile to use (1-3).
    // If 0, uses the dedicated API_KEY-automated-content-extraction.
    // [Human] Stanardmäßig wird hier Profil 0 (der dedizierte Key für die automatisierte Extraktion) verwendet.
    // Dies kann bei Bedarf in der AiStudioAutoExtractionConfig.json überschrieben werden.
    public int ActiveApiProfile { get; set; } = 0;
    public string[] AiStudioApiKeyEnvNames { get; set; } = [
        "API_KEY-automated-content-extraction",
        "API_KEY-ai-studio-test-project-1",
        "API_KEY-ai-studio-test-project-2",
        "API_KEY-ai-studio-test-project-3"
    ];
    // [AI Context] Directory containing the raw, unprocessed lecture .mp4 files.
    public string SourceFolder { get; set; } = @"D:\lecture-videos\grundstrukturen";
    // [AI Context] Directory where intermediate video chunks and final .tex files will be saved.
    public string TargetFolder { get; set; } = @"";
    // [AI Context] Absolute paths to the overarching Director's Cut persona and instruction markdown files.
    public string[] SystemInstructionPaths { get; set; } = [];
    /* Default fallback:
    new[] {
      @"C:\Users\miche\latex\prompt-engineering\transcription\transcription.md",
      @"C:\Users\miche\latex\prompt-engineering\transcription\hard-specs.md",
      @"C:\Users\miche\latex\prompt-engineering\transcription\environments.md",
      @"C:\Users\miche\latex\prompt-engineering\transcription\big-examples.md",
      @"C:\Users\miche\latex\prompt-engineering\transcription\big-examples2.md",
      @"C:\Users\miche\latex\prompt-engineering\transcription\big-examples3.md",
    };
    */
    // [AI Context] Centralized fallback paths for loading historical reference materials into the context window.
    public string[] HistoryPreloadPaths { get; set; } = [
    @"C:\Users\miche\latex\prompt-engineering\transcription\training-history"
    //@"C:\Users\miche\latex\prompt-engineering\transcription\table-of-content.md"
  ];
    public string LogFolder { get; set; } = @"D:\gemini-logs";
    // [AI Context] Default model selection for developer-tier batch processing.
    public float Temperature { get; set; } = 0.35f; // 1.0f is default.5  
    public float TopP { get; set; } = 0.8f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 65535; // Hardcoded for maximum output length
    public string Model { get; set; } = "gemini-3.5-flash";
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;

    // [AI Context] If true, generates parallel '-offset.tex' files where timestamps in the extracted LaTeX 
    // are adjusted by the video chunk's start time to represent absolute global time in the lecture.
    // [Human] Wenn aktiviert, werden zusätzliche '.tex'-Dateien erstellt, bei denen die Zeitstempel im Text auf die tatsächliche Videolänge korrigiert sind.
    public bool GenerateOffsetFiles { get; set; } = true;

    // [AI Context] If true, loaded history files are added to SystemInstruction instead of History, skipping the explicit handshake.
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;

    // [AI Context] If true, commands FFmpeg to extract an AAC of the entire lecture video before chunking.
    // [Human] Wenn aktiviert, wird vor der Verarbeitung eine komplette AAC-Audiospur der Vorlesung extrahiert.
    public bool GenerateAudioFile { get; set; } = true;

    // [AI Context] If true, the session will attempt to seamlessly refine the output into a single LaTeX document, provided other prerequisites are met.
    public bool GoIntoLatexRefinement { get; set; } = true;

    // [AI Context] Number of overlapping parts to split the video into for processing to circumvent AI Studio context limits.
    // [Human] Anzahl der überlappenden Video-Teile, in die die Vorlesung geschnitten wird. Standard: 3.
    public int NumberOfParts { get; set; } = 3;

    // [AI Context] Overlap duration in seconds between adjacent video parts to ensure context is not lost during transitions.
    // [Human] Überlappung in Sekunden zwischen den geschnittenen Video-Teilen. Standard: 180 (3 Minuten).
    public int OverlapSeconds { get; set; } = 180;
}