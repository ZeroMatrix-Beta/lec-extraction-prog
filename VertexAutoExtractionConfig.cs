using System;
using Config;

namespace AutoExtraction;

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
    public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";

    public string[] SystemInstructionPaths { get; set; } = [];
    public string[] HistoryPreloadPaths { get; set; } = AppConfig.HistoryPreloadPaths;
    public string LogFolder { get; set; } = AppConfig.LogFolder;

    public string Model { get; set; } = "gemini-3.1-pro-preview";
    public float Temperature { get; set; } = 0.5f; // Similar to AiStudioAutoExtractionConfig
    public float TopP { get; set; } = AppConfig.DefaultTopP;
    public int TopK { get; set; } = AppConfig.DefaultTopK;
    public int MaxOutputTokens { get; set; } = 65535; // Similar to AiStudioAutoExtractionConfig
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;

    public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
    public double SpeedMultiplier { get; set; } = 1.2;

    // [AI Context] If true, generates parallel '-offset.tex' files where timestamps in the extracted LaTeX 
    // are adjusted by the video chunk's start time to represent absolute global time in the lecture.
    // [Human] Wenn aktiviert, werden zusätzliche '.tex'-Dateien erstellt, bei denen die Zeitstempel im Text auf die tatsächliche Videolänge korrigiert sind.
    public bool GenerateOffsetFiles { get; set; } = true;

    // [AI Context] If true, loaded history files are added to SystemInstruction instead of History, skipping the explicit handshake.
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;

    // [AI Context] If true, commands FFmpeg to extract an AAC of the entire lecture video before chunking.
    // [Human] Wenn aktiviert, wird vor der Verarbeitung eine komplette AAC-Audiospur der Vorlesung extrahiert.
    public bool GenerateAudioFile { get; set; } = true; // Set to true to match AiStudio
    
    // [AI Context] If true, the session will attempt to seamlessly refine the output into a single LaTeX document, provided other prerequisites are met.
    public bool GoIntoLatexRefinement { get; set; } = true;

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
}