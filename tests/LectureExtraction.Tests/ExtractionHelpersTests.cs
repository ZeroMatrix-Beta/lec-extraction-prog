using AutoExtraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Characterization tests for the pure helpers inside <see cref="ExtractionHelpers"/>.
/// The refactor splits this 583-line grab-bag into focused types; these tests pin the
/// behaviour that has to survive the split.
/// </summary>
public class ExtractionHelpersTests {
    // ---- CleanLatexResponse ------------------------------------------------
    // Strips the markdown fences and "[SYSTEM] ... complete" chatter that the model
    // wraps around its LaTeX, so the result can be written straight to a .tex file.

    [Fact]
    public void CleanLatexResponse_UnwrapsAFencedLatexBlock() {
        string raw = "```latex\n\\section{Intro}\n```";

        Assert.Equal("\\section{Intro}", ExtractionHelpers.CleanLatexResponse(raw));
    }

    [Fact]
    public void CleanLatexResponse_UnwrapsAFenceWithNoLanguageTag() {
        string raw = "```\n\\section{Intro}\n```";

        Assert.Equal("\\section{Intro}", ExtractionHelpers.CleanLatexResponse(raw));
    }

    [Fact]
    public void CleanLatexResponse_ConcatenatesMultipleFencedBlocks() {
        string raw = "```latex\n\\section{A}\n```\nnoise\n```latex\n\\section{B}\n```";

        string cleaned = ExtractionHelpers.CleanLatexResponse(raw);

        Assert.Contains("\\section{A}", cleaned);
        Assert.Contains("\\section{B}", cleaned);
        // Content outside the fenced blocks is discarded once any block is found.
        Assert.DoesNotContain("noise", cleaned);
    }

    [Fact]
    public void CleanLatexResponse_WithoutAnyFence_JustTrims() {
        Assert.Equal("\\section{Intro}", ExtractionHelpers.CleanLatexResponse("  \\section{Intro}  "));
    }

    [Theory]
    [InlineData("\\section{A}\n[SYSTEM] Segment complete.")]
    [InlineData("\\section{A}\n**[SYSTEM] Segment complete.**")]
    [InlineData("\\section{A}\n%[AI-MODEL] Video complete")]
    [InlineData("\\section{A}\n   [SYSTEM] segment  COMPLETE now")]
    public void CleanLatexResponse_StripsCompletionChatter(string raw) {
        Assert.Equal("\\section{A}", ExtractionHelpers.CleanLatexResponse(raw));
    }

    /// <summary>
    /// Documents a known gap rather than a desired behaviour: SystemMessageRegex allows
    /// leading <c>* _ %</c> markers only when they sit flush against the bracket
    /// (<c>%[SYSTEM]</c>). A LaTeX comment written the normal way, <c>% [SYSTEM]</c>
    /// with a space, is therefore NOT stripped and leaks into the .tex output.
    /// If the regex is ever widened, this test should start failing — update it then.
    /// </summary>
    [Fact]
    public void CleanLatexResponse_DoesNotStripChatter_BehindASpacedLatexCommentMarker() {
        const string raw = "\\section{A}\n% [AI-MODEL] Video complete";

        Assert.Equal(raw, ExtractionHelpers.CleanLatexResponse(raw));
    }

    [Fact]
    public void CleanLatexResponse_AlsoRepairsMalformedEndTags() {
        string raw = "```latex\n\\begin{equation}x=1\\end{equation>\n```";

        Assert.Equal("\\begin{equation}x=1\\end{equation}", ExtractionHelpers.CleanLatexResponse(raw));
    }

    // ---- CleanCopySuffix ---------------------------------------------------
    // Windows Explorer and the FFmpeg step both produce "- Kopie" / "-Copy" names.

    [Theory]
    [InlineData("lecture - Kopie", "lecture")]
    [InlineData("lecture-Copy", "lecture")]
    [InlineData("lecture - copy", "lecture")]
    [InlineData("lecture", "lecture")]
    [InlineData("", "")]
    public void CleanCopySuffix_RemovesGermanAndEnglishCopyMarkers(string input, string expected) {
        Assert.Equal(expected, ExtractionHelpers.CleanCopySuffix(input));
    }

    // ---- NormalizeRelativePath --------------------------------------------
    // Produces the "./a/b.tex" form used inside the generated markdown file trees.

    [Theory]
    [InlineData("sub\\file.tex", "./sub/file.tex")]
    [InlineData("sub/file.tex", "./sub/file.tex")]
    [InlineData("./sub/file.tex", "./sub/file.tex")]
    [InlineData("/abs/file.tex", "./abs/file.tex")]
    public void NormalizeRelativePath_ProducesForwardSlashedDotPrefixedPaths(string input, string expected) {
        Assert.Equal(expected, ExtractionHelpers.NormalizeRelativePath(input));
    }

    [Fact]
    public void NormalizeRelativePath_AlsoStripsCopySuffixes() {
        Assert.Equal("./lecture.tex", ExtractionHelpers.NormalizeRelativePath("lecture - Kopie.tex"));
    }

    // ---- FindCommonBaseDirectory ------------------------------------------

    [Fact]
    public void FindCommonBaseDirectory_OfSiblingFiles_IsTheirSharedFolder() {
        string folder = Path.Combine(Path.GetTempPath(), "lec-test", "a", "b");
        List<string> paths = [Path.Combine(folder, "1.tex"), Path.Combine(folder, "2.tex")];

        Assert.Equal(folder, ExtractionHelpers.FindCommonBaseDirectory(paths));
    }

    [Fact]
    public void FindCommonBaseDirectory_WalksUpUntilEveryPathIsCovered() {
        string root = Path.Combine(Path.GetTempPath(), "lec-test", "a");
        List<string> paths = [
            Path.Combine(root, "b", "1.tex"),
            Path.Combine(root, "c", "2.tex")
        ];

        Assert.Equal(root, ExtractionHelpers.FindCommonBaseDirectory(paths));
    }

    [Fact]
    public void FindCommonBaseDirectory_OfNothing_IsNull() {
        Assert.Null(ExtractionHelpers.FindCommonBaseDirectory([]));
    }
}
