using LectureExtraction.Latex;

namespace LectureExtraction.Tests;

/// <summary>
/// Characterization tests for <see cref="LatexResponseCleaner"/>.
/// </summary>
public class LatexResponseCleanerTests {
    // ---- CleanLatexResponse ------------------------------------------------
    // Strips the markdown fences and "[SYSTEM] ... complete" chatter that the model
    // wraps around its LaTeX, so the result can be written straight to a .tex file.

    [Fact]
    public void CleanLatexResponse_UnwrapsAFencedLatexBlock() {
        string raw = "```latex\n\\section{Intro}\n```";

        Assert.Equal("\\section{Intro}", LatexResponseCleaner.CleanLatexResponse(raw));
    }

    [Fact]
    public void CleanLatexResponse_UnwrapsAFenceWithNoLanguageTag() {
        string raw = "```\n\\section{Intro}\n```";

        Assert.Equal("\\section{Intro}", LatexResponseCleaner.CleanLatexResponse(raw));
    }

    [Fact]
    public void CleanLatexResponse_ConcatenatesMultipleFencedBlocks() {
        string raw = "```latex\n\\section{A}\n```\nnoise\n```latex\n\\section{B}\n```";

        string cleaned = LatexResponseCleaner.CleanLatexResponse(raw);

        Assert.Contains("\\section{A}", cleaned);
        Assert.Contains("\\section{B}", cleaned);
        // Content outside the fenced blocks is discarded once any block is found.
        Assert.DoesNotContain("noise", cleaned);
    }

    [Fact]
    public void CleanLatexResponse_WithoutAnyFence_JustTrims() {
        Assert.Equal("\\section{Intro}", LatexResponseCleaner.CleanLatexResponse("  \\section{Intro}  "));
    }

    [Theory]
    [InlineData("\\section{A}\n[SYSTEM] Segment complete.")]
    [InlineData("\\section{A}\n**[SYSTEM] Segment complete.**")]
    [InlineData("\\section{A}\n%[AI-MODEL] Video complete")]
    [InlineData("\\section{A}\n   [SYSTEM] segment  COMPLETE now")]
    public void CleanLatexResponse_StripsCompletionChatter(string raw) {
        Assert.Equal("\\section{A}", LatexResponseCleaner.CleanLatexResponse(raw));
    }

    /// <summary>
    /// SystemMessageRegex used to only allow leading <c>* _ %</c> markers flush against the
    /// bracket (<c>%[SYSTEM]</c>), so a LaTeX comment written the normal way, <c>% [SYSTEM]</c>
    /// with a space, leaked into the .tex output. Fixed by allowing whitespace between the
    /// marker and the bracket.
    /// </summary>
    [Fact]
    public void CleanLatexResponse_StripsChatter_BehindASpacedLatexCommentMarker() {
        const string raw = "\\section{A}\n% [AI-MODEL] Video complete";

        Assert.Equal("\\section{A}", LatexResponseCleaner.CleanLatexResponse(raw));
    }

    [Fact]
    public void CleanLatexResponse_AlsoRepairsMalformedEndTags() {
        string raw = "```latex\n\\begin{equation}x=1\\end{equation>\n```";

        Assert.Equal("\\begin{equation}x=1\\end{equation}", LatexResponseCleaner.CleanLatexResponse(raw));
    }
}
