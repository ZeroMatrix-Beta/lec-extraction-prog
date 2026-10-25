using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

public class BareMathCommandInTitleFixTests {
    private static readonly BareMathCommandInTitleFix Fix = new();

    // The two failing titles from step 4 of the Analysis II lecture of 2026-02-23 (rerun of 2026-10-25).
    [Fact]
    public void WrapsImpliesInProofWrapperTitle() {
        string input = @"\begin{proofWrapper}[Proof of Equivalence: Sequential Compactness \implies Topological Compactness]";
        string expected = @"\begin{proofWrapper}[Proof of Equivalence: Sequential Compactness $\implies$ Topological Compactness]";
        Assert.Equal(expected, Fix.FixLine(input));
    }

    [Fact]
    public void WrapsImpliesInContentTitle() {
        string input = @"\begin{content}[Goal: Sequential Compactness \implies Topological Compactness]";
        string expected = @"\begin{content}[Goal: Sequential Compactness $\implies$ Topological Compactness]";
        Assert.Equal(expected, Fix.FixLine(input));
    }

    [Fact]
    public void WrapsEveryBareCommandAndLeavesMathAlone() {
        string input = @"\begin{lemma}[$f \colon X \to Y$ continuous \iff preimages of open sets are open, $U \subseteq Y$]";
        string expected = @"\begin{lemma}[$f \colon X \to Y$ continuous $\iff$ preimages of open sets are open, $U \subseteq Y$]";
        Assert.Equal(expected, Fix.FixLine(input));
    }

    [Theory]
    [InlineData(@"\begin{proofWrapper}[Proof of Equivalence: Sequential Compactness $\implies$ Topological Compactness]")]
    [InlineData(@"\begin{theorem}[Heine--Borel]\label[theorem]{thm:heine-borel}")]
    [InlineData(@"\begin{content}[Indexierung \texorpdfstring{$I \to X$}{I to X}]")]
    [InlineData(@"\begin{definition}[Total order]")]
    [InlineData(@"The map $f \colon X \to Y$ is continuous \implies nothing, outside a title.")]
    [InlineData(@"\begin{remark}[Includes \textbackslash in and \inlinemetanote{x}]")]
    public void LeavesCompilingLinesUntouched(string line) {
        Assert.Equal(line, Fix.FixLine(line));
    }

    [Fact]
    public void IsIdempotent() {
        string once = Fix.FixLine(@"\begin{content}[A \subseteq B \implies f(A) \subseteq f(B)]");
        Assert.Equal(once, Fix.FixLine(once));
    }
}
