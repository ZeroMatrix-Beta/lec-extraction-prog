using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Restores the backslash of a command that a rewriting stage replaced by `<`:
/// `    <coordinate (dcross_L1) at (7.5, -1.5);` becomes `    \coordinate (dcross_L1) at (7.5, -1.5);`.
/// pdflatex reports "Missing $ inserted" there. Only a `<` at the start of the line (after indentation)
/// directly followed by one of the listed command names is touched: such a line never compiles, while a
/// `<` anywhere else (a comparison in math, a TikZ arrow spec `<->`) is left alone.
/// [Human] Ersetzt ein `<` am Zeilenanfang vor einem Befehl wieder durch `\`, z. B. <coordinate -> \coordinate.
/// </summary>
public sealed partial class LeadingAngleBracketCommandFix : ILatexLineFix {
    public string Description => "`<` am Zeilenanfang durch `\\` ersetzt";

    public string FixLine(string code) {
        if (!code.Contains('<')) return code;
        return LeadingAngleBracketRegex().Replace(code, @"$1\");
    }

    [GeneratedRegex(@"^(\s*)<(?=(?:coordinate|draw|node|path|fill|filldraw|shade|clip|foreach|begin|end|item)\b)")]
    private static partial Regex LeadingAngleBracketRegex();
}
