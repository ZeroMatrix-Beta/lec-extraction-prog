using LectureExtraction.Configuration;
using LectureExtraction.Extraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the per-fragment prompt construction that moved out of
/// <c>AiStudioAutoExtractionSession.ProcessYouTubeTasksAsync</c> when
/// <see cref="YouTubeTaskRunner"/> was extracted (Phase 11).
///
/// <para>This text goes straight into a paid request and shapes what the model transcribes, but it
/// was previously buried mid-method in a 90-line loop and unreachable from a test. Pulling the
/// runner out made it a pure function; these tests pin it.</para>
/// </summary>
public class YouTubeTaskRunnerTests {
    private static YouTubeTimestampFragment Fragment(string start = "00:00", string end = "10:00", string title = "Intro") =>
        new() { StartTime = start, EndTime = end, PartTitle = title };

    [Fact]
    public void FragmentPrompt_CarriesTheFragmentBoundsAndTitle() {
        string prompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment("05:00", "15:30", "Beweis"), partNum: 2);

        Assert.Contains("05:00", prompt);
        Assert.Contains("15:30", prompt);
        Assert.Contains("Beweis", prompt);
        // The model must transcribe only the requested window, not the whole video.
        Assert.Contains("ONLY", prompt);
    }

    [Fact]
    public void FirstPart_IsToldTheDateMatters() {
        string prompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 1);

        Assert.Contains("part 1 of the lecture, the date of the transcription is important", prompt);
    }

    [Fact]
    public void LaterParts_AreToldTheDateMattersLessButStillStateIt() {
        string prompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 3);

        // Without the "(but tell it anyway)" half the model omits the date entirely on later parts;
        // without the "not so important" half it repeats a full date header on every part.
        Assert.Contains("not so important", prompt);
        Assert.Contains("but tell it anyway", prompt);
    }

    [Fact]
    public void PartNumber_IsStatedSoTheModelKnowsWhereItIs() {
        Assert.Contains("This is part 4", YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 4));
    }
}
