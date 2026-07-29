using LectureExtraction.Chat;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the chat REPL's command surface — the list Phase 8.5b names as "must be preserved" through
/// the rewrite.
///
/// <para>Before this parser existed, each command recognised itself, validated its argument, mutated
/// session state and printed, all in one private method. Testing any of it meant constructing a live
/// chat session against a paid API, so none of it was tested — and that is exactly how
/// <c>set thinking-budget</c> and <c>set thinking-level</c> shipped permanently broken. Their
/// arguments were sliced at hand-counted offsets (18 and 17) that were two and one characters short
/// of the prefixes they belonged to, so the parsed value was <c>"t 4096"</c> / <c>"l HIGH"</c> and
/// every invocation fell into the error branch.</para>
/// </summary>
public class ChatCommandParserTests {
    // --- The two commands that never worked -------------------------------------------------

    [Theory]
    [InlineData("set thinking-budget 4096", 4096)]
    [InlineData("set thinking-budget 0", 0)]
    [InlineData("/set thinking-budget 32768", 32768)]
    [InlineData("SET THINKING-BUDGET 128", 128)]
    public void Set_thinking_budget_finally_parses_its_argument(string input, int expected) {
        var command = ChatCommandParser.Parse(input);

        Assert.Equal(ChatCommandKind.SetThinkingBudget, command.Kind);
        Assert.True(command.IsValid, $"expected a valid budget, got: {command.Error}");
        Assert.Equal(expected, command.Integer);
    }

    [Theory]
    [InlineData("set thinking-level HIGH", "HIGH")]
    [InlineData("set thinking-level high", "HIGH")]
    [InlineData("/set thinking-level minimal", "MINIMAL")]
    [InlineData("set thinking-level MEDIUM", "MEDIUM")]
    [InlineData("set thinking-level low", "LOW")]
    public void Set_thinking_level_finally_parses_its_argument(string input, string expected) {
        var command = ChatCommandParser.Parse(input);

        Assert.Equal(ChatCommandKind.SetThinkingLevel, command.Kind);
        Assert.True(command.IsValid, $"expected a valid level, got: {command.Error}");
        Assert.Equal(expected, command.Text);
    }

    // --- Rejection is still rejection, with the original wording -----------------------------

    [Fact]
    public void An_unknown_thinking_level_is_rejected_and_lists_the_valid_ones() {
        var command = ChatCommandParser.Parse("set thinking-level EXTREME");

        Assert.Equal(ChatCommandKind.SetThinkingLevel, command.Kind);
        Assert.False(command.IsValid);
        Assert.Contains("MINIMAL, LOW, MEDIUM, HIGH", command.Error);
    }

    [Theory]
    [InlineData("set thinking-budget abc")]
    [InlineData("set thinking-budget -1")]
    public void A_non_numeric_or_negative_budget_is_rejected(string input) {
        Assert.False(ChatCommandParser.Parse(input).IsValid);
    }

    // --- Commands that already worked, pinned so the rewrite cannot lose them ----------------

    [Theory]
    [InlineData("set temp 0.5", 0.5f)]
    [InlineData("set temp 0", 0f)]
    [InlineData("set temp 2", 2f)]
    public void Set_temp_accepts_the_documented_range(string input, float expected) {
        var command = ChatCommandParser.Parse(input);
        Assert.Equal(ChatCommandKind.SetTemperature, command.Kind);
        Assert.True(command.IsValid);
        Assert.Equal(expected, command.Number);
    }

    [Theory]
    [InlineData("set temp 2.1")]
    [InlineData("set temp -0.1")]
    [InlineData("set temp warm")]
    public void Set_temp_rejects_values_outside_the_range(string input) {
        Assert.False(ChatCommandParser.Parse(input).IsValid);
    }

    /// <summary>
    /// Parsed with InvariantCulture deliberately: the app runs under a German culture where "," is
    /// the decimal separator, but the documented syntax is "set temp 0.5".
    /// </summary>
    [Fact]
    public void Set_temp_reads_a_dot_decimal_regardless_of_the_running_culture() {
        var command = ChatCommandParser.Parse("set temp 0.7");
        Assert.True(command.IsValid);
        Assert.Equal(0.7f, command.Number, 3);
    }

    [Theory]
    [InlineData("set tokens 8192", 8192)]
    [InlineData("set tokens 1", 1)]
    public void Set_tokens_accepts_positive_integers(string input, int expected) {
        var command = ChatCommandParser.Parse(input);
        Assert.Equal(ChatCommandKind.SetMaxTokens, command.Kind);
        Assert.True(command.IsValid);
        Assert.Equal(expected, command.Integer);
    }

