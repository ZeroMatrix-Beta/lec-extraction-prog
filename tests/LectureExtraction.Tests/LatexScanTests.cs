using System.Linq;
using LectureExtraction.Latex;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The shared LaTeX scanning rules ("parsing tricks") every tool in src/Latex relies on. Each test
/// pins one rule; a tool that disagrees with these is the bug.
/// </summary>
public class LatexScanTests {
    [Theory]
    [InlineData(@"\%", 1, true)]     // escaped percent sign
    [InlineData(@"\\%", 2, false)]   // line break, then a real comment
    [InlineData(@"\\\%", 3, true)]   // line break, then an escaped percent sign
    [InlineData(@"a%", 1, false)]
    public void Escapes_count_an_odd_number_of_backslashes(string text, int index, bool escaped) {
        Assert.Equal(escaped, LatexScan.IsEscaped(text, index));
    }

    [Theory]
    [InlineData(@"50\% of all % comment", 12)]
    [InlineData(@"a \\% comment", 4)]
    [InlineData(@"no comment here", -1)]
    public void A_comment_starts_at_the_first_unescaped_percent(string line, int expected) {
        Assert.Equal(expected, LatexScan.FindCommentStart(line));
    }

    [Fact]
    public void Brace_matching_skips_escaped_braces() {
        Assert.Equal(6, LatexScan.FindMatchingBrace(@"{a\}b}c", 0) + 1);
        Assert.Equal(-1, LatexScan.FindMatchingBrace(@"{never closed", 0));
    }

    [Fact]
    public void Counting_skips_escaped_characters() {
        Assert.Equal(2, LatexScan.CountUnescaped(@"$a$ costs \$5", '$'));
    }

    [Fact]
    public void An_optional_argument_ends_where_the_author_meant_and_reports_nesting() {
        string text = @"[Units in $R[x]$ over D] rest";

        int end = LatexScan.FindOptionalArgumentEnd(text, 0, out bool nested);

        Assert.Equal(text.IndexOf("] rest"), end);
        Assert.True(nested);
    }

    [Fact]
    public void Brackets_inside_braces_are_invisible_to_an_optional_argument() {
        int end = LatexScan.FindOptionalArgumentEnd(@"[{a]b}]", 0, out bool nested);

        Assert.Equal(6, end);
        Assert.False(nested);
    }

    [Fact]
    public void Environment_tokens_skip_comments_line_breaks_and_verbatim_content() {
        string tex = string.Join("\n",
            @"\begin{content} % \begin{itemize}",
            @"\\begin{notanenv}",
            @"\begin{verbatim}",
            @"\begin{itemize} \end{content}",
            @"\end{verbatim}",
            @"\end{content}");

        var tokens = LatexScan.EnvironmentTokens(tex);

        Assert.Equal(
            ["+content@1", "+verbatim@3", "-verbatim@5", "-content@6"],
            tokens.Select(t => $"{(t.IsBegin ? "+" : "-")}{t.Name}@{t.Line}"));
    }

    [Fact]
    public void Matching_reports_run_over_stray_and_open_environments() {
        string tex = string.Join("\n",
            @"\begin{content}", @"\begin{itemize}", @"\end{content}", // run over itemize
            @"\end{proof}",                                         // stray
            @"\begin{speech}");                                     // open at the end

        var tokens = LatexScan.EnvironmentTokens(tex);
        var match = LatexScan.MatchEnvironments(tokens);

        LatexScan.ForcedClose forced = Assert.Single(match.ForcedCloses);
        Assert.Equal("itemize", tokens[Assert.Single(forced.LeftOpen)].Name);
        Assert.Equal("proof", tokens[Assert.Single(match.StrayEnds)].Name);
        Assert.Equal("speech", tokens[Assert.Single(match.OpenAtEnd)].Name);
        Assert.False(match.IsBalanced);
    }

    [Fact]
    public void Code_segments_drop_comments_and_verbatim_content_but_keep_the_markers() {
        string tex = "a % comment\n\\begin{verbatim}\n% { not code\n\\end{verbatim} b";

        string code = string.Join("|", LatexScan.CodeSegments(tex).Select(s => s.Text));

        Assert.Equal("a |\\begin{verbatim}|\\end{verbatim} b", code);
    }

    [Fact]
    public void A_snippet_stays_on_its_line() {
        Assert.Equal("second line", LatexScan.Snippet("first\nsecond line\nthird", 9));
    }
}
