using LectureExtraction.Cli.Commands;
using LectureExtraction.ConsoleUi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>Small wording helpers of the console output, as a log reader sees them.</summary>
public class OutputTextTests {
    [Theory]
    [InlineData("Warte auf Rate-Limits (Token Refill)...", "Warte auf Rate-Limits (Token Refill) (64s)")]
    [InlineData("Server-Verarbeitung", "Warte 64s: Server-Verarbeitung")]
    public void A_wait_line_says_Warte_once(string message, string expected) {
        Assert.Equal(expected, InteractiveDelay.DescribeWait(64, message));
    }

    [Theory]
    [InlineData(@"D:\out\02-18-2026-wednesday-week1-Analysis_II", "02-18-2026-wednesday-week1-Analysis_II-all-offset-copy-1", "02-18-2026-wednesday-week1-Analysis_II.pdf")]
    [InlineData(@"D:\out\05-12-25-tuesday", "05-12-25-tuesday", "05-12-25-tuesday.pdf")]
    [InlineData(@"D:\some\other-folder", "lecture", "lecture.pdf")]
    public void The_finished_pdf_is_named_after_the_lecture_folder(string folder, string baseName, string expected) {
        Assert.Equal(expected, LectureExtraction.Refinement.LatexRefinementSession.FinalPdfFileName(folder, baseName));
    }

    [Fact]
    public void A_step_file_is_shown_without_the_lecture_name_its_row_already_gives() {
        string lecture = "02-18-2026-wednesday-week1-Analysis_II";

        Assert.Equal("step2-…-all-offset-merged.tex", FinishCommand.ShortFileName($@"D:\out\{lecture}\step2-{lecture}-all-offset-merged.tex", lecture));
    }
}
