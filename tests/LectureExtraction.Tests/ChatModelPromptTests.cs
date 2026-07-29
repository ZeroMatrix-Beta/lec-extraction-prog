using LectureExtraction.Chat;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// [AI Context] Pins <see cref="ChatModelPrompt.Resolve"/>, the argument rule behind
/// <c>set model</c>. It is separated from the interactive picker precisely so it can be tested,
/// following the <see cref="ChatCommandParser"/> lesson: an argument rule fused into a printing
/// handler is where <c>set thinking-budget</c> and <c>set thinking-level</c> stayed broken.
///
/// <para>No <c>[Collection]</c> attribute is needed here - nothing in this class touches
/// <c>AnsiConsole.Console</c>, which is the global static the console collection exists to
/// serialise. <see cref="ChatModelPrompt.Pick"/> does, and is not covered here.</para>
/// [Human] Testet die Regel, wie das Argument von "set model" zu einem Modellnamen wird.
/// </summary>
public class ChatModelPromptTests {
    private static readonly string[] s_models = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-2.5-flash"];

    [Theory]
    [InlineData("1", "gemini-3.6-flash")]
    [InlineData("2", "gemini-3.5-flash")]
    [InlineData("3", "gemini-2.5-flash")]
    public void Resolve_NumberInRange_SelectsByOneBasedIndex(string arg, string expected) {
        Assert.Equal(expected, ChatModelPrompt.Resolve(arg, s_models));
    }

    [Theory]
    [InlineData("0")]   // 0 is not an index here - the list is 1-based
    [InlineData("4")]   // one past the end
    [InlineData("-1")]
    public void Resolve_NumberOutOfRange_IsTakenAsALiteralModelName(string arg) {
        Assert.Equal(arg, ChatModelPrompt.Resolve(arg, s_models));
    }

    /// <summary>
    /// The freetext branch is the only way to reach a model that is not in the configured list -
    /// a Gemma build, say - so it must survive any future tightening of this method.
    /// </summary>
    [Fact]
    public void Resolve_NonNumericArgument_IsUsedVerbatimAsTheModelName() {
        Assert.Equal("gemma-3-27b-it", ChatModelPrompt.Resolve("gemma-3-27b-it", s_models));
    }

    [Fact]
    public void Resolve_EmptyList_FallsBackToTheArgument() {
        Assert.Equal("1", ChatModelPrompt.Resolve("1", []));
    }
}
