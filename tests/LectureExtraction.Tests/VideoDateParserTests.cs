using LectureExtraction.Media;

namespace LectureExtraction.Tests;

/// <summary>
/// Characterization tests for <see cref="VideoDateParser"/>. These pin the *current* behaviour
/// of the filename parser so that moving and renaming it during the refactor cannot change it.
/// </summary>
public class VideoDateParserTests {
    [Theory]
    // MM-DD-YYYY, the format the main-menu warning text advertises.
    [InlineData("02-16-2026-monday-week1-Analysis_II.mp4", 2026, 2, 16)]
    // The same date with the week marker leading, the second advertised format.
    [InlineData("week1-02-16-2026-montag.mp4", 2026, 2, 16)]
    // ISO order is preferred over MM-DD-YYYY because YyyyMmDdRegex is matched first.
    [InlineData("2026-02-16-lecture.mp4", 2026, 2, 16)]
    // A two-digit year is expanded into the 2000s.
    [InlineData("02-16-26-monday.mp4", 2026, 2, 16)]
    public void Parse_ExtractsDate_FromSupportedFilenameFormats(string fileName, int year, int month, int day) {
        var info = VideoDateParser.Parse(fileName);

        Assert.True(info.IsValid);
        Assert.Equal(new DateTime(year, month, day), info.Date);
        Assert.Equal($"{year:D4}-{month:D2}-{day:D2}", info.DateString);
    }

    [Fact]
    public void Parse_WithoutYear_FallsBackToCurrentYear() {
        var info = VideoDateParser.Parse("03-15-lecture.mp4");

        Assert.True(info.IsValid);
        Assert.Equal(DateTime.Now.Year, info.Date.Year);
        Assert.Equal(3, info.Date.Month);
        Assert.Equal(15, info.Date.Day);
    }

    [Theory]
    [InlineData("02-16-2026-monday.mp4", "Monday", "Monday")]
    [InlineData("02-16-2026-montag.mp4", "Montag", "Monday")]
    [InlineData("02-16-2026-dienstag.mp4", "Dienstag", "Tuesday")]
    [InlineData("02-16-2026-mittwoch.mp4", "Mittwoch", "Wednesday")]
    [InlineData("02-16-2026-donnerstag.mp4", "Donnerstag", "Thursday")]
    [InlineData("02-16-2026-freitag.mp4", "Freitag", "Friday")]
    [InlineData("02-16-2026-samstag.mp4", "Samstag", "Saturday")]
    [InlineData("02-16-2026-sonntag.mp4", "Sonntag", "Sunday")]
    public void Parse_MapsGermanWeekdays_ToTheirEnglishEquivalent(string fileName, string weekday, string english) {
        var info = VideoDateParser.Parse(fileName);

        Assert.Equal(weekday, info.Weekday);
        Assert.Equal(english, info.WeekdayEnglish);
    }

    [Theory]
    [InlineData("week1-02-16-2026.mp4", 1, "Week 1")]
    [InlineData("woche3-02-16-2026.mp4", 3, "Week 3 (Woche 3)")]
    [InlineData("02-16-2026-week12-lecture.mp4", 12, "Week 12")]
    public void Parse_ExtractsWeekNumber_AndLabelsGermanWeeksBilingually(string fileName, int number, string label) {
        var info = VideoDateParser.Parse(fileName);

        Assert.Equal(number, info.WeekNumber);
        Assert.Equal(label, info.WeekInfo);
    }

    [Fact]
    public void Parse_WithNoRecognisableTokens_IsInvalidAndKeepsFilenameAsDateString() {
        var info = VideoDateParser.Parse("random-video.mp4");

        Assert.False(info.IsValid);
        Assert.Equal(DateTime.MinValue, info.Date);
        Assert.Equal("random-video", info.DateString);
    }

    [Fact]
    public void Parse_WithOutOfRangeDateValues_DegradesToInvalidInsteadOfThrowing() {
        // Month 13 / day 45 match the MM-DD-YYYY shape but cannot form a DateTime.
        var info = VideoDateParser.Parse("13-45-2026-lecture.mp4");

        Assert.Equal(DateTime.MinValue, info.Date);
        Assert.Equal("13-45-2026-lecture", info.DateString);
    }

    [Fact]
    public void GetFormattedContext_JoinsWeekdayDateAndWeek_InThatOrder() {
        var info = VideoDateParser.Parse("02-16-2026-montag-week1.mp4");

        // German weekday is rendered as "English (German)" when the two differ.
        Assert.Equal("Monday (Montag), 2026-02-16, Week 1", info.GetFormattedContext());
    }

    [Fact]
    public void GetFormattedContext_WithNothingParsed_FallsBackToTheDateString() {
        var info = VideoDateParser.Parse("random-video.mp4");

        Assert.Equal("random-video", info.GetFormattedContext());
    }
}
