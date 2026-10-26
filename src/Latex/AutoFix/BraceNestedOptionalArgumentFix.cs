using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Optional arguments with nested square brackets. LaTeX ends an optional argument at
/// the first ']' outside braces, so <c>\begin{theorem}[Units in $R[x]$ over ...]</c> closes after
/// <c>R[x</c> and the rest spills into the document ("Extra }, or forgotten $"). Wrapping the
/// argument in <c>{...}</c> hides the inner brackets. Covers <c>\begin{env}[...]</c> and
/// <c>\item[...]</c>. An interval in math that pairs with nothing (<c>$(0, 1]$</c>, <c>$[0, 1)$</c>) is
/// braced too, in every environment: it breaks even the bracket-counting ones below.
///
/// <para>The regex only locates where an argument starts; where it ends is decided by counting
/// bracket and brace depth, because nesting is exactly what a regex cannot count. Lines whose
/// argument does not close on the line, or whose braces go negative inside it, are left alone -
/// those belong to other error classes.</para>
/// [Human] Setzt optionale Argumente mit verschachtelten eckigen Klammern in {...}.
/// </summary>
public sealed partial class BraceNestedOptionalArgumentFix : ILatexLineFix {
    /// <summary>
    /// Environments whose optional argument already copes with nested brackets: defined with
    /// <c>\NewTColorBox</c> and an xparse argument spec in pdf-preamble.tex (<c>content</c> takes
    /// <c>{ O{} }</c>), which counts bracket pairs. The 09-16 lecture compiled with
    /// <c>\begin{content}[... $\mathbb{Z}[i]$]</c>; bracing those would be harmless but needless.
    /// Everything defined with \newtheorem, \newtcolorbox[1][...] or \newenvironment reads a classic
    /// LaTeX optional argument that ends at the first ']' - those are the ones this fix is for.
    /// </summary>
    public static IReadOnlySet<string> BracketAwareEnvironments { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "content", "math-stroke", "highlight" };

    public string Description => "optionales Argument in {...} gesetzt";

    public string FixLine(string code) {
        int searchFrom = 0;
        while (true) {
            Match match = ArgumentStartRegex().Match(code, searchFrom);
            if (!match.Success) return code;

            int open = match.Index + match.Length - 1; // the '['
            int close = LatexScan.FindOptionalArgumentEnd(code, open, out bool hasNestedBrackets, out bool hasUnpairedMathBrackets);
            bool endsEarly = BracketAwareEnvironments.Contains(match.Groups[1].Value)
                ? hasUnpairedMathBrackets
                : hasNestedBrackets;
            if (close < 0 || !endsEarly) {
                searchFrom = open + 1;
                continue;
            }

            code = code[..(open + 1)] + "{" + code[(open + 1)..close] + "}" + code[close..];
            searchFrom = close + 3; // past both inserted braces and the ']'
        }
    }

    [GeneratedRegex(@"\\(?:begin\{([^}]+)\}|item)\[")]
    private static partial Regex ArgumentStartRegex();
}
