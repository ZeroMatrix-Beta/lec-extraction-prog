using System;
using LectureExtraction.ConsoleUi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the navigation contract that replaced the <c>"__EXIT__"</c> / <c>"__CHANGED_KEY__"</c>
/// sentinel strings (review findings F2 and F11).
///
/// <para>The failure mode these guard against is a prompt that silently falls through: a menu
/// whose "back" entry returns a <i>value</i> instead of <see cref="PromptOutcome.Back"/> looks
/// fine and quietly continues the flow with whatever happened to be selected. The tests drive the
/// real Spectre prompts through a <see cref="TestConsole"/>, so they exercise the same code path
/// the user does.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class PromptResultTests {
    private static TestConsole NewConsole(params ConsoleKey[] keys) {
        var console = new TestConsole().Interactive();
        foreach (var key in keys) {
            console.Input.PushKey(key);
        }
        AnsiConsole.Console = console;
        return console;
    }

    [Fact]
    public void Or_returns_the_value_when_one_was_chosen() {
        Assert.Equal("chosen", PromptResult.FromValue("chosen").Or("fallback"));
    }

    [Theory]
    [InlineData(PromptOutcome.Back)]
    [InlineData(PromptOutcome.Exit)]
    [InlineData(PromptOutcome.Restart)]
    public void Or_returns_the_fallback_for_every_non_value_outcome(PromptOutcome outcome) {
        var result = new PromptResult<string>(outcome, null);
        Assert.False(result.IsValue);
        Assert.Equal("fallback", result.Or("fallback"));
    }

    [Fact]
    public void Select_returns_the_payload_of_the_chosen_entry_not_its_label() {
        NewConsole(ConsoleKey.Enter);

        var result = Ui.Select("Titel", [("1) Erste Option", 42), ("2) Zweite Option", 7)]);

        Assert.True(result.IsValue);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Select_reports_Back_when_the_back_entry_is_chosen() {
        // Two entries plus the appended back entry: two Downs land on it.
        NewConsole(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);

        var result = Ui.Select("Titel", [("Erste", 1), ("Zweite", 2)]);

        Assert.True(result.IsBack);
        Assert.Equal(default, result.Value);
    }

    [Fact]
    public void Select_reports_Exit_when_the_cancel_entry_is_chosen() {
        NewConsole(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);

        var result = Ui.Select("Titel", [("Erste", 1), ("Zweite", 2)], allowBack: true, allowExit: true);

        Assert.True(result.IsExit);
    }

    [Fact]
    public void Select_without_allowBack_offers_no_back_entry() {
        var console = NewConsole(ConsoleKey.DownArrow, ConsoleKey.Enter);

        var result = Ui.Select("Titel", [("Erste", 1), ("Zweite", 2)], allowBack: false);

        Assert.DoesNotContain("Zurück", console.Output);
        Assert.True(result.IsValue);
        Assert.Equal(2, result.Value);
    }

    [Fact]
    public void Select_escapes_markup_in_labels_rather_than_rendering_it() {
        // Video labels carry "[Woche 1]" and filenames may contain brackets; Spectre would either
        // throw on an unknown style or swallow the rest of the line.
        var console = NewConsole(ConsoleKey.Enter);

        var result = Ui.Select("Titel", [("1) video.mp4  [Woche 1]", "v")]);

        Assert.True(result.IsValue);
        Assert.Contains("[Woche 1]", console.Output);
    }

    [Fact]
    public void ConfirmOrBack_distinguishes_No_from_Back() {
        NewConsole(ConsoleKey.DownArrow, ConsoleKey.Enter);
        var no = Ui.ConfirmOrBack("Frage?", defaultYes: true);
        Assert.True(no.IsValue);
        Assert.False(no.Value);

        NewConsole(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);
        var back = Ui.ConfirmOrBack("Frage?", defaultYes: true);
        Assert.True(back.IsBack);
    }

    [Fact]
    public void SetupQuestion_hides_the_api_key_entry_when_the_caller_cannot_handle_it() {
        var console = NewConsole(ConsoleKey.Enter);

        var answer = SetupQuestionPrompt.Ask("Laden?");

        Assert.True(answer.IsValue);
        Assert.True(answer.Value);
        Assert.DoesNotContain("API-Key", console.Output);
    }

    [Fact]
    public void SetupQuestion_reports_Restart_after_the_api_key_profile_was_changed() {
        NewConsole(ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);
        bool invoked = false;

        var answer = SetupQuestionPrompt.Ask("Laden?", () => invoked = true);

        Assert.True(invoked);
        Assert.True(answer.IsRestart);
    }
}
