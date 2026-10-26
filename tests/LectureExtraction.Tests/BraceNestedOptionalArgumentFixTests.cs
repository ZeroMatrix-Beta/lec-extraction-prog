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
    public void Braces_the_half_open_interval_that_broke_the_02_25_lecture() {
        string line = @"\begin{example}[Failure of Compactness for $A = (0, 1] \subset \mathbb{R}$]\label[example]{ex:half-open-interval-non-compact}";

        Assert.Equal(
            @"\begin{example}[{Failure of Compactness for $A = (0, 1] \subset \mathbb{R}$}]\label[example]{ex:half-open-interval-non-compact}",
            Fix.FixLine(line));
    }

    [Theory]
    [InlineData(@"\begin{content}[Non-Compactness of $(0, 1]$]", @"\begin{content}[{Non-Compactness of $(0, 1]$}]")]
    [InlineData(@"\begin{content}[Compactness of $[0, 1)$]", @"\begin{content}[{Compactness of $[0, 1)$}]")] // xparse counts the lone '['
    [InlineData(@"\item[$(a, b]$] half-open", @"\item[{$(a, b]$}] half-open")]
    public void Braces_unpaired_interval_brackets_in_every_environment(string line, string expected) {
        Assert.Equal(expected, Fix.FixLine(line));
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
    [InlineData(@"\begin{lemma}[Compactness of $[0, 1)$]")] // a classic argument ends at the first ']' only
    [InlineData(@"\begin{content}[On $[a, b]$ and $\mathbb{Z}[i]$]")]
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
