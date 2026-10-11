using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// \texorpdfstring whose plain-text half lost a ']' that came out as a surplus '}' - the five
/// content titles that broke the 2026-09-30 algebra PDF. Correct \texorpdfstring uses must pass
/// through untouched.
/// </summary>
public class TexorpdfstringBracketFixTests {
    private static readonly TexorpdfstringBracketFix Fix = new();

    [Fact]
    public void Restores_the_bracket_and_drops_the_surplus_brace() {
        string line = @"\begin{content}[{Series Representation and Addition in \texorpdfstring{$R[[x]]$}{R[[x]}}}]";

        Assert.Equal(
            @"\begin{content}[{Series Representation and Addition in \texorpdfstring{$R[[x]]$}{R[[x]]}}]",
            Fix.FixLine(line));
    }

    [Theory]
    [InlineData(@"\begin{content}[{Series Representation and Addition in \texorpdfstring{$R[[x]]$}{R[[x]]}}]")]
    [InlineData(@"\begin{content}[{Practical Arithmetic and Field Structure in \texorpdfstring{$\operatorname{Frac}(R)$}{Frac(R)}}]")]
    [InlineData(@"\subsection{The Category of Groups \texorpdfstring{$\mathbf{Grp}$}{Grp}}")]
    [InlineData(@"\section{Intervals \texorpdfstring{$[a,b)$}{[a,b)}}")]
    public void Leaves_correct_uses_alone(string line) {
        Assert.Equal(line, Fix.FixLine(line));
    }

    [Fact]
    public void Leaves_an_unbalanced_plain_text_alone_when_no_brace_is_surplus() {
        // Half-open interval written as "[a,b)" in the plain text: unbalanced brackets, but the braces
        // pair up, so nothing is broken and nothing may change.
        string line = @"\subsection{On \texorpdfstring{$[a,b)$}{[a,b)}}";
        Assert.Equal(line, Fix.FixLine(line));
    }
}
