using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers when a video whose first sent part is not part 1 gets its extra warm-up round. The round
/// costs a request, so the tests pin both directions: it runs for a resume with inlined predecessors,
/// and it stays out of every run that starts at part 1 or cannot benefit from it.
/// </summary>
public class ResumedPartWarmupTests {
    private static AiStudioAutoExtractionConfig DefaultConfig() => new() {
        EnableImplicitPrefixCacheWarmup = true,
        DebugSendReferenceFile = true,
        InlinePrecedingLecTexParts = true
    };

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    public void Warms_up_a_resumed_part_with_inlined_predecessors(int partNumber, int precedingOnDisk) {
        Assert.True(AiStudioAutoExtractionSession.ShouldWarmUpResumedPart(DefaultConfig(), partNumber, precedingOnDisk));
    }

    [Fact]
    public void Skips_part_one() {
        Assert.False(AiStudioAutoExtractionSession.ShouldWarmUpResumedPart(DefaultConfig(), 1, 0));
    }

    [Fact]
    public void Skips_when_no_predecessor_is_on_disk() {
        Assert.False(AiStudioAutoExtractionSession.ShouldWarmUpResumedPart(DefaultConfig(), 3, 0));
    }

    [Fact]
    public void Skips_when_predecessors_are_uploaded_instead_of_inlined() {
        var config = DefaultConfig();
        config.InlinePrecedingLecTexParts = false;
        Assert.False(AiStudioAutoExtractionSession.ShouldWarmUpResumedPart(config, 4, 3));
    }

    [Fact]
    public void Skips_when_reference_context_is_off() {
        var config = DefaultConfig();
        config.DebugSendReferenceFile = false;
        Assert.False(AiStudioAutoExtractionSession.ShouldWarmUpResumedPart(config, 4, 3));
    }

    [Fact]
    public void Skips_when_warm_up_is_disabled() {
        var config = DefaultConfig();
        config.EnableImplicitPrefixCacheWarmup = false;
        Assert.False(AiStudioAutoExtractionSession.ShouldWarmUpResumedPart(config, 4, 3));
    }
}
