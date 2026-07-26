using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

public class PdfCompilationConfig {
    public bool Enabled { get; set; } = true;
    public string PreamblePath { get; set; } = "pdf-preamble.tex";
    public bool UseAntiGravityAgent { get; set; } = false;
    public int MaxFixRounds { get; set; } = 3;
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public int MaxAntiGravityAgentRounds {
        get => MaxFixRounds;
        set => MaxFixRounds = value;
    }
}
