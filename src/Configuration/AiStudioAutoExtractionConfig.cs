using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

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
    public string[] PredefinedSourceFolders { get; set; } = [];
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
    public float Temperature { get; set; } = AppConfig.DefaultTemperature;
    public float TopP { get; set; } = 0.8f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 65535; // Hardcoded for maximum output length
    public string[] Model { get; set; } = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"];
    // [AI Context] Zero-based index into Model[] indicating the currently chosen model. Persisted to JSON so the user's selection survives restarts.
    public int CurrentModelIndex { get; set; } = 0;
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string CurrentModel {
        get => Model.Length > 0 ? Model[Math.Clamp(CurrentModelIndex, 0, Model.Length - 1)] : "";
        set {
            int idx = Math.Clamp(CurrentModelIndex, 0, Model.Length > 0 ? Model.Length - 1 : 0);
            if (Model.Length == 0) Model = [value];
            else Model[idx] = value;
        }
    }
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;

    // [AI Context] If true, generates parallel '-offset.tex' files where timestamps in the extracted LaTeX 
    // are adjusted by the video chunk's start time to represent absolute global time in the lecture.
    // [Human] Wenn aktiviert, werden zusätzliche '.tex'-Dateien erstellt, bei denen die Zeitstempel im Text auf die tatsächliche Videolänge korrigiert sind.
    public bool GenerateOffsetFiles { get; set; } = true;

    // [AI Context] If true, loaded history files are added to SystemInstruction instead of History, skipping the explicit handshake.
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;

    // [AI Context] If true, image files in the history are embedded as inline blobs (InlineData) instead of being uploaded via the File API.
    // Inline blobs are part of the stable request prefix and enable Google's implicit prefix caching.
    // File API URIs are external references that prevent prefix caching.
    // [Human] Wenn aktiviert, werden Bilder in der History als Inline-Blob eingebettet (Prefix-Cache-freundlich).
    // Deaktivieren, wenn History-Bilder sehr groß sind und die Request-Payload zu groß wird.
    public bool InlineHistoryImages { get; set; } = true;

    // [AI Context] Controls how many multi-turn batches the history is split into when loading.
    // 0 or 1 = all history in a single turn (legacy behavior).
    // N > 1  = top-level subfolders are distributed evenly into N batches; each batch becomes its own
    //          AcknowledgeHistory turn in _sessionPreamble, reducing per-request token count.
    // [Human] Anzahl der Turns, in die die History aufgeteilt wird. 0/1 = alles in einem Turn.
    // Z.B. 4 = die Subfolders werden gleichmäßig auf 4 Turns verteilt.
    public int HistoryBatchCount { get; set; } = 0;

    // [AI Context] If true, previous .tex files are appended as read-only reference context to subsequent parts.
    public bool DebugSendReferenceFile { get; set; } = true;

    // [AI Context] If true, inlines previously generated lecture .tex parts as text before the video attachment.
    // Placing preceding .tex content before the video allows Google's implicit prefix caching to match across sequential parts (e.g. Part 3 reuses the prefix from Part 2).
    // [Human] Wenn aktiviert, werden vorherige .tex-Teile direkt vor dem Video im Prompt eingebettet. Dies ermöglicht schrittweises Prefix-Caching über aufeinanderfolgende Videoteile.
    public bool InlinePrecedingLecTexParts { get; set; } = true;

    // [AI Context] If true, sends a simple "Hello" debug roundtrip during initialization.
    // [Human] Wenn aktiviert, wird ein einfacher "Hello"-Roundtrip zum Debuggen gesendet.
    public bool DebugHelloRoundtrip { get; set; } = false;

    // [AI Context] If true, commands FFmpeg to extract an AAC of the entire lecture video before chunking.
    // [Human] Wenn aktiviert, wird vor der Verarbeitung eine komplette AAC-Audiospur der Vorlesung extrahiert.
    public bool GenerateAudioFile { get; set; } = true;

    // [AI Context] If true, the session will attempt to seamlessly refine the output into a single LaTeX document, provided other prerequisites are met.
    public bool GoIntoLatexRefinement { get; set; } = true;

    // [AI Context] If true, the chosen extraction model and AI parameters will be used in-memory for all subsequent LaTeX refinement steps.
    // [Human] Wenn aktiviert, werden das gewählte Modell und seine Parameter für alle weiteren Schritte der Pipeline (Refinement) im Arbeitsspeicher verwendet.
    public bool UseChosenModelForRestOfPipeline { get; set; } = true;

    // [AI Context] Number of overlapping parts to split the video into for processing to circumvent AI Studio context limits.
    // [Human] Anzahl der überlappenden Video-Teile, in die die Vorlesung geschnitten wird. Standard: 3.
    public int NumberOfParts { get; set; } = 3;

    // [AI Context] Overlap duration in seconds between adjacent video parts to ensure context is not lost during transitions.
    // [Human] Überlappung in Sekunden zwischen den geschnittenen Video-Teilen. Standard: 180 (3 Minuten).
    public int OverlapSeconds { get; set; } = 180;

    // [AI Context] If true, logs the complete system instruction dump to disk.
    // [Human] Wenn aktiviert, wird die gesamte zusammengesetzte System Instruction als Datei geloggt.
    public bool CreateLogFiles { get; set; } = true;

    // [AI Context] If true, uploads the next video part (and the audio file for refinement) in the background while Gemini is generating the current response.
    // [Human] Wenn aktiviert, wird der nächste Videoteil (und die Audiodatei fürs Refinement) im Hintergrund hochgeladen, während die KI den aktuellen Teil generiert.
    public bool EnableParallelFileUploads { get; set; } = true;

    // [AI Context] Optional framerate override for Google Gemini API video sampling.
    // [Human] Optionale Bildwiederholrate für die Gemini-API (z.B. 0.333 für 1 Frame alle 3 Sekunden).
    public double? GoogleVideoFps { get; set; }

    public bool UseGoogleSearch { get; set; } = false;
    public string FfmpegPreset { get; set; } = "fast";

    // [AI Context] Rate limit delay in seconds after processing video parts or chunk transitions to ensure token refill.
    // [Human] Wartezeit in Sekunden nach einzelnen Video-Teilen (Standard: 130s).
    public int VideoPartDelaySeconds { get; set; } = 130;


    // [AI Context] Dedicated rate limit delay in seconds after the base system instruction warmup (Step 0).
    // [Human] Wartezeit in Sekunden nach der Basis-System-Instruction (Step 0) (Standard: 65s).
    public int SystemInstructionDelaySeconds { get; set; } = 65;

    // [AI Context] Delay in seconds after uploading and activating a media file (like video) to prevent quota issues.
    // [Human] Wartezeit in Sekunden nach der Datei-Aktivierung bei AI Studio (Standard: 130s).
    public int FileActivationDelaySeconds { get; set; } = 130;

    // [AI Context] Maximum timeout in seconds for a single video upload attempt before timing out and triggering a retry.
    // [Human] Maximales Zeitlimit in Sekunden für einen einzelnen Upload-Versuch (Standard: 240s / 4 Min).
    public int VideoUploadTimeoutSeconds { get; set; } = 240;

    // [AI Context] Maximum retry attempts for uploading media files.
    // [Human] Maximale Anzahl an Wiederholungsversuchen beim Datei-Upload (Standard: 10).
    public int VideoUploadMaxRetries { get; set; } = 10;

    // [AI Context] If true, merges the base system instruction and the first history batch into a single warmup request.
    // [Human] Wenn true, wird die Basis-System-Instruction direkt mit dem ersten History-Batch vereint aufgewärmt (spart 1 Request).
    public bool MergeSystemInstructionAndFirstHistoryBatch { get; set; } = false;

    // [AI Context] If true, merges all consecutive history batches, handshaking only every second step to save API requests.
    // [Human] Wenn true, wird nur bei jedem zweiten History-Batch ein Handshake durchgeführt, um API-Aufrufe einzusparen.
    public bool MergeAllConsecutiveHistoryBatches { get; set; } = false;

    // [AI Context] If true, the dummy-part0.tex file is included in EVERY warm-up handshake round, not just the last one.
    // This ensures Google's implicit prefix cache sees a consistent user-turn structure (contextText + dummy + staticBeginning)
    // across all warm-up rounds, potentially improving cache association and avoiding quota errors on the final round.
    // [Human] Wenn true, wird dummy-part0.tex bei JEDEM Warm-up-Handshake mitgesendet (nicht nur beim letzten).
    public bool SendDummyFileWithEachWarmUpRound { get; set; } = false;

    // [AI Context] Dedicated rate limit delay in seconds between history loading/cache warming steps (e.g. 65s)
    // to keep Google's implicit GPU prefix cache warm without causing cache eviction or token refill starvation.
    // [Human] Spezielle Wartezeit in Sekunden während des History-Aufbaus (Standard: 65s).
    public int HistoryRateLimitDelaySeconds { get; set; } = 65;

    public YouTubeTranscriptionTask[] YouTubeTasks { get; set; } = [];
}