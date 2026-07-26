using System.Collections.Generic;

namespace LectureExtraction.Configuration;

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
