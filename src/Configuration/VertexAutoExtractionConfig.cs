using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Configuration for the enterprise Vertex AI tier.
/// Binds to a specific GCP Project and Region, requiring an active billing account and a dedicated GCS bucket for multimodal payloads.
/// [Human] Konfiguration für den professionellen Google Cloud Modus. Erfordert ein eingerichtetes Rechnungskonto und Cloud Storage.
/// </summary>
public class VertexAutoExtractionConfig : IAutoExtractionConfig {
    // [AI Context] The Google Cloud Platform (GCP) Project ID associated with the billing account.
    public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
    // [AI Context] Region for Vertex AI execution. Must support the requested Gemini models.
    public string Location { get; set; } = "global";
    // [AI Context] Crucial: The designated Google Cloud Storage bucket used exclusively for Vertex AI multimodal attachments.
    public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";

    public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
    public string[] PredefinedSourceFolders { get; set; } = [];
    public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";

    public string[] SystemInstructionPaths { get; set; } = [];
    public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
    public string LogFolder { get; set; } = AppConfig.LogFolder;

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
    public float Temperature { get; set; } = AppConfig.DefaultTemperature;
    public float TopP { get; set; } = AppConfig.DefaultTopP;
    public int TopK { get; set; } = AppConfig.DefaultTopK;
    public int MaxOutputTokens { get; set; } = 65535; // Similar to AiStudioAutoExtractionConfig
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;
    public double SpeedMultiplier { get; set; } = 1.2;

    // [AI Context] If true, generates parallel '-offset.tex' files where timestamps in the extracted LaTeX 
    // are adjusted by the video chunk's start time to represent absolute global time in the lecture.
    // [Human] Wenn aktiviert, werden zusätzliche '.tex'-Dateien erstellt, bei denen die Zeitstempel im Text auf die tatsächliche Videolänge korrigiert sind.
    public bool GenerateOffsetFiles { get; set; } = true;

    // [AI Context] If true, loaded history files are added to SystemInstruction instead of History, skipping the explicit handshake.
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;

    // [AI Context] If true, previous .tex files are appended as read-only reference context to subsequent parts.
    public bool DebugSendReferenceFile { get; set; } = true;

    // [AI Context] If true, inlines previously generated lecture .tex parts as text before the video attachment.
    // Placing preceding .tex content before the video allows Google's implicit prefix caching to match across sequential parts (e.g. Part 3 reuses the prefix from Part 2).
    // [Human] Wenn aktiviert, werden vorherige .tex-Teile direkt vor dem Video im Prompt eingebettet. Dies ermöglicht schrittweises Prefix-Caching über aufeinanderfolgende Videoteile.
    public bool InlinePrecedingLecTexParts { get; set; } = true;

    // [AI Context] If true, commands FFmpeg to extract an AAC of the entire lecture video before chunking.
    // [Human] Wenn aktiviert, wird vor der Verarbeitung eine komplette AAC-Audiospur der Vorlesung extrahiert.
    public bool GenerateAudioFile { get; set; } = true; // Set to true to match AiStudio
    
    // [AI Context] If true, the session will attempt to seamlessly refine the output into a single LaTeX document, provided other prerequisites are met.
    public bool GoIntoLatexRefinement { get; set; } = true;

    // [AI Context] If true, the chosen extraction model and AI parameters will be used in-memory for all subsequent LaTeX refinement steps.
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

    // [AI Context] If true, system instructions (>100k tokens) are cached on Google Cloud servers to reduce latency and token costs.
    // [Human] Wenn aktiviert, werden System Instructions bei Google im Cache gespeichert (spart Tokens & Geld).
    public bool UseContextCaching { get; set; } = true;

    // [AI Context] Default caching duration in minutes on Google servers.
    // [Human] Standard-Gültigkeitsdauer des Kontext-Caches in Minuten (z.B. 15).
    public int ContextCachingMinutes { get; set; } = 15;

    // [AI Context] Standard increment in minutes when prolonging the context cache via GUI.
    // [Human] Verlängerungsintervall in Minuten beim Verlängern über das GUI (z.B. 30).
    public int ContextCachingIncrementMinutes { get; set; } = 30;

    // [AI Context] Minimum remaining TTL in minutes before the automatic pre-part cache extension is triggered.
    // If the cache has fewer minutes remaining than this value before a video part is sent, the cache is automatically extended by ContextCachingIncrementMinutes.
    // [Human] Schwellenwert in Minuten: Wenn der Cache kürzer als dieser Wert gültig ist, wird er vor dem nächsten Videoteil automatisch verlängert.
    public int ContextCachingMinimumRemainingMinutes { get; set; } = 10;

    // [AI Context] If true, uploads the next video part (and the audio file for refinement) in the background while Gemini is generating the current response.
    // [Human] Wenn aktiviert, wird der nächste Videoteil (und die Audiodatei fürs Refinement) im Hintergrund hochgeladen, während die KI den aktuellen Teil generiert.
    public bool EnableParallelFileUploads { get; set; } = true;

    // [AI Context] Optional framerate override for Google Gemini API video sampling.
    // [Human] Optionale Bildwiederholrate für die Gemini-API (z.B. 0.333 für 1 Frame alle 3 Sekunden).
    public double? GoogleVideoFps { get; set; }

    public bool UseGoogleSearch { get; set; } = false;
    public string FfmpegPreset { get; set; } = "fast";
    public int RateLimitDelaySeconds { get; set; } = 130;
    public YouTubeTranscriptionTask[] YouTubeTasks { get; set; } = [];
}