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
}
