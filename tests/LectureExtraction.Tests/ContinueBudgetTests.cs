using LectureExtraction.GoogleAi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the two limits of <see cref="ContinueBudget"/>. Every continue re-sends the whole history
/// (for extraction: the video), so these limits are what bounds the bill of a model that never
/// writes its completion marker.
/// </summary>
public class ContinueBudgetTests {
    [Fact]
    public void Stops_at_the_total_even_when_every_response_makes_progress() {
        var budget = new ContinueBudget(maxTotal: 3);

        Assert.True(budget.TryContinue("part one", out _));
        Assert.Equal(2, budget.RequestNumber);
        Assert.True(budget.TryContinue("part two", out _));
        Assert.Equal(3, budget.RequestNumber);
        Assert.False(budget.TryContinue("part three", out string reason));
        Assert.Contains("3", reason);
    }

    [Fact]
    public void Stops_after_consecutive_empty_responses() {
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinue("", out _));
        Assert.False(budget.TryContinue("   ", out string reason));
        Assert.Contains("ohne Fortschritt", reason);
    }

    [Fact]
    public void A_repeated_tail_counts_as_no_progress() {
        // A model stuck re-emitting the same block until MAX_TOKENS.
        string loop = new string('x', 500);
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinue(loop, out _));
        Assert.True(budget.TryContinue(loop, out _));
        Assert.False(budget.TryContinue(loop, out _));
    }

    [Fact]
    public void Progress_resets_the_no_progress_count() {
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinue("", out _));
        Assert.True(budget.TryContinue("text", out _));
        Assert.True(budget.TryContinue("", out _));
        Assert.Equal(1, budget.RequestsWithoutProgress);
    }
}
