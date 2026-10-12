using System;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Fixes syntax and math delimiter errors inside and around \inlinemetanote:
/// 1) Mismatched math delimiters where English text was accidentally placed inside math mode, e.g.
///    '\inlinemetanote{writes $\bar{f} is surjective onto the image of $f$}'
///    -> '\inlinemetanote{writes $\bar{f}$ is surjective onto the image of $f$}'.
/// 2) Unclosed inline math mode before the note ends, e.g.
///    '\inlinemetanote{schreibt \qt{Fall 1: } $A \text{ endl.}}'
///    -> '\inlinemetanote{schreibt \qt{Fall 1: } $A \text{ endl.}$}'.
/// 3) Unclosed math environments inside the note (e.g. \begin{cases} without \end{cases}).
/// 4) Extraneous trailing dollar sign immediately following the note, on a line with an odd number of '$':
///    '\inlinemetanote{schreibt $F x$}$' -> '\inlinemetanote{schreibt $F x$}'.
/// A surplus '}' after the note needs the whole document: see <see cref="SurplusBraceAfterMetaNoteFix"/>.
/// [Human] Korrigiert versehentlichen Mathe-Modus über Fließtext, ungeschlossene Umgebungen/$-Delimiters in \inlinemetanote sowie ein überflüssiges $ direkt nach der Notiz.
/// </summary>
public sealed partial class InlineMetaNoteMathFix : ILatexLineFix {
    public string Description => "Syntaxfehler in/um \\inlinemetanote korrigiert";

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

            // Trailing dollar right after \inlinemetanote{...}$: only on a line with an odd number of '$',
            // so a line that compiled (say a note inside math, '$x = \inlinemetanote{...}$') stays as it is
            if (closeBrace + 1 < code.Length && code[closeBrace + 1] == '$'
                && LatexScan.CountUnescaped(fixedInner, '$') % 2 == 0
                && LatexScan.CountUnescaped(code, '$') % 2 == 1) {
                code = string.Concat(code.AsSpan(0, closeBrace + 1), code.AsSpan(closeBrace + 2));
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

        // 2) Unclosed environments inside the note (e.g. \begin{cases} without \end{cases}):
        string[] mathEnvs = ["cases", "aligned", "matrix", "pmatrix", "bmatrix"];
        foreach (string env in mathEnvs) {
            string beginTag = @"\begin{" + env + "}";
            string endTag = @"\end{" + env + "}";
            if (inner.Contains(beginTag, StringComparison.Ordinal) && !inner.Contains(endTag, StringComparison.Ordinal)) {
                if (inner.EndsWith('$')) {
                    inner = string.Concat(inner.AsSpan(0, inner.Length - 1), " ", endTag, "$");
                } else {
                    inner = string.Concat(inner, " ", endTag);
                }
            }
        }

        // 3) Unclosed math inside the note:
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
