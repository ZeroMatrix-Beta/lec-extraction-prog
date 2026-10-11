using System.Collections.Generic;
using LectureExtraction.Latex;
using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The runner around the individual fixes: what it shares with all of them (comments out of reach,
/// line endings, the report, fix order) and that a new fix plugs in without touching it. The fixes'
/// own behaviour is tested in their own classes.
/// </summary>
public class LatexAutoFixerTests {
    [Fact]
    public void Repairs_all_six_broken_lines_of_the_09_30_lecture_and_reports_each() {
        string tex = string.Join("\n",
            @"\section{Units in Polynomial Rings}",
            @"\begin{theorem}[Units in $R[x]$ over an Integral Domain]\label[theorem]{thm:units-poly-integral-domain}",
            @"\begin{content}[{Series Representation and Addition in \texorpdfstring{$R[[x]]$}{R[[x]}}}]",
            @"\begin{content}[{Cauchy Product Multiplication in \texorpdfstring{$R[[x]]$}{R[[x]}}}]",
            @"\begin{content}[{Ring Structure of \texorpdfstring{$R[[x]]$}{R[[x]}}}]",
            @"\begin{content}[{The Problem of Invertibility in \texorpdfstring{$R[[x]]$}{R[[x]}}}]",
            @"\begin{content}[{Classification of Units in \texorpdfstring{$R[[x]]$}{R[[x]}}}]",
            @"Plain text with $R[[x]]$ stays.");

        var result = LatexAutoFixer.Apply(tex);

        Assert.Equal(6, result.Changes.Count);
        Assert.DoesNotContain(@"{R[[x]}}}", result.Text);
        Assert.Contains(@"\begin{theorem}[{Units in $R[x]$ over an Integral Domain}]", result.Text);
        Assert.StartsWith("Zeile 2: optionales Argument", result.Changes[0]);
        Assert.StartsWith(@"Zeile 3: \texorpdfstring-Klartext", result.Changes[1]);
    }

    [Fact]
    public void Runs_the_fixes_in_order_so_one_repair_unmasks_the_next() {
        // The surplus brace hides the nested brackets from the optional-argument fix until the
        // \texorpdfstring fix has removed it.
        string line = @"\begin{remark}[Units of $R[x]$ in \texorpdfstring{$R[[x]]$}{R[[x]}}]";

        var result = LatexAutoFixer.Apply(line);

        Assert.Equal(@"\begin{remark}[{Units of $R[x]$ in \texorpdfstring{$R[[x]]$}{R[[x]]}}]", result.Text);
        Assert.Contains(@"\texorpdfstring-Klartext repariert, optionales Argument", result.Changes[0]);
    }

    [Fact]
    public void Never_touches_a_comment_but_still_fixes_the_code_before_it() {
        string tex = @"Text % \begin{theorem}[Units in $R[x]$]" + "\n" + @"\begin{theorem}[Units in $R[x]$] % about $R[x]$";

        var result = LatexAutoFixer.Apply(tex);

        Assert.Equal(@"Text % \begin{theorem}[Units in $R[x]$]" + "\n" + @"\begin{theorem}[{Units in $R[x]$}] % about $R[x]$", result.Text);
        Assert.Single(result.Changes);
    }

    [Fact]
    public void Applying_twice_changes_nothing_the_second_time() {
        string tex = @"\begin{theorem}[Units in $R[x]$]" + "\n" + @"\begin{content}[{Ring of \texorpdfstring{$R[[x]]$}{R[[x]}}}]";

        var first = LatexAutoFixer.Apply(tex);
        var second = LatexAutoFixer.Apply(first.Text);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void Keeps_windows_line_endings() {
        var result = LatexAutoFixer.Apply("\\begin{theorem}[Units in $R[x]$]\r\nNext line\r\n");

        Assert.Equal("\\begin{theorem}[{Units in $R[x]$}]\r\nNext line\r\n", result.Text);
    }

    [Fact]
    public void Leaves_a_document_without_known_errors_untouched() {
        string tex = "\\section{Rings}\n\\begin{definition}[Ring]\nA ring $R$ with $R[x]$.\n\\end{definition}\n";

        var result = LatexAutoFixer.Apply(tex);

        Assert.False(result.Changed);
        Assert.Equal(tex, result.Text);
    }

    [Fact]
    public void Runs_document_fixes_after_line_fixes_and_reports_both() {
        string tex = string.Join("\n",
            @"\begin{speech}[00:40:59 - 00:41:32]",
            "What about a subring? Yeah?",
            @"\end{student}",
            @"\begin{theorem}[Units in $R[x]$]",
            "x",
            @"\end{theorem}");

        var result = LatexAutoFixer.Apply(tex);

        Assert.Contains("Yeah?\n\\end{speech}\n", result.Text);
        Assert.Contains(@"\begin{theorem}[{Units in $R[x]$}]", result.Text);
        Assert.StartsWith("Zeile 4: optionales Argument", result.Changes[0]);
        Assert.StartsWith(@"Zeile 3: \end{student} durch \end{speech} ersetzt", result.Changes[1]);
    }

    [Fact]
    public void A_new_fix_plugs_in_without_changing_the_runner() {
        var fixes = new List<ILatexLineFix> { new ReplaceFix("\\bf{", "\\textbf{") };

        var result = LatexAutoFixer.Apply("Some \\bf{bold} text % \\bf{in a comment}", fixes);

        Assert.Equal("Some \\textbf{bold} text % \\bf{in a comment}", result.Text);
        Assert.Equal("Zeile 1: ersetzt \\bf{: Some \\textbf{bold} text % \\bf{in a comment}", result.Changes[0]);
    }

    private sealed class ReplaceFix(string from, string to) : ILatexLineFix {
        public string Description => $"ersetzt {from}";
        public string FixLine(string code) => code.Replace(from, to);
    }
}
