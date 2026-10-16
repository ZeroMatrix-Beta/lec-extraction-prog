using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>SlimOlderReferenceParts: what an older preceding part looks like in the reference context.</summary>
public class ReferenceContextSlimmerTests {
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void Speech_bodies_are_replaced_and_their_timestamps_kept() {
        string tex = Lines(@"\begin{speech}[00:00:00 - 00:02:22]", "Good morning, everybody.", "Today: metric spaces.", @"\end{speech}",
            @"\begin{content}", "$d(x,y)$", @"\end{content}");

        Assert.Equal(
            Lines(@"\begin{speech}[00:00:00 - 00:02:22]", "% tokenOpt", @"\end{speech}", @"\begin{content}", "$d(x,y)$", @"\end{content}"),
            ReferenceContextSlimmer.Slim(tex));
    }

    [Fact]
    public void Tikz_pictures_are_removed_and_the_text_around_them_stays() {
        string tex = Lines(@"\begin{center}", @"\begin{tikzpicture}", @"\draw (0,0) -- (1,1);", @"\end{tikzpicture}", @"\end{center}", @"\label{fig:a}");

        Assert.Equal(Lines(@"\begin{center}", "% tokenOpt (tikzpicture)", @"\end{center}", @"\label{fig:a}"), ReferenceContextSlimmer.Slim(tex));
    }

    [Fact]
    public void Several_blocks_are_each_replaced_on_their_own() {
        string tex = Lines(@"\begin{speech}[a]", "one", @"\end{speech}", "keep", @"\begin{speech}[b]", "two", @"\end{speech}");

        Assert.Equal(
            Lines(@"\begin{speech}[a]", "% tokenOpt", @"\end{speech}", "keep", @"\begin{speech}[b]", "% tokenOpt", @"\end{speech}"),
            ReferenceContextSlimmer.Slim(tex));
    }
}
