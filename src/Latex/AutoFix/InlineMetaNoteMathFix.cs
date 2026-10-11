using System;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Fixes math delimiter errors inside \inlinemetanote:
/// 1) Mismatched math delimiters where English text was accidentally placed inside math mode, e.g.
///    '\inlinemetanote{writes $\bar{f} is surjective onto the image of $f$}'
///    -> '\inlinemetanote{writes $\bar{f}$ is surjective onto the image of $f$}'.
/// 2) Unclosed inline math mode before the note ends, e.g.
///    '\inlinemetanote{schreibt \qt{Fall 1: } $A \text{ endl.}}'
///    -> '\inlinemetanote{schreibt \qt{Fall 1: } $A \text{ endl.}$}'.
/// [Human] Korrigiert versehentlichen Mathe-Modus über Fließtext sowie ungeschlossene $-Delimiters in \inlinemetanote.
/// </summary>
public sealed partial class InlineMetaNoteMathFix : ILatexLineFix {
    public string Description => "Math-Modus in \\inlinemetanote korrigiert";

    public string FixLine(string code) {
        if (!code.Contains(@"\inlinemetanote{")) return code;

        string fixedCode = InlineMetaNoteMathRegex().Replace(code, m =>
            $@"\inlinemetanote{{{m.Groups[1].Value}${m.Groups[2].Value}$ {m.Groups[3].Value}${m.Groups[4].Value}${m.Groups[5].Value}}}");

        fixedCode = FixUnclosedMathInInlineMetaNote(fixedCode);

        return fixedCode;
    }

    private static string FixUnclosedMathInInlineMetaNote(string line) {
        int idx = 0;
        while ((idx = line.IndexOf(@"\inlinemetanote{", idx, StringComparison.Ordinal)) >= 0) {
            int openBrace = idx + @"\inlinemetanote".Length;
            int closeBrace = LatexScan.FindMatchingBrace(line, openBrace);
            if (closeBrace < 0) {
                idx++;
                continue;
            }

            string inner = line[(openBrace + 1)..closeBrace];
            int dollarCount = 0;
            for (int i = 0; i < inner.Length; i++) {
                if (inner[i] == '$' && !LatexScan.IsEscaped(inner, i)) {
                    dollarCount++;
                }
            }

            if (dollarCount % 2 != 0) {
                line = line[..closeBrace] + "$" + line[closeBrace..];
                idx = closeBrace + 2;
            } else {
                idx = closeBrace + 1;
            }
        }
        return line;
    }

    [GeneratedRegex(@"\\inlinemetanote\{([^}]*?)\$([a-zA-Z0-9\\_^{}]+)\s+([a-zA-Z]{2,}\b[^{}$]*?)\$([a-zA-Z0-9\\_^{}]+)\$([^}]*)\}")]
    private static partial Regex InlineMetaNoteMathRegex();
}
