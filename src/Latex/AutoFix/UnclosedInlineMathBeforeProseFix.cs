using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Closes an inline formula whose closing '$' was dropped in front of ordinary prose:
/// 'Once you have $x and negatives, ...' -> 'Once you have $x$ and negatives, ...' and
/// 'for any ideal $I \in \operatorname{Id}_K(R), the forward image:' -> '... $I \in \operatorname{Id}_K(R)$,
/// the forward image:'. Both were written by AI repair round 1 of the 2026-10-07 algebra lecture
/// ("Missing $ inserted"); the first one also shifts every later '$' on its line.
///
/// <para>Only a line with an odd number of unescaped '$' is touched, since it cannot balance as written, and
/// only when one of its formulas, paired from the left (the last one may run to the end of the line), holds
/// two or more plain words in a row: letters only, two or more each, not a command name, outside
/// \text-like arguments. The missing '$' goes right after the last math character before the first such
/// word, so the punctuation in front of the prose stays outside. A formula that legitimately continues on
/// the next line has no prose in it and stays as it is; lines with '$$' are left alone.</para>
/// [Human] Ergänzt das schließende $ einer Formel, wenn direkt danach normaler Text folgt.
/// </summary>
public sealed partial class UnclosedInlineMathBeforeProseFix : ILatexLineFix {
    public string Description => "fehlendes $ vor Fließtext ergänzt";

    // arguments that hold text even inside a formula; their words are not prose in math mode
    private static readonly string[] TextArgumentCommands = [
        @"\text", @"\textbf", @"\textit", @"\textrm", @"\textsf", @"\mathrm", @"\mathit", @"\mathsf", @"\mathbf",
        @"\operatorname", @"\mbox", @"\emph", @"\qt", @"\label", @"\cref", @"\Cref", @"\ref", @"\eqref",
    ];

    public string FixLine(string code) {
        if (!code.Contains('$') || code.Contains("$$")) return code;

        var dollars = new List<int>();
        for (int i = 0; i < code.Length; i++) {
            if (code[i] == '$' && !LatexScan.IsEscaped(code, i)) dollars.Add(i);
        }
        if (dollars.Count % 2 == 0) return code;

        for (int k = 0; k < dollars.Count; k += 2) {
            int start = dollars[k] + 1;
            int end = k + 1 < dollars.Count ? dollars[k + 1] : code.Length;
            int prose = FindProse(code, start, end);
            if (prose < 0) continue;

            int insert = prose;
            while (insert > start && " ,;:.".Contains(code[insert - 1])) insert--;
            if (insert == start) return code; // no formula in front of the prose: some other error
            return code[..insert] + "$" + code[insert..];
        }
        return code;
    }

    /// <summary>Index in <paramref name="code"/> where two plain words in a row begin inside [start, end), or -1.</summary>
    private static int FindProse(string code, int start, int end) {
        string segment = code[start..end];
        char[] masked = segment.ToCharArray();
        foreach (string command in TextArgumentCommands) {
            int at = 0;
            while ((at = segment.IndexOf(command + "{", at, StringComparison.Ordinal)) >= 0) {
                int open = at + command.Length;
                int close = LatexScan.FindMatchingBrace(segment, open);
                int stop = close < 0 ? segment.Length : close;
                for (int i = open + 1; i < stop; i++) masked[i] = '#';
                at = open + 1;
            }
        }
        Match match = ProseRegex().Match(new string(masked));
        return match.Success ? start + match.Index : -1;
    }

    [GeneratedRegex(@"(?<![\\A-Za-z])[A-Za-z]{2,}(?:[ ,;:]+[A-Za-z]{2,}(?![A-Za-z]))+")]
    private static partial Regex ProseRegex();
}
