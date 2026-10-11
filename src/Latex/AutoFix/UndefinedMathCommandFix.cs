using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Replaces math commands that pdf-preamble.tex does not define with ones it does:
/// '\mapsfrom' -> '\mathrel{\reflectbox{$\mapsto$}}' and '\longmapsfrom' likewise ("Undefined control
/// sequence" in step 4 of the 2026-10-07 algebra lecture: 'q^{-1}(J) \mapsfrom J'), and '\sub' -> '\subset'
/// (written by AI repair round 1 of the same lecture: 'K \sub I'). The preamble loads neither stmaryrd nor
/// a \sub; \reflectbox comes with graphicx, which tikz loads, and the AI repair's own
/// '\mathrel{\reflectbox{$\mapsto$}}' compiled. Longer names (\subset, \subseteq, \subsection, \mapsfromchar)
/// and an escaped backslash ('\\sub...' is a line break plus text) stay untouched.
/// [Human] Ersetzt Befehle, die die Präambel nicht kennt (\mapsfrom, \longmapsfrom, \sub), durch bekannte.
/// </summary>
public sealed partial class UndefinedMathCommandFix : ILatexLineFix {
    public string Description => @"unbekannten Befehl (\mapsfrom, \sub) ersetzt";

    public string FixLine(string code) {
        if (!code.Contains(@"\sub") && !code.Contains("mapsfrom")) return code;
        return CommandRegex().Replace(code, m => {
            if (LatexScan.IsEscaped(code, m.Index)) return m.Value;
            return m.Groups[1].Value switch {
                "mapsfrom" => @"\mathrel{\reflectbox{$\mapsto$}}",
                "longmapsfrom" => @"\mathrel{\reflectbox{$\longmapsto$}}",
                _ => @"\subset",
            };
        });
    }

    [GeneratedRegex(@"\\(mapsfrom|longmapsfrom|sub)(?![A-Za-z@])")]
    private static partial Regex CommandRegex();
}
