namespace LectureExtraction.Media;

/// <summary>
/// [AI Context] One FFmpeg-produced chunk of a source lecture video: its path on disk and the
/// timestamp (in seconds, relative to the original, unsplit video) at which it starts. Replaces
/// the anonymous `(string FilePath, double StartTime)` tuple that `FfmpegToolkit.ProcessSplitVideoAsync`
/// and both extraction sessions previously passed around. Lives in `Media`, not `Extraction.Model`,
/// because `FfmpegToolkit` (the type that produces it) must not depend on the extraction pipeline.
/// [Human] Ein einzelnes, von FFmpeg erzeugtes Segment eines Vorlesungsvideos: Dateipfad plus
/// Startzeitpunkt (in Sekunden, relativ zum ungeschnittenen Originalvideo).
/// </summary>
public sealed record VideoSegment(string FilePath, double StartTimeSeconds);
