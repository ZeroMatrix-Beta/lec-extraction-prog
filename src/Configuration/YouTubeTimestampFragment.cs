namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Represents a specific time fragment within a YouTube video.
/// [Human] Zeitabschnitt (Start und Ende) eines YouTube-Videos.
/// </summary>
public class YouTubeTimestampFragment {
    public string StartTime { get; set; } = "00:00:00";
    public string EndTime { get; set; } = "00:30:00";
    public string PartTitle { get; set; } = "Part 1";
}
