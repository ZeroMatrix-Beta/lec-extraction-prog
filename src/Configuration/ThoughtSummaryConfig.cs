namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Per-stage switch for Gemini's thought summaries and where they are written. Every
/// generating stage has its own copy: Stage 1 (AiStudio and Vertex extraction configs), refinement
/// steps 1-3 (<see cref="RefinementStepConfig"/>) and the PDF repair (<see cref="PdfCompilationConfig"/>).
///
/// <para><see cref="Enabled"/> sends <c>ThinkingConfig.IncludeThoughts = true</c>. Gemini then returns a
/// condensed summary of its reasoning as extra response parts marked <c>Thought</c>; the raw thinking
/// stays hidden. Billing is unchanged - thinking tokens are billed whether or not a summary is asked
/// for - and the summary never enters the .tex output or the continue history.</para>
///
/// <para>The three targets are independent; any combination works. With <see cref="Enabled"/> off,
/// none of them does anything and the request is exactly what it was before this setting existed.</para>
/// [Human] Gedanken-Zusammenfassungen (Thought Summaries) von Gemini pro Stufe einschalten und festlegen,
/// wohin sie geschrieben werden: Konsole, bestehendes Log, eigene Datei.
/// </summary>
public class ThoughtSummaryConfig {
    /// <summary>Request thought summaries for this stage. Off by default.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Print each summary to the console (dimmed) while it streams in.</summary>
    public bool ToConsole { get; set; } = true;

    /// <summary>
    /// [AI Context] Append each summary to the log this stage already writes: Stage 1's
    /// <c>chat_log.md</c> in the session log folder (needs <c>CreateLogFiles</c>), a refinement step's
    /// <c>&lt;output&gt;-prompt-log.md</c>, or the PDF repair round's <c>compile-log-step5-last_tryN.txt</c>.
    /// [Human] An das Log anhängen, das die Stufe ohnehin schreibt.
    /// </summary>
    public bool ToExistingLog { get; set; } = false;

    /// <summary>
    /// [AI Context] Write <c>&lt;output stem&gt;-thoughts.md</c> beside the stage's output file - the file
    /// <c>lecx thoughts list/show</c> reads. A second run never overwrites it: it gets <c>-thoughts-2.md</c>.
    /// [Human] Eigene Datei neben der Ausgabe; die liest <c>lecx thoughts</c>.
    /// </summary>
    public bool ToSeparateFile { get; set; } = true;
}
