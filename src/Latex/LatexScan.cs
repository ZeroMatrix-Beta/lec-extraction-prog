using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex;

/// <summary>
/// [AI Context] The LaTeX scanning rules every tool in src/Latex must agree on - the "parsing tricks"
/// behind LatexCommentStripper, the auto-fixes and LatexDiagnostics. Deliberately NOT a LaTeX parser:
/// catcode changes, macro expansion and \verb are out of reach. These rules hold for the documents the
/// pipeline produces, and keeping them in one place stops each tool from inventing its own variant.
///
/// <list type="number">
/// <item><b>Escapes.</b> A character is escaped when an ODD number of backslashes precedes it: <c>\%</c>
/// is a percent sign, <c>\\%</c> is a line break followed by a comment. Never test only the one
/// character before (<see cref="IsEscaped"/>).</item>
/// <item><b>Comments.</b> An unescaped '%' starts a comment that runs to the end of the line. Look for
/// structure only in the code before it; when rewriting a line, re-attach the comment unchanged
/// (<see cref="FindCommentStart"/>).</item>
/// <item><b>Optional arguments.</b> <c>[...]</c> ends at the first ']' outside braces; brackets inside
/// <c>{...}</c> are invisible to LaTeX. Nesting has to be counted - so regexes may locate where something
/// starts, but never decide where it ends (<see cref="FindOptionalArgumentEnd"/>).</item>
/// <item><b>Environments.</b> <c>\begin{x}</c>/<c>\end{x}</c> pair like brackets. LaTeX reports a missing
/// <c>\end</c> at the NEXT <c>\end</c> ("\begin{itemize} ... ended by \end{content}"), not where it is
/// missing (<see cref="EnvironmentTokens"/>, <see cref="MatchEnvironments"/>).</item>
/// <item><b>Verbatim.</b> Inside verbatim-like environments nothing is structure - no comments, braces or
/// environments - until the literal <c>\end{name}</c> (<see cref="CodeSegments"/>).</item>
/// </list>
/// [Human] Gemeinsame LaTeX-Scan-Regeln: Escapes, Kommentare, Klammern, Umgebungen, Verbatim.
/// </summary>
public static partial class LatexScan {
    /// <summary>Environments whose content is literal text.</summary>
    public static IReadOnlySet<string> VerbatimEnvironments { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "verbatim", "Verbatim", "lstlisting", "minted", "comment" };

