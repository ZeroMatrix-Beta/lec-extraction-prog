using System;
using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// An \end written under the wrong name - the real 09-22 algebra failure ("\begin{speech} on input
/// line 1011 ended by \end{student}"). The fix is deliberately narrow, so half of these tests pin what
/// it must NOT do: missing, run-over and stray ends stay as they are (LatexDiagnostics reports them to
/// the AI repair). Some tests flip the static switches, so the class runs in the non-parallel
/// AutoFixSettings collection.
/// </summary>
[Collection(AutoFixSettingsCollection.Name)]
public sealed class MisnamedEnvironmentEndFixTests : IDisposable {
    private static readonly MisnamedEnvironmentEndFix Fix = new();

    public void Dispose() => MisnamedEnvironmentEndFix.RestoreDefaults();

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static readonly string Lecture0922 = Lines(
        @"\begin{speech}[00:40:59 - 00:41:32]",
        "What about a subring? Is it a subring? Yeah?",
        @"\end{student}",
        "",
        @"\begin{student}[Student Answer]",
        @"No, because we don't have the $1$.",
        @"\end{student}");

    [Fact]
    public void Renames_the_misnamed_end_from_the_09_22_lecture() {
        DocumentFixResult result = Fix.Apply(Lecture0922);

        Assert.Equal(Lecture0922.Replace("Yeah?\n\\end{student}", "Yeah?\n\\end{speech}"), result.Text);
        Assert.Equal(@"Zeile 3: \end{student} durch \end{speech} ersetzt (schliesst \begin{speech} aus Zeile 1)", Assert.Single(result.Changes));
    }

    [Fact]
    public void Renames_inside_nested_structure_and_keeps_the_rest_of_the_line() {
        string tex = Lines(@"\begin{content}", @"\begin{theorem}[T]", @"x \end{lemma} y", @"\end{content}");

        DocumentFixResult result = Fix.Apply(tex);

        Assert.Contains(@"x \end{theorem} y", result.Text);
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time() {
        DocumentFixResult first = Fix.Apply(Lecture0922);

        Assert.Empty(Fix.Apply(first.Text).Changes);
    }

    // --- Must stay untouched: not this error class ---

    [Theory]
    [InlineData("\\begin{content}\n\\begin{itemize}\n\\end{content}\n")]           // run-over end: missing \end{itemize}
    [InlineData("\\begin{content}[Last board]\nMath.\n")]                            // never closed
    [InlineData("\\begin{speech}\nWords.\n\\begin{content}\n\\end{content}\n")]      // root container never closed
    [InlineData("\\begin{speech}\nWords.\n\\end{speech}\n\\end{proof}\nAfter.\n")]   // stray end, nothing open
    public void Leaves_missing_run_over_and_stray_ends_to_the_ai_repair(string tex) {
        DocumentFixResult result = Fix.Apply(tex);

        Assert.Same(tex, result.Text);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Does_not_rename_when_the_open_environment_is_closed_later() {
        // The inner \end{student} is stray, but content does get its own \end - renaming would break it.
        string tex = Lines(@"\begin{content}", "x", @"\end{student}", "y", @"\end{content}");

        Assert.Empty(Fix.Apply(tex).Changes);
    }

    [Fact]
    public void Leaves_a_balanced_document_alone() {
        string tex = Lines(@"\begin{speech}", "Words.", @"\end{speech}", @"\begin{content}[Board]", @"\begin{theorem}[T]", "x", @"\end{theorem}", @"\end{content}");

        Assert.Same(tex, Fix.Apply(tex).Text);
    }

    [Fact]
    public void Ignores_environments_in_comments_and_verbatim_content() {
        string tex = Lines(@"\begin{speech}", @"% \end{student} only mentioned", @"\begin{verbatim}", @"\end{student}", @"\end{verbatim}", @"\end{speech}");

        Assert.Empty(Fix.Apply(tex).Changes);
    }

    // --- Switches ---

    [Fact]
    public void A_switched_off_category_is_not_closed_by_renaming() {
        MisnamedEnvironmentEndFix.CloseRootContainers = false;

        DocumentFixResult result = Fix.Apply(Lecture0922);

        Assert.Same(Lecture0922, result.Text);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Each_category_maps_to_its_own_switch() {
        MisnamedEnvironmentEndFix.CloseProofWrappers = false;
        MisnamedEnvironmentEndFix.CloseTheoremEnvironments = false;

        Assert.False(MisnamedEnvironmentEndFix.MayClose("proofWrapper"));
        Assert.False(MisnamedEnvironmentEndFix.MayClose("lemma"));
        Assert.True(MisnamedEnvironmentEndFix.MayClose("proof"));
        Assert.True(MisnamedEnvironmentEndFix.MayClose("speech"));
        Assert.True(MisnamedEnvironmentEndFix.MayClose("align"));
    }

    [Fact]
    public void A_switched_off_proof_is_not_closed_by_renaming() {
        MisnamedEnvironmentEndFix.CloseProofs = false;
        string tex = Lines(@"\begin{content}", @"\begin{proof}", "x", @"\end{lemma}", @"\end{content}");

        Assert.Empty(Fix.Apply(tex).Changes);
    }

    [Fact]
    public void RestoreDefaults_switches_everything_back_on() {
        MisnamedEnvironmentEndFix.CloseRootContainers = false;
        MisnamedEnvironmentEndFix.CloseProofs = false;
        MisnamedEnvironmentEndFix.CloseOtherEnvironments = false;

        MisnamedEnvironmentEndFix.RestoreDefaults();

        Assert.True(MisnamedEnvironmentEndFix.CloseRootContainers);
        Assert.True(MisnamedEnvironmentEndFix.CloseProofs);
        Assert.True(MisnamedEnvironmentEndFix.CloseOtherEnvironments);
    }
}
