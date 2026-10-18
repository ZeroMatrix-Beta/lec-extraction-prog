using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

public class PdfCompilationConfig {
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// [AI Context] Fallback preamble: used when the lecture's language is unknown, or when the
    /// language's own preamble below is empty or missing on disk.
    /// [Human] Standard-Preamble, wenn die Sprache unbekannt ist.
    /// </summary>
    public string PreamblePath { get; set; } = "pdf-preamble.tex";

    /// <summary>
    /// [AI Context] Preamble for German lectures (`% Primary Language: German`), generated from the
    /// current Grundstrukturen book (grundstrukturen-ai-transcription-spring-2025) by tools/build-language-preambles.py.
    /// [Human] Preamble für deutsche Vorlesungen.
    /// </summary>
    public string PreamblePathGerman { get; set; } = "pdf-preamble-de.tex";

    /// <summary>
    /// [AI Context] Preamble for English lectures, generated from the Algebra 1 book preamble by
    /// tools/build-language-preambles.py.
    /// [Human] Preamble für englische Vorlesungen.
    /// </summary>
    public string PreamblePathEnglish { get; set; } = "pdf-preamble-en.tex";

    /// <summary>
    /// [AI Context] The preamble for a lecture in <paramref name="language"/>: the language's own file if
    /// it is set and exists, otherwise <see cref="PreamblePath"/>.
    /// [Human] Wählt die Preamble passend zur Sprache, sonst die Standard-Preamble.
    /// </summary>
    public string ResolvePreamblePath(LectureExtraction.Latex.LectureLanguage language) {
        string? own = language switch {
            LectureExtraction.Latex.LectureLanguage.German => PreamblePathGerman,
            LectureExtraction.Latex.LectureLanguage.English => PreamblePathEnglish,
            _ => null,
        };
        return !string.IsNullOrWhiteSpace(own) && System.IO.File.Exists(own) ? own : PreamblePath;
    }

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

    /// <summary>
    /// [AI Context] Thought summaries for the AI repair rounds. The repair runs on step 3's model and
    /// generation settings, but this switch is its own, so the repair can be watched without step 3.
    /// [Human] Gedanken-Zusammenfassungen der KI-Reparatur (eigener Schalter).
    /// </summary>
    public ThoughtSummaryConfig ThoughtSummaries { get; set; } = new();
}
