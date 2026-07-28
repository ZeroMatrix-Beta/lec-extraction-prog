namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Configuration for Google Cloud explicit context caching (TTL, increment, minimum remaining).
/// [Human] Einstellungen für Google Cloud Context Caching (Dauer, Verlängerung, Mindest-Restzeit).
/// </summary>
public class ContextCacheSettings {
    public bool Enabled { get; set; } = false;
    public int Minutes { get; set; } = 15;
    public int IncrementMinutes { get; set; } = 30;
    public int MinimumRemainingMinutes { get; set; } = 10;
}
