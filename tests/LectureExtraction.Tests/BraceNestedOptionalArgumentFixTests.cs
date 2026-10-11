using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Optional arguments with nested square brackets - the theorem title that broke the 2026-09-30
/// algebra PDF. Broken forms must come out braced; correct LaTeX, including the already-braced form,
/// must pass through untouched.
/// </summary>
public class BraceNestedOptionalArgumentFixTests {
    private static readonly BraceNestedOptionalArgumentFix Fix = new();

    [Fact]
    public void Braces_a_theorem_title_with_brackets_inside_math() {
        string line = @"\begin{theorem}[Units in $R[x]$ over an Integral Domain]\label[theorem]{thm:units-poly-integral-domain}";

        Assert.Equal(
            @"\begin{theorem}[{Units in $R[x]$ over an Integral Domain}]\label[theorem]{thm:units-poly-integral-domain}",
            Fix.FixLine(line));
    }

    [Fact]
    public void Braces_the_lemma_title_that_broke_the_10_06_lecture() {
        string line = @"\begin{lemma}[Division Algorithm in $\mathbb{Q}[x]$]\label[lemma]{lem:poly-division-algorithm}";

        Assert.Equal(@"\begin{lemma}[{Division Algorithm in $\mathbb{Q}[x]$}]\label[lemma]{lem:poly-division-algorithm}", Fix.FixLine(line));
    }

    [Theory]
    [InlineData(@"\begin{content}[Units and Prime Divisors in $\mathbb{Z}$ and $\mathbb{Z}[i]$]")] // compiled in the 09-16 lecture
    [InlineData(@"\begin{math-stroke}[On $R[x]$]")]
    public void Leaves_bracket_aware_environments_alone(string line) {
        Assert.Equal(line, Fix.FixLine(line));
    }

    [Fact]
    public void Braces_an_item_label_with_an_interval() {
        Assert.Equal(@"\item[{$[a,b]$}] closed interval", Fix.FixLine(@"\item[$[a,b]$] closed interval"));
    }

    [Fact]
    public void Braces_every_affected_argument_on_a_line() {
        Assert.Equal(@"\item[{$[0,1]$}] and \item[{$[1,2]$}]", Fix.FixLine(@"\item[$[0,1]$] and \item[$[1,2]$]"));
    }

    [Theory]
    [InlineData(@"\begin{theorem}[{Units in $R[x]$ over an Integral Domain}]\label[theorem]{thm:x}")]
    [InlineData(@"\begin{definition}[Formal Power Series (Series Representation)]\label[definition]{def:power-series-series}")]
    [InlineData(@"\begin{speech}[00:00:00 - 00:00:26]")]
    [InlineData(@"\begin{content}[Definition of the Candidate Map \texorpdfstring{$\hat{\phi}$}{phi-hat}]")]
    [InlineData(@"An element $(a_0, a_1, \dots) \in R[[x]]$ is a formal power series.")]
    [InlineData(@"\begin{remark}[Display \[x\] versus inline]")]
    public void Leaves_correct_lines_alone(string line) {
        Assert.Equal(line, Fix.FixLine(line));
    }

    [Fact]
    public void Leaves_an_argument_that_does_not_close_on_its_line_alone() {
        string line = @"\begin{theorem}[Units in $R[x]$ over";
        Assert.Equal(line, Fix.FixLine(line));
    }

    [Fact]
    public void Leaves_an_argument_with_a_surplus_brace_to_other_fixes() {
        string line = @"\begin{remark}[{Ring of \texorpdfstring{$R[[x]]$}{R[[x]}}}]";
        Assert.Equal(line, Fix.FixLine(line));
    }
}
