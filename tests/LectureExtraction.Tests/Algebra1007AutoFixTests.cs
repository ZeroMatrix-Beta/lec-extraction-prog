using LectureExtraction.Latex;
using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The lines that broke the 2026-10-07 algebra lecture: '\mapsfrom' in step 4, and in AI repair round 1
/// '\sub' plus two formulas whose closing '$' went missing before prose. Correct LaTeX - including the
/// repaired forms - must pass through untouched.
/// </summary>
public class Algebra1007AutoFixTests {
    private static readonly UndefinedMathCommandFix Commands = new();
    private static readonly UnclosedInlineMathBeforeProseFix Prose = new();

    [Fact]
    public void Replaces_mapsfrom_with_a_reflected_mapsto() {
        string line = @"    \operatorname{Id}_K(R) \xleftrightarrow{\quad \text{bijection} \quad} \operatorname{Id}(R/K), \quad I \mapsto q(I) = I/K, \quad q^{-1}(J) \mapsfrom J.";

        Assert.Equal(
            @"    \operatorname{Id}_K(R) \xleftrightarrow{\quad \text{bijection} \quad} \operatorname{Id}(R/K), \quad I \mapsto q(I) = I/K, \quad q^{-1}(J) \mathrel{\reflectbox{$\mapsto$}} J.",
            Commands.FixLine(line));
        Assert.Equal(@"$J \mathrel{\reflectbox{$\longmapsto$}} I$", Commands.FixLine(@"$J \longmapsfrom I$"));
    }

    [Fact]
    public void Replaces_sub_with_subset() {
        string line = @"\inlinemetanote{writes $\operatorname{Id}_K(R) = \{ I \subset R \mid K \sub I, I \text{ is an ideal} \}$}.";

        Assert.Equal(
            @"\inlinemetanote{writes $\operatorname{Id}_K(R) = \{ I \subset R \mid K \subset I, I \text{ is an ideal} \}$}.",
            Commands.FixLine(line));
    }

    // Step 4 of the Analysis II lecture of 2026-02-23 (3.8 rerun of 2026-10-25), line 1965.
    [Fact]
    public void Replaces_marvosym_Lightning_with_stmaryrd_lightning() {
        string line = @"\text{\Large\textbf{\Lightning}} \qquad \left( 0 < \frac{r(x)}{4} \le r(x_{n_\ell}) \to 0 \right)";

        Assert.Equal(
            @"\text{\Large\textbf{\ensuremath{\lightning}}} \qquad \left( 0 < \frac{r(x)}{4} \le r(x_{n_\ell}) \to 0 \right)",
            Commands.FixLine(line));
        Assert.Equal(@"so $r \to 0$, \ensuremath{\lightning}.", Commands.FixLine(@"so $r \to 0$, \Lightning."));
    }

    [Theory]
    [InlineData(@"a contradiction $\lightning$")]
    [InlineData(@"\ensuremath{\lightning}")]
    [InlineData(@"\LightningBolt is not ours")]
    [InlineData(@"$K \subset I \subseteq R \subsetneq S$")]
    [InlineData(@"\subsection{Sub-ideals}")]
    [InlineData(@"\subsubsection{Well-Definedness}")]
    [InlineData(@"q^{-1}(J) \mathrel{\reflectbox{$\mapsto$}} J")]
    [InlineData(@"first line\\sub-second line")]
    public void Leaves_defined_commands_alone(string line) {
        Assert.Equal(line, Commands.FixLine(line));
    }

    [Fact]
    public void Closes_a_formula_that_runs_into_prose_and_shifts_the_rest_of_the_line() {
        string line = @"Once you have $x and negatives, you can get zero; zero is in it anyway. Yeah, then you can add $x$ and $-x$ and get zero. Let me say here: we also know that zero \inlinemetanote{writes $0 \in f^{-1}(J)$}... $f(0) = 0 \in J$ \inlinemetanote{writes $f(0) = 0 \in J$}, because zero goes to zero.";

        Assert.Equal(
            @"Once you have $x$ and negatives, you can get zero; zero is in it anyway. Yeah, then you can add $x$ and $-x$ and get zero. Let me say here: we also know that zero \inlinemetanote{writes $0 \in f^{-1}(J)$}... $f(0) = 0 \in J$ \inlinemetanote{writes $f(0) = 0 \in J$}, because zero goes to zero.",
            Prose.FixLine(line));
    }

    [Fact]
    public void Closes_the_last_formula_of_a_line_before_the_comma_that_starts_the_prose() {
        string line = @"    \item \textbf{Right Arrow ($\rightarrow$):} We must prove that for any ideal $I \in \operatorname{Id}_K(R), the forward image:";

        Assert.Equal(
            @"    \item \textbf{Right Arrow ($\rightarrow$):} We must prove that for any ideal $I \in \operatorname{Id}_K(R)$, the forward image:",
            Prose.FixLine(line));
    }

    [Theory]
    [InlineData(@"Since $f \text{ is a ring homomorphism}$, the image $\operatorname{Im}(f)$ is a subring.")]
    [InlineData(@"The set $\{ x \in R \mid")]
    [InlineData(@"$q(I) \subset R/K \quad \text{is an ideal of } R/K$ and $\bar{x} \in q(I)$, odd \$ sign")]
    [InlineData(@"Once you have $x$ and negatives, you can add $x$ and $-x$ and get zero.")]
    [InlineData(@"\[ \operatorname{Id}_K(R) \longleftrightarrow \operatorname{Id}(R/K) \]")]
    public void Leaves_balanced_or_prose_free_formulas_alone(string line) {
        Assert.Equal(line, Prose.FixLine(line));
    }

    [Fact]
    public void The_whole_fixer_repairs_all_four_lines_and_a_second_run_changes_nothing() {
        string tex = string.Join("\n",
            @"\quad q^{-1}(J) \mapsfrom J.",
            @"\inlinemetanote{writes $\operatorname{Id}_K(R) = \{ I \subset R \mid K \sub I, I \text{ is an ideal} \}$}.",
            @"Once you have $x and negatives, you can add $x$ and $-x$ and get zero.",
            @"We must prove that for any ideal $I \in \operatorname{Id}_K(R), the forward image:");

        var first = LatexAutoFixer.Apply(tex);
        var second = LatexAutoFixer.Apply(first.Text);

        Assert.Equal(4, first.Changes.Count);
        Assert.False(second.Changed);
    }
}
