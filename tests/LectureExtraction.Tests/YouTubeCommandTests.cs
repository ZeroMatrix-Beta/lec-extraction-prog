using LectureExtraction.Cli.Commands;
using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// <c>lecx youtube</c> builds the menu's task from flags: the video id from any common link shape
/// (a playlist link must not leak its list into the request), and the menu's fragments from the duration.
/// </summary>
public class YouTubeCommandTests {
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=IPfi49IbHNU&list=PLj7p5OoL6vGzu4nnDWPFgFyg_pelicDN8")]
    [InlineData("https://www.youtube.com/watch?list=PLj7p5OoL6vGzu4nnDWPFgFyg_pelicDN8&v=IPfi49IbHNU&index=2")]
    [InlineData("https://youtu.be/IPfi49IbHNU?si=abc")]
    [InlineData("https://www.youtube.com/shorts/IPfi49IbHNU")]
    [InlineData("https://www.youtube.com/live/IPfi49IbHNU")]
    [InlineData("IPfi49IbHNU")]
    public void ExtractVideoId_FindsTheIdInEveryLinkShape(string url) {
        Assert.Equal("IPfi49IbHNU", YouTubeCommand.ExtractVideoId(url));
    }

    [Theory]
    [InlineData("https://www.youtube.com/playlist?list=PLj7p5OoL6vGzu4nnDWPFgFyg_pelicDN8")]
    [InlineData("not a link")]
    [InlineData("")]
    public void ExtractVideoId_RejectsLinksWithoutAVideo(string url) {
        Assert.Null(YouTubeCommand.ExtractVideoId(url));
    }

    [Theory]
    [InlineData("90", 5400)]
    [InlineData("47.5", 2850)]
    [InlineData("1:35:00", 5700)]
    [InlineData("45:30", 2730)]
    [InlineData("abc", 0)]
    public void ParseDuration_ReadsMinutesAndClockTimes(string input, double seconds) {
        Assert.Equal(seconds, YouTubeTaskPrompt.ParseDuration(input));
    }

    [Fact]
    public void BuildFragments_OverlapsAndEndsAtTheVideoEnd() {
        var fragments = YouTubeTaskPrompt.BuildFragments(5700, YouTubeTaskPrompt.DefaultPartCount(5700), 240);

        Assert.Equal(3, fragments.Count);
        Assert.Equal("00:00:00", fragments[0].StartTime);
        Assert.Equal("00:34:20", fragments[0].EndTime);
        Assert.Equal("00:30:20", fragments[1].StartTime);
        Assert.Equal("01:35:00", fragments[2].EndTime);
    }

    [Fact]
    public void BuildFragments_KeepsAShortVideoWhole() {
        var fragments = YouTubeTaskPrompt.BuildFragments(1800, YouTubeTaskPrompt.DefaultPartCount(1800), 240);

        Assert.Single(fragments);
        Assert.Equal("00:30:00", fragments[0].EndTime);
    }
}
