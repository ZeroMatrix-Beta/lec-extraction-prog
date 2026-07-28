namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Directory configuration for video source, target, log, and upload locations.
/// [Human] Verzeichniskonfiguration für Quell-, Ziel-, Log- und Upload-Ordner.
/// </summary>
public class WorkspacePaths {
    public string SourceFolder { get; set; } = "";
    public string[] PredefinedSourceFolders { get; set; } = [];
    public string TargetFolder { get; set; } = "";
    public string LogFolder { get; set; } = "";
    public string UploadFolder { get; set; } = "";
}
