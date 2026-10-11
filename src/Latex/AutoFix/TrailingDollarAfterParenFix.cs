using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Fixes stray/trailing dollar sign immediately following a parenthesized inline formula,
/// e.g. '($i \notin \mathbb{Q}$)$' -> '($i \notin \mathbb{Q}$)'. Occurs when an LLM closes the inline math
/// before the parenthesis but reflexively types an additional dollar sign right after the closing parenthesis.
/// [Human] Entfernt ein überflüssiges $ direkt nach einer schließenden Klammer, z. B. ($x$)$ -> ($x$).
/// </summary>
public sealed partial class TrailingDollarAfterParenFix : ILatexLineFix {
    public string Description => "überflüssiges $ nach schließender Klammer entfernt";

    public string FixLine(string code) {
        if (!code.Contains(")$")) return code;
        return TrailingDollarRegex().Replace(code, "$1)");
    }

    [GeneratedRegex(@"(\([^\n$]*\$[^$\n]+\$\s*)\)\$")]
    private static partial Regex TrailingDollarRegex();
}
