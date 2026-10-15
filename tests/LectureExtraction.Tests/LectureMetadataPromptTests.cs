using System;
using LectureExtraction.Extraction;
using LectureExtraction.Media;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The lecture_metadata prompt parameter. A file name without a weekday used to reach the model as
/// "day of the week (Unknown)" with no reason given, which invites a guess.
/// </summary>
public class LectureMetadataPromptTests {
    // Built directly rather than through VideoDateParser.Parse, which warns on the console for a
    // name without a date - and this class is not in the console collection.
    private static VideoDateParser.VideoDateInfo Info(DateTime date, string? weekday) =>
        new() { Date = date, Weekday = weekday, WeekdayEnglish = weekday, WeekInfo = "Week 1" };

    [Fact]
    public void A_stated_weekday_keeps_the_text_unchanged() {
        string text = AutoExtractionSession.LectureMetadataText(Info(new DateTime(2026, 2, 16), "Monday"), "Monday, 02/16/2026, Week 1", 1);

        Assert.Equal("The lecture being transcribed is from Monday, 02/16/2026, Week 1. Please note that the exact date, day of the week (Monday), and week number (Week 1) are important metadata since this is part 1 of the lecture.", text);
    }

    [Fact]
    public void A_missing_weekday_is_derived_from_the_date_and_the_model_is_told_so() {
        string text = AutoExtractionSession.LectureMetadataText(Info(new DateTime(2026, 2, 18), null), "02/18/2026, Week 1", 2);

        Assert.Equal("The lecture took place on 02/18/2026, Week 1 (Day of the week: Wednesday). The file name does not state the day of the week; Wednesday is derived from the date.", text);
    }

    [Fact]
    public void Without_weekday_and_date_the_model_is_told_not_to_guess() {
        string text = AutoExtractionSession.LectureMetadataText(Info(DateTime.MinValue, null), "lecture", 1);

        Assert.Contains("day of the week (Unknown)", text);
        Assert.Contains("Do not guess it", text);
    }
}
