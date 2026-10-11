using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Fixes an unclosed '\begin{env' or '\end{env' statement at the end of a line or before comments/whitespace,
/// e.g. '\end{proof' -> '\end{proof}', '\end{itemize' -> '\end{itemize}'.
/// Seen in 10-22-2025 where a line ended with '\end{itemize' and in 10-27-2025 with '\end{proof'.
/// [Human] Ergänzt eine fehlende schließende geschweifte Klammer bei \begin{...} oder \end{...} am Zeilenende.
/// </summary>
public sealed partial class UnclosedEnvironmentEndFix : ILatexLineFix {
    public string Description => "fehlende geschweifte Klammer bei \\begin{...} oder \\end{...} ergänzt";

    public string FixLine(string code) {
        if (!code.Contains(@"\end{") && !code.Contains(@"\begin{")) return code;
        return UnclosedTagRegex().Replace(code, @"\$1{$2}");
    }

    [GeneratedRegex(@"\\(begin|end)\{([a-zA-Z][a-zA-Z0-9*]*)(?=\s*$|%)")]
    private static partial Regex UnclosedTagRegex();
}
