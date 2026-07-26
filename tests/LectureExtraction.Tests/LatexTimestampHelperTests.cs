using DocumentUtilities;

namespace LectureExtraction.Tests;

/// <summary>
/// Characterization tests for <see cref="LatexTimestampHelper"/>, the logic that shifts
/// per-segment timestamps into whole-lecture time when the "-offset" files are generated.
/// </summary>
public class LatexTimestampHelperTests {
    [Fact]
    public void ExtractContentWithoutTimestampHeader_RemovesThePartStartComment() {
        string withHeader = "% PART_START_SECONDS: 120\n\\section{Intro}";

        Assert.Equal("\\section{Intro}", LatexTimestampHelper.ExtractContentWithoutTimestampHeader(withHeader));
    }

    [Fact]
    public void ExtractContentWithoutTimestampHeader_HandlesFractionalSeconds() {
        string withHeader = "% PART_START_SECONDS: 120.5\n\\section{Intro}";

        Assert.Equal("\\section{Intro}", LatexTimestampHelper.ExtractContentWithoutTimestampHeader(withHeader));
    }

    [Fact]
    public void ExtractContentWithoutTimestampHeader_WithoutAHeader_OnlyTrimsLeadingWhitespace() {
        Assert.Equal("\\section{Intro}", LatexTimestampHelper.ExtractContentWithoutTimestampHeader("  \n\\section{Intro}"));
    }

    [Fact]
    public void AdjustTimestamps_ShiftsBothEndsOfASpokenCleanRange() {
        string input = "\\begin{spoken-clean}[00:01:00 - 00:02:00]";

        string shifted = LatexTimestampHelper.AdjustTimestamps(input, 3600);

        Assert.Equal("\\begin{spoken-clean}[01:01:00 - 01:02:00]", shifted);
    }

    [Fact]
    public void AdjustTimestamps_CarriesSecondsIntoMinutes() {
        string input = "\\begin{spoken-clean}[00:00:50 - 00:01:10]";

        string shifted = LatexTimestampHelper.AdjustTimestamps(input, 20);

        Assert.Equal("\\begin{spoken-clean}[00:01:10 - 00:01:30]", shifted);
    }

    [Fact]
    public void AdjustTimestamps_ShiftsEveryOccurrence() {
        string input = "\\begin{spoken-clean}[00:00:00 - 00:00:30]\ntext\n\\begin{spoken-clean}[00:00:30 - 00:01:00]";

        string shifted = LatexTimestampHelper.AdjustTimestamps(input, 60);

        Assert.Contains("[00:01:00 - 00:01:30]", shifted);
        Assert.Contains("[00:01:30 - 00:02:00]", shifted);
    }

    [Fact]
    public void AdjustTimestamps_WithZeroOffset_ReturnsInputUnchanged() {
        string input = "\\begin{spoken-clean}[00:01:00 - 00:02:00]";

        Assert.Same(input, LatexTimestampHelper.AdjustTimestamps(input, 0));
    }

    [Fact]
    public void AdjustTimestamps_LeavesUnrelatedLatexUntouched() {
        string input = "\\section{Intro}\n\\begin{equation}x = 1\\end{equation}";

        Assert.Equal(input, LatexTimestampHelper.AdjustTimestamps(input, 500));
    }
}
