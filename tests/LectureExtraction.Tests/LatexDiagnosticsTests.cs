using System.Linq;
using LectureExtraction.Latex;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The read-only findings that go to the AI repair prompt. They are only useful if the line numbers
/// are right and a clean document produces nothing - a repair model told about problems that do not
/// exist would "fix" correct LaTeX.
/// </summary>
public class LatexDiagnosticsTests {
    [Fact]
    public void A_clean_document_has_no_findings() {
        string tex = "\\begin{content}[Board]\nWe have $x$ and $$y$$ and 5\\$.\n\\end{content}\n";

        Assert.Empty(LatexDiagnostics.Analyze(tex));
        Assert.Equal("", LatexDiagnostics.FormatForPrompt(LatexDiagnostics.Analyze(tex)));
    }

    [Fact]
    public void Points_at_the_line_with_the_surplus_brace_from_the_09_30_lecture() {
        string tex = "\\section{Power series}\n\\begin{content}[{Ring Structure of \\texorpdfstring{$R[[x]]$}{R[[x]}}}]\nx\n\\end{content}\n";

        string finding = Assert.Single(LatexDiagnostics.Analyze(tex));

        Assert.StartsWith("Line 2: surplus '}'", finding);
    }

    [Fact]
    public void Names_the_run_over_environment_and_where_it_was_opened() {
        string tex = "\\begin{content}\n\\begin{itemize}\n\\item a\n\\end{content}\n";

        string finding = Assert.Single(LatexDiagnostics.Analyze(tex));

        Assert.Contains("Line 4: \\end{content} closes \\begin{content} (line 1) while \\begin{itemize} (line 2) is still open", finding);
    }

    [Fact]
    public void Reports_stray_and_never_closed_environments() {
        var findings = LatexDiagnostics.Analyze("\\end{student}\n\\begin{speech}\nWords.\n");

        Assert.Contains(findings, f => f.StartsWith("Line 1: \\end{student} has no matching"));
        Assert.Contains(findings, f => f == "Line 2: \\begin{speech} is never closed.");
    }

    [Fact]
    public void Reports_a_paragraph_with_an_unclosed_inline_formula() {
        string tex = "First paragraph with $x$.\n\nSo, we take the $y_2 y_3 and we use this equation.\nStill the same paragraph.\n\nThird $z$.\n";

        string finding = Assert.Single(LatexDiagnostics.Analyze(tex));

        Assert.StartsWith("Paragraph starting at line 3: odd number of '$'", finding);
    }

    [Fact]
    public void Ignores_dollars_and_braces_in_comments_and_verbatim_content() {
        string tex = "Text % costs $5 and {\n\\begin{verbatim}\n$ { \\begin{itemize}\n\\end{verbatim}\n";

        Assert.Empty(LatexDiagnostics.Analyze(tex));
    }

    [Fact]
    public void Caps_the_number_of_findings() {
        string tex = string.Concat(Enumerable.Repeat("\\end{proof}\n", 30));

        var findings = LatexDiagnostics.Analyze(tex);

        Assert.Equal(LatexDiagnostics.MaxFindings + 1, findings.Count);
        Assert.StartsWith("... and 10 more", findings[^1]);
    }

    [Fact]
    public void The_prompt_block_lists_every_finding_under_a_header() {
        var findings = LatexDiagnostics.Analyze("\\begin{speech}\n");

        string block = LatexDiagnostics.FormatForPrompt(findings);

        Assert.Contains("### Structural findings from the pipeline's own LaTeX scanner", block);
        Assert.Contains("- Line 1: \\begin{speech} is never closed.", block);
    }
}
