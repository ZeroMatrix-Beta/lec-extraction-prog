using System.Text;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] A math-only command written as prose in a box title: <c>\begin{proofWrapper}[Proof of
/// Equivalence: Sequential Compactness \implies Topological Compactness]</c> ("Missing $ inserted" in step 4
/// of the Analysis II lecture of 2026-02-23, rerun of 2026-10-25, twice: a <c>proofWrapper</c> and a
/// <c>content</c> title). Titles are typeset in text mode, so <c>\implies</c>, <c>\to</c>, <c>\subseteq</c>
/// and their kind need <c>$...$</c> there. Wraps each such command that stands outside math in its own
/// <c>$...$</c>; a command already inside <c>$...$</c>, and every other line, stays untouched.
///
/// <para>Only the optional argument of <c>\begin{env}[...]</c> is scanned; where it ends is counted with
/// <see cref="LatexScan.FindOptionalArgumentEnd"/>, and whether a position is in math is counted with the
/// unescaped dollars before it. An argument with <c>\texorpdfstring</c> is skipped: its second half is
/// plain text for the bookmark, where a dollar would be wrong too.</para>
/// [Human] Setzt Mathe-Befehle wie \implies, die in einem Kastentitel außerhalb von $...$ stehen, in $...$.
/// </summary>
public sealed partial class BareMathCommandInTitleFix : ILatexLineFix {
    public string Description => @"Mathe-Befehl im Titel in $...$ gesetzt";

    public string FixLine(string code) {
        if (!code.Contains(@"\begin{")) return code;
        int searchFrom = 0;
        while (true) {
            Match match = ArgumentStartRegex().Match(code, searchFrom);
            if (!match.Success) return code;

            int open = match.Index + match.Length - 1; // the '['
            int close = LatexScan.FindOptionalArgumentEnd(code, open, out _);
            if (close < 0) return code;

            string argument = code[(open + 1)..close];
            string fixedArgument = argument.Contains(@"\texorpdfstring") ? argument : WrapBareCommands(argument);
            code = code[..(open + 1)] + fixedArgument + code[close..];
            searchFrom = open + 1 + fixedArgument.Length + 1;
        }
    }

    private static string WrapBareCommands(string argument) {
        var result = new StringBuilder(argument.Length + 8);
        bool inMath = false;
        for (int i = 0; i < argument.Length; i++) {
            char c = argument[i];
            if (c == '$' && !LatexScan.IsEscaped(argument, i)) {
                inMath = !inMath;
                result.Append(c);
                continue;
            }
            if (c == '\\' && !inMath && !LatexScan.IsEscaped(argument, i)) {
                Match command = MathCommandRegex().Match(argument, i);
                if (command.Success && command.Index == i) {
                    result.Append('$').Append(command.Value).Append('$');
                    i += command.Length - 1;
                    continue;
                }
            }
            result.Append(c);
        }
        return result.ToString();
    }

    [GeneratedRegex(@"\\begin\{[^}]+\}\[")]
    private static partial Regex ArgumentStartRegex();

    /// <summary>Relations, arrows and operators that exist only in math mode; the name must end there.</summary>
    [GeneratedRegex(@"\\(?:implies|impliedby|iff|Rightarrow|Leftarrow|Leftrightarrow|Longrightarrow|Longleftrightarrow|rightarrow|leftarrow|leftrightarrow|to|mapsto|subseteq|subsetneq|subset|supseteq|supset|notin|in|cup|cap|setminus|leq|geq|neq|le|ge|times|cdot|infty|forall|exists)(?![A-Za-z@])")]
    private static partial Regex MathCommandRegex();
}