    /// <summary>True when an odd number of backslashes directly precedes <paramref name="index"/>.</summary>
    public static bool IsEscaped(string text, int index) {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && text[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>Index of the '%' that starts this line's comment, or -1 if the line has none.</summary>
    public static int FindCommentStart(string line) {
        for (int i = 0; i < line.Length; i++) {
            if (line[i] == '%' && !IsEscaped(line, i)) return i;
        }
        return -1;
    }

    /// <summary>Index of the '}' matching the '{' at <paramref name="open"/>, or -1 if it never closes.</summary>
    public static int FindMatchingBrace(string text, int open) {
        int depth = 0;
        for (int i = open; i < text.Length; i++) {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>How often <paramref name="wanted"/> occurs without a backslash escape.</summary>
    public static int CountUnescaped(string text, char wanted) {
        int count = 0;
        for (int i = 0; i < text.Length; i++) {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == wanted) count++;
        }
        return count;
    }

    /// <summary>
    /// The ']' that closes the optional argument opened at <paramref name="open"/>, counting nested
    /// brackets the way the author meant them; -1 if it does not close in <paramref name="text"/> or a
    /// brace closes that was never opened. Brackets inside braces do not count - LaTeX does not see them.
    /// <paramref name="hasNestedBrackets"/> tells whether LaTeX itself would have ended the argument early.
    /// </summary>
    public static int FindOptionalArgumentEnd(string text, int open, out bool hasNestedBrackets) =>
        FindOptionalArgumentEnd(text, open, out hasNestedBrackets, out _);

    /// <summary>
    /// As above. Brackets inside <c>$...$</c> are counted on their own and never end the argument, because
    /// the author meant them as mathematics: the ']' of a half-open interval <c>$(0, 1]$</c> pairs with
    /// nothing, yet it is not where the title ends (Analysis II 2026-02-25, the most frequent compile error).
    /// <paramref name="hasUnpairedMathBrackets"/> tells whether such a bracket occurs; that breaks even the
    /// xparse arguments that count bracket pairs.
    /// </summary>
    public static int FindOptionalArgumentEnd(string text, int open, out bool hasNestedBrackets, out bool hasUnpairedMathBrackets) {
        hasNestedBrackets = false;
        hasUnpairedMathBrackets = false;
        int braceDepth = 0;
        int bracketDepth = 1;
        bool inMath = false;
        int mathBracketDepth = 0;

        for (int i = open + 1; i < text.Length; i++) {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') { braceDepth++; continue; }
            if (c == '}') {
                if (--braceDepth < 0) return -1;
                continue;
            }
            if (braceDepth > 0) continue;
            if (c == '$') {
                if (inMath && mathBracketDepth > 0) hasUnpairedMathBrackets = true; // "[0, 1)"
                inMath = !inMath;
                mathBracketDepth = 0;
                continue;
            }
            if (inMath) {
                if (c == '[') mathBracketDepth++;
                else if (c == ']') {
                    hasNestedBrackets = true; // LaTeX would stop here
                    if (mathBracketDepth > 0) mathBracketDepth--;
                    else hasUnpairedMathBrackets = true;
                }
                continue;
            }
            if (c == '[') { bracketDepth++; hasNestedBrackets = true; }
            else if (c == ']' && --bracketDepth == 0) return i;
        }

        return -1;
    }

    /// <summary>A stretch of code: one line's text outside comments and verbatim content.</summary>
    /// <param name="Start">Index of the first character in the whole document.</param>
    /// <param name="Line">1-based line number.</param>
    public readonly record struct CodeSegment(int Start, string Text, int Line);

    /// <summary>
    /// The document's code, line by line, without comments and without the content of verbatim-like
    /// environments. The verbatim markers themselves stay in, so environment matching still sees them.
    /// Every structural scan should iterate this rather than the raw text.
    /// </summary>
    public static IEnumerable<CodeSegment> CodeSegments(string tex) {
        string? verbatim = null;
        int lineStart = 0;
        int lineNumber = 1;

        while (true) {
            int lineEnd = tex.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = tex.Length;
            string line = tex[lineStart..lineEnd];
            int from = 0;

            while (from <= line.Length) {
                if (verbatim != null) {
                    int endAt = line.IndexOf($"\\end{{{verbatim}}}", from, StringComparison.Ordinal);
                    if (endAt < 0) break; // the rest of this line is literal
                    verbatim = null;
                    from = endAt; // structure resumes at the \end marker
                    continue;
                }

                string rest = line[from..];
                int commentStart = FindCommentStart(rest);
                string code = commentStart < 0 ? rest : rest[..commentStart];

                Match begin = VerbatimBeginRegex().Match(code);
                while (begin.Success && IsEscaped(code, begin.Index)) begin = begin.NextMatch();
                if (begin.Success) {
                    int cut = begin.Index + begin.Length;
                    yield return new CodeSegment(lineStart + from, code[..cut], lineNumber);
                    verbatim = begin.Groups[1].Value;
                    from += cut;
                    continue;
                }

                yield return new CodeSegment(lineStart + from, code, lineNumber);
                break;
            }

            if (lineEnd == tex.Length) yield break;
            lineStart = lineEnd + 1;
            lineNumber++;
        }
    }

    /// <summary>One <c>\begin{name}</c> or <c>\end{name}</c> in the code.</summary>
    /// <param name="Index">Index of the backslash in the whole document.</param>
    /// <param name="Length">Length of the whole token, e.g. 13 for <c>\begin{proof}</c>.</param>
    public readonly record struct EnvironmentToken(bool IsBegin, string Name, int Index, int Length, int Line);

    /// <summary>Every environment token in document order, outside comments and verbatim content.</summary>
    public static List<EnvironmentToken> EnvironmentTokens(string tex) {
        var tokens = new List<EnvironmentToken>();
        foreach (CodeSegment segment in CodeSegments(tex)) {
            foreach (Match match in EnvironmentRegex().Matches(segment.Text)) {
                if (IsEscaped(segment.Text, match.Index)) continue; // "\\begin" is a line break plus text
                tokens.Add(new EnvironmentToken(
                    match.Groups[1].Value == "begin", match.Groups[2].Value,
                    segment.Start + match.Index, match.Length, segment.Line));
            }
        }
        return tokens;
    }

    /// <summary>A <c>\end</c> that closed its <c>\begin</c> while environments opened inside it were still open.</summary>
    /// <param name="LeftOpen">The environments it ran over, outermost first.</param>
    public sealed record ForcedClose(int EndToken, int BeginToken, IReadOnlyList<int> LeftOpen);

    /// <summary>Result of pairing <c>\begin</c>/<c>\end</c> tokens; all values are indices into the token list.</summary>
    /// <param name="UnmatchedBegins">Every <c>\begin</c> without its own <c>\end</c>.</param>
    /// <param name="StrayEnds"><c>\end</c> tokens with no open <c>\begin</c> of that name.</param>
    /// <param name="ForcedCloses"><c>\end</c> tokens that ran over still-open environments.</param>
    /// <param name="OpenAtEnd"><c>\begin</c> tokens still open at the end of the document, outermost first.</param>
    public sealed record EnvironmentMatch(
        IReadOnlySet<int> UnmatchedBegins,
        IReadOnlyList<int> StrayEnds,
        IReadOnlyList<ForcedClose> ForcedCloses,
        IReadOnlyList<int> OpenAtEnd) {
        public bool IsBalanced => UnmatchedBegins.Count == 0 && StrayEnds.Count == 0;
    }

    /// <summary>
    /// Pairs the tokens like brackets. An <c>\end{x}</c> closes the innermost open <c>x</c>; whatever
    /// was opened inside it and is still open counts as unclosed (that is how LaTeX itself recovers).
    /// </summary>
    public static EnvironmentMatch MatchEnvironments(IReadOnlyList<EnvironmentToken> tokens) {
        var stack = new List<int>();
        var unmatched = new HashSet<int>();
        var stray = new List<int>();
        var forced = new List<ForcedClose>();

        for (int i = 0; i < tokens.Count; i++) {
            EnvironmentToken token = tokens[i];
            if (token.IsBegin) {
                stack.Add(i);
                continue;
            }

            int position = stack.FindLastIndex(open => tokens[open].Name == token.Name);
            if (position < 0) {
                stray.Add(i);
                continue;
            }

            if (position < stack.Count - 1) {
                List<int> leftOpen = stack.GetRange(position + 1, stack.Count - position - 1);
                unmatched.UnionWith(leftOpen);
                forced.Add(new ForcedClose(i, stack[position], leftOpen));
            }
            stack.RemoveRange(position, stack.Count - position);
        }

        unmatched.UnionWith(stack);
        return new EnvironmentMatch(unmatched, stray, forced, [.. stack]);
    }

    /// <summary>Up to 30 characters either side of <paramref name="index"/> on its line, for messages.</summary>
    public static string Snippet(string text, int index) {
        int lineStart = index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        int lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = text.Length;
        int from = Math.Max(lineStart, index - 30);
        int to = Math.Min(lineEnd, index + 30);
        return text[from..to].Trim();
    }

    [GeneratedRegex(@"\\(begin|end)\{([A-Za-z@*]+)\}")]
    private static partial Regex EnvironmentRegex();

    [GeneratedRegex(@"\\begin\{(verbatim|Verbatim|lstlisting|minted|comment)\}")]
    private static partial Regex VerbatimBeginRegex();
}
