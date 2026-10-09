using System.IO;
using LectureExtraction.ConsoleUi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

[Collection(ConsoleTestCollection.Name)]
public class UiTests {
    [Theory]
    [InlineData(@"\section[short]{long}")]
    [InlineData(@"\begin{itemize}\item[a] x")]
    [InlineData("[FEHLER] literal in payload")]
    public void Raw_and_severity_helpers_never_mangle_latex(string s) {
        var previous = AnsiConsole.Console;
        var console = new TestConsole();
        AnsiConsole.Console = console;
        try {
            Ui.Raw(s);
            Assert.Equal(s, console.Output);

            console.Clear();
            Ui.Info(s);
            Assert.Contains(s, console.Output);
        }
        finally {
            AnsiConsole.Console = previous;
        }
    }

    [Fact]
    public async Task SmartDelayAsync_ZeroSeconds_ReturnsTrueImmediately() {
        bool result = await InteractiveDelay.SmartDelayAsync(0);
        Assert.True(result);
    }

    [Fact]
    public void Frames_are_plain_ascii_when_the_output_is_not_a_console() {
        // A log read back as Windows-1252 turns every box-drawing glyph into "â”€"-style noise.
        var previous = AnsiConsole.Console;
        var console = new TestConsole();
        AnsiConsole.Console = console;
        try {
            Assert.False(Ui.IsTerminalOutput);

            Ui.Step("Schritt", "Scope");
            Ui.Header("Kopf");
            Ui.Table("Tabelle", [("Key", "Value")]);

            Assert.All(console.Output, c => Assert.True(c < 128, $"Non-ASCII '{c}' (U+{(int)c:X4}) in: {console.Output}"));
        }
        finally {
            AnsiConsole.Console = previous;
        }
    }

    [Fact]
    public async Task A_delay_that_cannot_redraw_prints_one_line_instead_of_a_countdown() {
        string output = await RunUnattendedDelay(new TestConsole(), seconds: 2);

        Assert.Single(output.Split('\n'), line => line.Contains("Warte"));
        Assert.Contains("[Delay]", output);
    }

    [Fact]
    public async Task A_delay_that_can_redraw_shows_the_live_countdown_even_unattended() {
        // An unattended `lecx run` in a real terminal: nobody presses Enter, but someone may watch.
        string output = await RunUnattendedDelay(new TestConsole().Interactive().EmitAnsiSequences(), seconds: 1);

        Assert.Contains("Warte 1s: Testwarten", output);
        Assert.DoesNotContain("[Delay]", output);
    }

    private static async Task<string> RunUnattendedDelay(TestConsole console, int seconds) {
        var previousConsole = AnsiConsole.Console;
        var previousSource = Ui.PromptSource;
        AnsiConsole.Console = console;
        Ui.PromptSource = new PresetPromptSource(assumeYes: true);
        try {
            Assert.True(await InteractiveDelay.SmartDelayAsync(seconds, "Testwarten"));
            return console.Output;
        }
        finally {
            AnsiConsole.Console = previousConsole;
            Ui.PromptSource = previousSource;
        }
    }
}
