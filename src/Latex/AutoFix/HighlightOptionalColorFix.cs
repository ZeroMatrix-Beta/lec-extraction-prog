using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Fixes missing mandatory color argument for the \begin{highlight} environment.
/// In pdf-preamble.tex, highlight is defined as \NewTColorBox{highlight}{ m o } where the first
/// argument is the frame/shadow color and the second is the optional title. When an LLM outputs
/// \begin{highlight}[Title], LaTeX assigns [Title] to the color argument, causing
/// "Package xcolor Error: Undefined color '['". This fix inserts the default color {BrickRed}
/// so it becomes \begin{highlight}{BrickRed}[Title].
/// [Human] Fügt der \begin{highlight}-Umgebung die fehlende Standardfarbe {BrickRed} hinzu, wenn
/// fälschlicherweise direkt mit [Titel] begonnen wurde.
/// </summary>
public sealed partial class HighlightOptionalColorFix : ILatexLineFix {
    public string Description => "fehlendes Farbargument in \\begin{highlight} ergänzt";

    public string FixLine(string code) {
        if (!code.Contains(@"\begin{highlight}[", System.StringComparison.Ordinal)) return code;
        return HighlightStartRegex().Replace(code, @"\begin{highlight}{BrickRed}[");
    }

    [GeneratedRegex(@"\\begin\{highlight\}\[")]
    private static partial Regex HighlightStartRegex();
}
