using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

public class NewLatexAutoFixTests {
    private static readonly TrailingDollarAfterParenFix TrailingDollarFix = new();
    private static readonly UnclosedEnvironmentEndFix UnclosedEndFix = new();
    private static readonly InlineMetaNoteMathFix InlineMetaFix = new();

    [Fact]
    public void TrailingDollarAfterParenFix_RemovesExtraneousDollar() {
        string input = @"Since $r_1, r_2 \in \mathbb{Q}$ and $i$ is imaginary ($i \notin \mathbb{Q}$)$, the real and imaginary parts must vanish independently:";
        string expected = @"Since $r_1, r_2 \in \mathbb{Q}$ and $i$ is imaginary ($i \notin \mathbb{Q}$), the real and imaginary parts must vanish independently:";

        Assert.Equal(expected, TrailingDollarFix.FixLine(input));
    }

    [Fact]
    public void TrailingDollarAfterParenFix_LeavesCorrectUsageUntouched() {
        string correct = @"Since $r_1, r_2 \in \mathbb{Q}$ and $i$ is imaginary ($i \notin \mathbb{Q}$), the real parts vanish.";
        Assert.Equal(correct, TrailingDollarFix.FixLine(correct));
    }

    [Fact]
    public void UnclosedEnvironmentEndFix_AddsMissingBrace() {
        string input = @"\end{proof";
        string expected = @"\end{proof}";
        Assert.Equal(expected, UnclosedEndFix.FixLine(input));

        string inputItemize = @"\end{itemize";
        string expectedItemize = @"\end{itemize}";
        Assert.Equal(expectedItemize, UnclosedEndFix.FixLine(inputItemize));

        string inputBegin = @"\begin{itemize";
        string expectedBegin = @"\begin{itemize}";
        Assert.Equal(expectedBegin, UnclosedEndFix.FixLine(inputBegin));
    }

    [Fact]
    public void UnclosedEnvironmentEndFix_LeavesProperlyClosedAlone() {
        string correct = @"\end{proof}";
        Assert.Equal(correct, UnclosedEndFix.FixLine(correct));

        string correctBegin = @"\begin{itemize}";
        Assert.Equal(correctBegin, UnclosedEndFix.FixLine(correctBegin));
    }

    [Fact]
    public void InlineMetaNoteMathFix_RepairsUnescapedEnglishWordsInMath() {
        string input = @"\inlinemetanote{writes $\bar{f} is surjective onto the image of $f$}";
        string expected = @"\inlinemetanote{writes $\bar{f}$ is surjective onto the image of $f$}";

        Assert.Equal(expected, InlineMetaFix.FixLine(input));
    }

    [Fact]
    public void InlineMetaNoteMathFix_ClosesUnclosedMathBeforeNoteEnds() {
        string input = @"\inlinemetanote{schreibt \qt{Fall 1: } $A \text{ endl.}}";
        string expected = @"\inlinemetanote{schreibt \qt{Fall 1: } $A \text{ endl.}$}";

        Assert.Equal(expected, InlineMetaFix.FixLine(input));
    }

    private static readonly BareHiddenOpeningFix BareHiddenFix = new();

    [Fact]
    public void BareHiddenOpeningFix_ReplacesBareHiddenCommand() {
        string input = @"\hidden";
        string expected = @"\begin{hidden}";
        Assert.Equal(expected, BareHiddenFix.FixLine(input));
    }

    [Fact]
    public void BareHiddenOpeningFix_ReplacesBareHiddenWithOpeningBrace() {
        string input = @"\hidden{";
        string expected = @"\begin{hidden}";
        Assert.Equal(expected, BareHiddenFix.FixLine(input));
    }

    [Fact]
    public void BareHiddenOpeningFix_LeavesProperEnvironmentsUntouched() {
        string beginHidden = @"\begin{hidden}";
        string endHidden = @"\end{hidden}";
        Assert.Equal(beginHidden, BareHiddenFix.FixLine(beginHidden));
        Assert.Equal(endHidden, BareHiddenFix.FixLine(endHidden));
    }
}
