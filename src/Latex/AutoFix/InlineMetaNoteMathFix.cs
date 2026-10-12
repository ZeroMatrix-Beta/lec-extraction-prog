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
        if (!code.Contains(@"\inlinemetanote{", StringComparison.Ordinal)) return code;

        int idx = 0;
        while ((idx = code.IndexOf(@"\inlinemetanote{", idx, StringComparison.Ordinal)) >= 0) {
            int openBrace = idx + @"\inlinemetanote".Length;
            int closeBrace = LatexScan.FindMatchingBrace(code, openBrace);
            if (closeBrace < 0) {
                idx++;
                continue;
            }

            string inner = code[(openBrace + 1)..closeBrace];
            string fixedInner = FixInnerNote(inner);

            if (fixedInner != inner) {
                code = string.Concat(code.AsSpan(0, openBrace + 1), fixedInner, code.AsSpan(closeBrace));
                closeBrace = openBrace + 1 + fixedInner.Length;
            }

            idx = closeBrace + 1;
        }

        return code;
    }

    private static string FixInnerNote(string inner) {
        // 1) English/prose text accidentally inside math mode:
        //    e.g. 'writes $\bar{f} is surjective onto the image of $f$'
        //    ->   'writes $\bar{f}$ is surjective onto the image of $f$'
        inner = InnerMetaNoteMathRegex().Replace(inner, m =>
            $"{m.Groups[1].Value}${m.Groups[2].Value}$ {m.Groups[3].Value}${m.Groups[4].Value}${m.Groups[5].Value}");

        // 2) Unclosed math inside the note:
        //    e.g. 'schreibt \qt{Fall 1: } $A \text{ endl.}'
        //    ->   'schreibt \qt{Fall 1: } $A \text{ endl.}$'
        int dollarCount = LatexScan.CountUnescaped(inner, '$');
        if (dollarCount % 2 != 0) {
            inner += "$";
        }

        return inner;
    }

    [GeneratedRegex(@"^([^$]*?)\$([a-zA-Z0-9\\_^{}]+)\s+([a-zA-Z]{2,}\b[^{}$]*?)\$([a-zA-Z0-9\\_^{}]+)\$([^$]*)$")]
    private static partial Regex InnerMetaNoteMathRegex();
}
