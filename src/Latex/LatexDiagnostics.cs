using System.Collections.Generic;
using System.Linq;

namespace LectureExtraction.Latex;

/// <summary>
/// [AI Context] Finds structural LaTeX problems in a document body and says where they are - for the
/// AI repair prompt and the console. pdflatex stops at the first fatal error and often reports it far
/// from its cause (a missing \end surfaces at the next \end; a surplus '}' in a title surfaces as "extra
/// }" inside some box). This scan looks at the whole document at once and names line numbers. It only
/// reports; repairs belong to the auto-fixes in src/Latex/AutoFix.
///
/// <para>Findings are in English because their reader is the repair model. Checks: environment pairing
/// (<see cref="LatexScan.MatchEnvironments"/>), document-wide brace balance, and paragraphs with an odd
/// number of '$' (inline math must close inside its paragraph, or TeX reports "Missing $ inserted").</para>
/// [Human] Findet Strukturfehler (Umgebungen, Klammern, $) mit Zeilennummer - nur Befund, keine Reparatur.
/// </summary>
public static class LatexDiagnostics {
    /// <summary>Cap on findings, so a badly broken document does not flood the repair prompt.</summary>
    public const int MaxFindings = 20;

    private const int MaxBraceFindingsPerKind = 3;

    public static IReadOnlyList<string> Analyze(string tex) {
        var findings = new List<string>();
        AddEnvironmentFindings(tex, findings);
        AddBraceFindings(tex, findings);
        AddInlineMathFindings(tex, findings);

        if (findings.Count <= MaxFindings) return findings;
        int more = findings.Count - MaxFindings;
        return [.. findings.Take(MaxFindings), $"... and {more} more finding(s) of the same kinds."];
    }

    /// <summary>The findings as a block to append to a compile log in a repair prompt; empty when there are none.</summary>
    public static string FormatForPrompt(IReadOnlyList<string> findings) =>
        findings.Count == 0
            ? ""
            : "\n\n### Structural findings from the pipeline's own LaTeX scanner\n" +
              "Computed over the whole body file; line numbers refer to that file. pdflatex stops at its first fatal error, " +
              "so these may name problems the log above does not show yet. Fix every one of them.\n" +
              string.Join("\n", findings.Select(finding => "- " + finding));

    private static void AddEnvironmentFindings(string tex, List<string> findings) {
        List<LatexScan.EnvironmentToken> tokens = LatexScan.EnvironmentTokens(tex);
        LatexScan.EnvironmentMatch match = LatexScan.MatchEnvironments(tokens);

        foreach (LatexScan.ForcedClose forced in match.ForcedCloses) {
            LatexScan.EnvironmentToken end = tokens[forced.EndToken];
            LatexScan.EnvironmentToken begin = tokens[forced.BeginToken];
            string open = string.Join(", ", forced.LeftOpen.Select(i => $"\\begin{{{tokens[i].Name}}} (line {tokens[i].Line})"));
            findings.Add($"Line {end.Line}: \\end{{{end.Name}}} closes \\begin{{{begin.Name}}} (line {begin.Line}) while {open} is still open - is an \\end missing before line {end.Line}?");
        }

        foreach (int i in match.StrayEnds) {
            findings.Add($"Line {tokens[i].Line}: \\end{{{tokens[i].Name}}} has no matching \\begin{{{tokens[i].Name}}} - stray, or the \\end of another environment under the wrong name?");
        }

        foreach (int i in match.OpenAtEnd) {
            findings.Add($"Line {tokens[i].Line}: \\begin{{{tokens[i].Name}}} is never closed.");
        }
    }

    private static void AddBraceFindings(string tex, List<string> findings) {
        var open = new Stack<(int Index, int Line)>();
        int surplus = 0;

        foreach (LatexScan.CodeSegment segment in LatexScan.CodeSegments(tex)) {
            string code = segment.Text;
            for (int i = 0; i < code.Length; i++) {
                char c = code[i];
                if (c == '\\') { i++; continue; }
                if (c == '{') {
                    open.Push((segment.Start + i, segment.Line));
                }
                else if (c == '}') {
                    if (open.Count > 0) open.Pop();
                    else if (surplus++ < MaxBraceFindingsPerKind) {
                        findings.Add($"Line {segment.Line}: surplus '}}' - it closes a group that was never opened (near \"{LatexScan.Snippet(tex, segment.Start + i)}\").");
                    }
                }
            }
        }

        foreach (var (index, line) in open.Take(MaxBraceFindingsPerKind)) {
            findings.Add($"Line {line}: '{{' is never closed (near \"{LatexScan.Snippet(tex, index)}\").");
        }
    }

    private static void AddInlineMathFindings(string tex, List<string> findings) {
        string[] lines = tex.Split('\n');
        var dollarsPerLine = new int[lines.Length + 1];
        foreach (LatexScan.CodeSegment segment in LatexScan.CodeSegments(tex)) {
            dollarsPerLine[segment.Line] += LatexScan.CountUnescaped(segment.Text, '$');
        }

        int paragraphStart = 0;
        int dollars = 0;
        for (int line = 1; line <= lines.Length + 1; line++) {
            bool paragraphBreak = line > lines.Length || string.IsNullOrWhiteSpace(lines[line - 1]);
            if (paragraphBreak) {
                if (paragraphStart > 0 && dollars % 2 == 1) {
                    findings.Add($"Paragraph starting at line {paragraphStart}: odd number of '$' - an inline formula is opened but never closed (or closed but never opened).");
                }
                paragraphStart = 0;
                dollars = 0;
                continue;
            }

            if (paragraphStart == 0) paragraphStart = line;
            dollars += dollarsPerLine[line];
        }
    }
}