    [Theory]
    [InlineData("set tokens 0")]
    [InlineData("set tokens -5")]
    [InlineData("set tokens many")]
    public void Set_tokens_rejects_non_positive_values(string input) {
        Assert.False(ChatCommandParser.Parse(input).IsValid);
    }

    [Theory]
    [InlineData("set grounding on", true)]
    [InlineData("set grounding true", true)]
    [InlineData("set grounding ja", true)]
    [InlineData("set grounding 1", true)]
    [InlineData("set grounding off", false)]
    [InlineData("set grounding nein", false)]
    [InlineData("set grounding 0", false)]
    public void Set_grounding_accepts_every_documented_spelling(string input, bool expected) {
        var command = ChatCommandParser.Parse(input);
        Assert.Equal(ChatCommandKind.SetGrounding, command.Kind);
        Assert.True(command.IsValid);
        Assert.Equal(expected, command.Flag);
    }

    [Fact]
    public void Set_grounding_rejects_anything_else() {
        Assert.False(ChatCommandParser.Parse("set grounding maybe").IsValid);
    }

    [Theory]
    [InlineData("set model", "")]
    [InlineData("set model 2", "2")]
    [InlineData("set model gemma-3-27b-it", "gemma-3-27b-it")]
    public void Set_model_carries_its_argument_and_allows_an_empty_one(string input, string expected) {
        var command = ChatCommandParser.Parse(input);
        Assert.Equal(ChatCommandKind.SetModel, command.Kind);
        Assert.Equal(expected, command.Text);
    }

    [Theory]
    [InlineData("change-key 2", 2)]
    [InlineData("changekey 0", 0)]
    [InlineData("change key 3", 3)]
    [InlineData("/change-key 1", 1)]
    public void Change_key_accepts_every_spelling_the_regex_allowed(string input, int expected) {
        var command = ChatCommandParser.Parse(input);
        Assert.Equal(ChatCommandKind.ChangeApiKeyProfile, command.Kind);
        Assert.True(command.IsValid);
        Assert.Equal(expected, command.Integer);
    }

    [Fact]
    public void Change_key_rejects_a_profile_outside_zero_to_three() {
        var command = ChatCommandParser.Parse("change-key 9");
        Assert.Equal(ChatCommandKind.ChangeApiKeyProfile, command.Kind);
        Assert.False(command.IsValid);
    }

    [Theory]
    [InlineData("help", ChatCommandKind.Help)]
    [InlineData("commands", ChatCommandKind.Help)]
    [InlineData("/help", ChatCommandKind.Help)]
    [InlineData("clear", ChatCommandKind.Clear)]
    [InlineData("reset", ChatCommandKind.Clear)]
    [InlineData("exit", ChatCommandKind.Exit)]
    [InlineData("quit", ChatCommandKind.Exit)]
    [InlineData("QUIT", ChatCommandKind.Exit)]
    public void Bare_commands_are_recognised_with_or_without_a_slash(string input, ChatCommandKind expected) {
        Assert.Equal(expected, ChatCommandParser.Parse(input).Kind);
    }

    [Fact]
    public void Attach_carries_the_whole_remainder_including_the_pipe_and_question() {
        var command = ChatCommandParser.Parse("attach \"a.tex\", \"b.tex\" | Was steht drin?");

        Assert.Equal(ChatCommandKind.Attach, command.Kind);
        Assert.Equal("\"a.tex\", \"b.tex\" | Was steht drin?", command.Text);
    }

    // --- Everything else is a prompt, not a command ------------------------------------------

    [Theory]
    [InlineData("Was ist die Ableitung von x^2?")]
    [InlineData("clearly this is a sentence")]
    [InlineData("exit the building")]
    [InlineData("set the table")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ordinary_input_is_not_mistaken_for_a_command(string? input) {
        Assert.Equal(ChatCommandKind.None, ChatCommandParser.Parse(input).Kind);
    }

    /// <summary>
    /// "clear" is a command; "clear the history please" is a question about clearing. Bare commands
    /// match exactly rather than by prefix, so a sentence that merely starts with one still reaches
    /// the model.
    /// </summary>
    [Fact]
    public void A_sentence_beginning_with_a_command_word_still_reaches_the_model() {
        Assert.Equal(ChatCommandKind.None, ChatCommandParser.Parse("clear the history please").Kind);
    }
}
