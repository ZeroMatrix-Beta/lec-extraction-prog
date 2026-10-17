using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Strips a tint from the color argument of \begin{highlight}. pdf-preamble.tex mixes its
/// own tints from that argument (`dynamic color shadow=#1!30`, `colframe=#1!50!ProofTint`), so a tinted
/// argument like {BrickRed!15} expands to `BrickRed!15!30`, which xcolor reads as a mix with a color
/// named `30`: "Package xcolor Error: Undefined color `30'". This fix keeps the bare name:
/// \begin{highlight}{BrickRed!15} becomes \begin{highlight}{BrickRed}.
/// [Human] Entfernt die Tönung (!15) aus der Farbe von \begin{highlight}, z. B. {BrickRed!15} -> {BrickRed}.
/// </summary>
public sealed partial class HighlightColorTintFix : ILatexLineFix {
    public string Description => "Tönung aus der Farbe von \\begin{highlight} entfernt";

    public string FixLine(string code) {
        if (!code.Contains(@"\begin{highlight}{", System.StringComparison.Ordinal)) return code;
        return TintedColorRegex().Replace(code, @"\begin{highlight}{$1}");
    }

    [GeneratedRegex(@"\\begin\{highlight\}\{([A-Za-z]+)(?:![0-9.]+(?:![A-Za-z]+)?)+\}")]
    private static partial Regex TintedColorRegex();
}
