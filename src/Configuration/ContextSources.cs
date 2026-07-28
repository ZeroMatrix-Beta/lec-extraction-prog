namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] System instructions and history preload path collections.
/// [Human] Pfadsammlungen für System-Instruktionen und Preload-Historien.
/// </summary>
public class ContextSources {
    public string[] SystemInstructionPaths { get; set; } = [];
    public string[] HistoryPreloadPaths { get; set; } = [];
}
