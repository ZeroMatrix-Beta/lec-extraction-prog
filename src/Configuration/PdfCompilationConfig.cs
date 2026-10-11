using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

public class PdfCompilationConfig {
    public bool Enabled { get; set; } = true;
    public string PreamblePath { get; set; } = "pdf-preamble.tex";
    public bool UseAntiGravityAgent { get; set; } = false;

    /// <summary>
    /// [AI Context] HIGHLY EXPERIMENTAL. After a failed compile, run LatexAutoFixer (our own rule-based
    /// repairs) before any AI repair, and recompile if it changed something. Off unless a config turns it
    /// on: it rewrites the .tex in place (the original is kept as -orig.tex) and its rules are young.
    /// The read-only LatexDiagnostics findings for the AI repair prompt run regardless of this switch.
    /// [Human] HOCHGRADIG EXPERIMENTELL: eigene LaTeX-Auto-Korrekturen vor der KI-Reparatur.
    /// </summary>
    public bool ExperimentalLatexAutoFix { get; set; } = false;
    public int MaxFixRounds { get; set; } = 3;
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public int MaxAntiGravityAgentRounds {
        get => MaxFixRounds;
        set => MaxFixRounds = value;
    }
}
