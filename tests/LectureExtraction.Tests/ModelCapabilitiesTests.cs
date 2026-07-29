using LectureExtraction.GoogleAi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the Gemma system-role handling ahead of the Phase 8.5b chat-session rewrite.
///
/// <para>The plan calls this out as the capability most likely to be lost in that rewrite, and the
/// reason is structural: <c>"gemma"</c> appears in no <c>AvailableModels</c> array, so it is
/// reachable only by typing the model name as freetext. No menu exercises it, so no amount of
/// clicking through the app would reveal that it had stopped working - the symptom would be a
/// rejected request against a paid API, on a model the user reaches deliberately and rarely.</para>
/// </summary>
public class ModelCapabilitiesTests {
    [Theory]
    [InlineData("gemma-2-9b-it")]
    [InlineData("gemma-3-27b-it")]
    [InlineData("gemma")]
    [InlineData("GEMMA-2-9B-IT")]
    public void Gemma_before_v4_needs_the_system_instruction_in_the_first_user_turn(string model) {
        Assert.True(ModelCapabilities.RequiresSystemInstructionInFirstUserTurn(model));
    }

    [Theory]
    [InlineData("gemma-4-12b-it")]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-2.5-pro")]
    [InlineData("")]
    [InlineData(null)]
    public void Everything_else_uses_the_dedicated_system_instruction_field(string? model) {
        Assert.False(ModelCapabilities.RequiresSystemInstructionInFirstUserTurn(model!));
    }

    /// <summary>
    /// Behaviour correction, stated explicitly because it is a change rather than a move: the
    /// original inline check paired a case-INsensitive <c>StartsWith("gemma")</c> with a
    /// case-SENSITIVE <c>Contains("gemma-4")</c>. "Gemma-4-12b-it" therefore passed the first test
    /// and failed the second, and was treated as pre-v4 - folding the system instruction into the
    /// user turn for a model that supports the dedicated field. The mismatched comparison on one
    /// line was clearly unintended; both halves are now case-insensitive.
    /// </summary>
    [Theory]
    [InlineData("Gemma-4-12b-it")]
    [InlineData("GEMMA-4-12B-IT")]
    public void Capitalised_v4_is_recognised_as_v4(string model) {
        Assert.False(ModelCapabilities.RequiresSystemInstructionInFirstUserTurn(model));
    }

    [Theory]
    [InlineData("gemini-2.5-pro", true)]
    [InlineData("gemini-3.6-flash", true)]
    [InlineData("gemini-2.0-flash-thinking", true)]
    [InlineData("gemini-2.0-flash", false)]
    [InlineData("gemma-3-27b-it", false)]
    [InlineData("", false)]
    public void SupportsThinking_is_unchanged_by_the_extraction(string model, bool expected) {
        Assert.Equal(expected, ModelCapabilities.SupportsThinking(model));
    }
}
