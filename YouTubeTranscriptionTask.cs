using System;
using System.Collections.Generic;

namespace Config;

/// <summary>
/// [AI Context] Represents a YouTube video transcription task configured in JSON.
/// Contains the YouTube video URL, output name, and specific timestamp fragments to extract.
/// [Human] Konfiguration für ein YouTube-Video mit spezifischen Zeitabschnitten (Fragments), die transkribiert werden sollen.
/// </summary>
public class YouTubeTranscriptionTask {
    public string VideoUrl { get; set; } = "";
    public string OutputName { get; set; } = "youtube-lecture";
    public List<YouTubeTimestampFragment> Fragments { get; set; } = [];
}

/// <summary>
/// [AI Context] Represents a specific time fragment within a YouTube video.
/// [Human] Zeitabschnitt (Start und Ende) eines YouTube-Videos.
/// </summary>
public class YouTubeTimestampFragment {
    public string StartTime { get; set; } = "00:00:00";
    public string EndTime { get; set; } = "00:30:00";
    public string PartTitle { get; set; } = "Part 1";
}
