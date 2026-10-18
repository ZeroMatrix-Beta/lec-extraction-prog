using LectureExtraction.Latex.AutoFix;
using Xunit;

namespace LectureExtraction.Tests;

public class FloatInBoxFixTests {
    private static readonly FloatInBoxFix Fix = new();

    [Fact]
    public void A_table_inside_content_becomes_a_center_block_with_captionof() {
        string input = "\\begin{content}\n\\begin{table}[h!]\n\\centering\n\\begin{tabular}{cc} a & b \\end{tabular}\n\\caption{Modes}\n\\end{table}\n\\end{content}\n";
        string expected = "\\begin{content}\n\\begin{center}\n\\centering\n\\begin{tabular}{cc} a & b \\end{tabular}\n\\captionof{table}{Modes}\n\\end{center}\n\\end{content}\n";

        DocumentFixResult result = Fix.Apply(input);

        Assert.Equal(expected, result.Text);
        Assert.Single(result.Changes);
        Assert.Equal(expected, Fix.Apply(expected).Text);   // idempotent
    }

    [Fact]
    public void A_figure_nested_deeper_in_a_box_is_fixed_too() {
        string input = "\\begin{highlight}{BrickRed}\n\\begin{itemize}\\item x\n\\begin{figure}\nA\\caption{F}\n\\end{figure}\n\\end{itemize}\n\\end{highlight}";
        string result = Fix.Apply(input).Text;

        Assert.Contains("\\begin{center}\nA\\captionof{figure}{F}\n\\end{center}", result);
        Assert.DoesNotContain("figure}\nA", result.Replace("captionof{figure}", ""));
    }

    [Fact]
    public void A_float_outside_every_box_is_left_alone() {
        string input = "\\begin{table}[h]\n\\caption{Fine}\n\\end{table}\n\\begin{content}x\\end{content}";
        DocumentFixResult result = Fix.Apply(input);

        Assert.Equal(input, result.Text);
        Assert.Empty(result.Changes);
    }
}
