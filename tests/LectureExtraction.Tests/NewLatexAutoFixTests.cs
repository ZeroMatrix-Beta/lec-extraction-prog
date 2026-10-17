using System;
using System.Text.RegularExpressions;
using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

public partial class NewLatexAutoFixTests {
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

    [Fact]
    public void InlineMetaNoteMathFix_DoesNotCorruptMultipleInlineNotesAndProse() {
        string input = @"Und für ein $n$-stelliges Funktionssymbol $F \in \mathcal{L}$ \inlinemetanote{schreibt $\bullet$ \qt{Für ein $n$-stelliges}} \inlinemetanote{schreibt \qt{Funktionssymbol}} \inlinemetanote{schreibt $F \in \mathcal{L}$} und $\mathcal{L}$-Terme $\tau_1, \dots, \tau_n$ \inlinemetanote{schreibt \qt{und $\mathcal{L}$-Terme $\tau_1, \dots, \tau_n$}}: Da sagen wir einfach, die Interpretation $I(F\tau_1\dots\tau_n)$ \inlinemetanote{schreibt \qt{sei $I(F\tau_1\dots\tau_n) := $}} soll sein: unsere Funktion $F^M$ \inlinemetanote{schreibt $F^M($} --- und $F^M$ ist jetzt eine tatsächliche Funktion, die von $A^n$ nach $A$ geht, das heißt, wir können hier $n$ Elemente aus $A$ einsetzen ---, und dann nehmen wir jetzt einfach die Interpretation von $\tau_1$ bis zur Interpretation von $\tau_n$. \inlinemetanote{schreibt $I(\tau_1), \dots,$} \inlinemetanote{schreibt $I(\tau_n))$}";
        string expected = @"Und für ein $n$-stelliges Funktionssymbol $F \in \mathcal{L}$ \inlinemetanote{schreibt $\bullet$ \qt{Für ein $n$-stelliges}} \inlinemetanote{schreibt \qt{Funktionssymbol}} \inlinemetanote{schreibt $F \in \mathcal{L}$} und $\mathcal{L}$-Terme $\tau_1, \dots, \tau_n$ \inlinemetanote{schreibt \qt{und $\mathcal{L}$-Terme $\tau_1, \dots, \tau_n$}}: Da sagen wir einfach, die Interpretation $I(F\tau_1\dots\tau_n)$ \inlinemetanote{schreibt \qt{sei $I(F\tau_1\dots\tau_n) := $}} soll sein: unsere Funktion $F^M$ \inlinemetanote{schreibt $F^M($} --- und $F^M$ ist jetzt eine tatsächliche Funktion, die von $A^n$ nach $A$ geht, das heißt, wir können hier $n$ Elemente aus $A$ einsetzen ---, und dann nehmen wir jetzt einfach die Interpretation von $\tau_1$ bis zur Interpretation von $\tau_n$. \inlinemetanote{schreibt $I(\tau_1), \dots,$} \inlinemetanote{schreibt $I(\tau_n))$}";

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

    private static readonly HighlightOptionalColorFix HighlightFix = new();

    [Fact]
    public void HighlightOptionalColorFix_AddsDefaultColorWhenOmitted() {
        string input = @"\begin{highlight}[Diffie-Hellman-Schlüsselaustauschprotokoll]";
        string expected = @"\begin{highlight}{BrickRed}[Diffie-Hellman-Schlüsselaustauschprotokoll]";
        Assert.Equal(expected, HighlightFix.FixLine(input));
    }

    [Fact]
    public void HighlightOptionalColorFix_LeavesExplicitColorUntouched() {
        string input = @"\begin{highlight}{RoyalBlue}[Diffie-Hellman-Schlüsselaustauschprotokoll]";
        Assert.Equal(input, HighlightFix.FixLine(input));
    }

    private static readonly HighlightColorTintFix HighlightTintFix = new();

    [Theory]
    [InlineData(@"\begin{highlight}{BrickRed!15}[A problem]", @"\begin{highlight}{BrickRed}[A problem]")]
    [InlineData(@"\begin{highlight}{MidnightBlue!10}", @"\begin{highlight}{MidnightBlue}")]
    [InlineData(@"\begin{highlight}{BrickRed!50!black}[T]", @"\begin{highlight}{BrickRed}[T]")]
    public void HighlightColorTintFix_KeepsTheBareColorName(string input, string expected) {
        Assert.Equal(expected, HighlightTintFix.FixLine(input));
        Assert.Equal(expected, HighlightTintFix.FixLine(expected));
    }

    [Fact]
    public void HighlightColorTintFix_LeavesOtherTintsUntouched() {
        string input = @"\node[fill=BrickRed!15] at (0,0) {$x$}; \begin{highlight}{ForestGreen}[Title]";
        Assert.Equal(input, HighlightTintFix.FixLine(input));
    }

    private static readonly LeadingAngleBracketCommandFix AngleBracketFix = new();

    [Theory]
    [InlineData("    <coordinate (dcross_L1) at (7.5, -1.5);", @"    \coordinate (dcross_L1) at (7.5, -1.5);")]
    [InlineData("<draw[->] (0,0) -- (1,0);", @"\draw[->] (0,0) -- (1,0);")]
    [InlineData("  <end{tikzpicture}", @"  \end{tikzpicture}")]
    public void LeadingAngleBracketCommandFix_RestoresTheBackslash(string input, string expected) {
        Assert.Equal(expected, AngleBracketFix.FixLine(input));
        Assert.Equal(expected, AngleBracketFix.FixLine(expected));
    }

    [Theory]
    [InlineData(@"    \draw[<->] (0,0) -- (1,0);")]
    [InlineData(@"$a <b$ holds")]
    [InlineData(@"<nodes are drawn later")]
    public void LeadingAngleBracketCommandFix_LeavesOtherAngleBracketsUntouched(string input) {
        Assert.Equal(input, AngleBracketFix.FixLine(input));
    }

    [Fact]
    public void InlineMetaNoteMathFix_RemovesTrailingDollarAfterClosedNote() {
        string input = @"Zum Beispiel $Fx$. \inlinemetanote{schreibt $F x$}$ Wie schon gesagt, $x$ ist eine Variable";
        string expected = @"Zum Beispiel $Fx$. \inlinemetanote{schreibt $F x$} Wie schon gesagt, $x$ ist eine Variable";
        Assert.Equal(expected, InlineMetaFix.FixLine(input));
    }

    [Fact]
    public void InlineMetaNoteMathFix_LeavesDollarAfterNoteInsideMathUntouched() {
        // the note sits inside math, so the '$' after it closes that math: the line compiles
        string correct = @"Also $x = 1 \inlinemetanote{zeigt auf $x$}$ und weiter.";
        Assert.Equal(correct, InlineMetaFix.FixLine(correct));
    }

    [Fact]
    public void InlineMetaNoteMathFix_ClosesUnclosedCasesInsideNote() {
        string input = @"$f(\beta)$, sagen wir, das soll $0$ sein für $\beta = \alpha$, \inlinemetanote{schreibt $f(\beta) := \begin{cases} 0 & \text{für } \beta = \alpha$}";
        string expected = @"$f(\beta)$, sagen wir, das soll $0$ sein für $\beta = \alpha$, \inlinemetanote{schreibt $f(\beta) := \begin{cases} 0 & \text{für } \beta = \alpha \end{cases}$}";
        Assert.Equal(expected, InlineMetaFix.FixLine(input));
    }

    private static readonly SurplusBraceAfterMetaNoteFix SurplusBraceFix = new();

    [Fact]
    public void SurplusBraceAfterMetaNoteFix_RemovesTheBraceThatClosesNothing() {
        string input = "\\begin{speech}\n"
            + @"E sind Paare von Gästen. \inlinemetanote{schreibt $E = \{\{x, y\} \mid x \text{ und } y\}$ und darunter \qt{gesehen: $|V|$ ist gerade.}}}" + "\n"
            + "\\end{speech}\n";
        DocumentFixResult result = SurplusBraceFix.Apply(input);

        Assert.Equal(input.Replace("gerade.}}}", "gerade.}}"), result.Text);
        Assert.Equal(@"Zeile 2: überzählige } nach \inlinemetanote entfernt", Assert.Single(result.Changes));
    }

    [Fact]
    public void SurplusBraceAfterMetaNoteFix_LeavesABraceThatClosesAGroupFromAnEarlierLine() {
        // the '}' after the note closes the footnote opened one line up: it compiles
        string correct = "Satz.\\footnote{Er schreibt\n\\inlinemetanote{schreibt $x$}}\nWeiter.\n";
        DocumentFixResult result = SurplusBraceFix.Apply(correct);

        Assert.Equal(correct, result.Text);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void SurplusBraceAfterMetaNoteFix_LeavesOtherSurplusBracesToTheAiRepair() {
        string input = "Ein Satz mit \\textbf{fett}} und mehr.\n";
        Assert.Empty(SurplusBraceFix.Apply(input).Changes);
    }
}
