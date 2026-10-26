namespace LectureExtraction.Configuration;

// [AI Context] The DTO that directly maps to the structure of the appsettings.json file. Paths default
// to empty: they belong to one machine, so appsettings.json sets them.
public class AppConfigOptions {
    public string UploadFolder { get; set; } = "";
    public string LogFolder { get; set; } = "";
    public string[] HistoryPreloadPaths { get; set; } = [];
    public string SystemInstructionPath { get; set; } = "";

    // [AI Context] Master kill switch for Google Cloud Vertex AI to prevent accidental billing/costs.
    public bool IsVertexAiEnabled { get; set; } = false;

    // [AI Context] Minutes a model request may deliver no data at all before it is abandoned and sent
    // again (GenerationLoop). 0 leaves only the 20-minute HTTP timeout.
    public int FirstChunkTimeoutMinutes { get; set; } = 10;
}
