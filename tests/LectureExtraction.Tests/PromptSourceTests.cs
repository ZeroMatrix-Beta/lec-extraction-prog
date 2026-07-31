using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the unattended answering rule, which is the load-bearing safety property of the CLI:
/// <b>a prompt carrying an explicit default may be auto-answered under <c>--yes</c>; a menu never
/// may.</b> Getting this backwards would let a scripted run silently pick the first model or the
/// first source folder in a list - a wrong, billable choice that nothing would report.
/// </summary>
public class PresetPromptSourceTests {
    private static readonly string[] TwoChoices = ["gemini-3.6-flash", "gemini-2.5-flash"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectIndex_AlwaysThrows_EvenWithAssumeYes(bool assumeYes) {
        var source = new PresetPromptSource(assumeYes);

        var ex = Assert.Throws<UnattendedPromptException>(
            () => source.SelectIndex("Welches Modell?", TwoChoices, 15, null));

        Assert.Equal("Welches Modell?", ex.PromptTitle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectManyIndices_AlwaysThrows_EvenWithAssumeYes(bool assumeYes) {
        var source = new PresetPromptSource(assumeYes);

        Assert.Throws<UnattendedPromptException>(
            () => source.SelectManyIndices("Welche Videos?", TwoChoices, 15, null, null));
    }

    [Fact]
    public void Confirm_ThrowsWithoutAssumeYes() {
        var source = new PresetPromptSource(assumeYes: false);

        Assert.Throws<UnattendedPromptException>(() => source.Confirm("Fortfahren?", defaultYes: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Confirm_TakesTheStatedDefault_UnderAssumeYes(bool defaultYes) {
        // "--yes" means "accept the default", not "answer yes" - a question whose considered
        // default is No must still come back No.
        var source = new PresetPromptSource(assumeYes: true);

        Assert.Equal(defaultYes, source.Confirm("Fortfahren?", defaultYes));
    }

    [Fact]
    public void AskValue_ReturnsTheDefault_UnderAssumeYes() {
        var source = new PresetPromptSource(assumeYes: true);

        Assert.Equal(42, source.AskValue("Wie viele Teile?", 42));
    }

    [Fact]
    public void AskValue_ThrowsWithoutAssumeYes() {
        var source = new PresetPromptSource(assumeYes: false);

        Assert.Throws<UnattendedPromptException>(() => source.AskValue("Wie viele Teile?", 42));
    }

    [Fact]
    public void AskText_ReturnsTheDefault_UnderAssumeYes() {
        var source = new PresetPromptSource(assumeYes: true);

        Assert.Equal("youtube-lecture", source.AskText("Name?", "youtube-lecture"));
    }

    [Fact]
    public void AskText_ThrowsWhenThereIsNoDefault_EvenUnderAssumeYes() {
        // Nothing to accept: an open free-text question has no considered answer to fall back on.
        var source = new PresetPromptSource(assumeYes: true);

        Assert.Throws<UnattendedPromptException>(() => source.AskText("Bitte gib die URL ein:", null));
    }

    [Fact]
    public void Message_NamesThePromptAndWhatWouldUnblockIt() {
        var source = new PresetPromptSource(assumeYes: false);

        var ex = Assert.Throws<UnattendedPromptException>(() => source.Confirm("Historie laden?", defaultYes: true));

        Assert.Contains("Historie laden?", ex.Message);
        Assert.Contains("--yes", ex.Message);
    }
}

/// <summary>
/// Covers the <see cref="Ui"/> side of the seam: that the menu primitives really delegate, and that
/// they map an answer back onto the right choice and outcome.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class UiPromptRoutingTests {
    /// <summary>A source that answers by position, so a test can drive Ui without a terminal.</summary>
    private sealed class ScriptedPromptSource(int index) : IPromptSource {
        public string? LastTitle { get; private set; }
        public IReadOnlyList<string> LastLabels { get; private set; } = [];

        public int SelectIndex(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText) {
            LastTitle = title;
            LastLabels = labels;
            return index;
        }

        public IReadOnlyList<int> SelectManyIndices(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText, string? instructionsText) {
            LastTitle = title;
            LastLabels = labels;
            return [index];
        }

        public bool Confirm(string question, bool defaultYes) => defaultYes;
        public string AskText(string question, string? defaultValue) => defaultValue ?? "";
        public T AskValue<T>(string question, T defaultValue) => defaultValue;
    }

    private static T WithSource<T>(IPromptSource source, Func<T> body) {
        var previous = Ui.PromptSource;
        Ui.PromptSource = source;
        try {
            return body();
        }
        finally {
            Ui.PromptSource = previous;
        }
    }

    [Fact]
    public void Select_ReturnsTheChosenValue() {
        var source = new ScriptedPromptSource(1);

        var result = WithSource(source, () => Ui.Select("Modell?", [("A", 10), ("B", 20)], allowBack: false));

        Assert.True(result.IsValue);
        Assert.Equal(20, result.Value);
    }

    [Fact]
    public void Select_AppendsBackAndExitEntries_InThatOrder() {
        var source = new ScriptedPromptSource(0);

        WithSource(source, () => Ui.Select("Modell?", [("A", 10)], allowBack: true, allowExit: true));

        Assert.Equal(["A", Ui.BackChoiceLabel, Ui.ExitChoiceLabel], source.LastLabels);
    }

    [Fact]
    public void Select_MapsTheBackEntryToTheBackOutcome() {
        // Index 1 is the appended back entry, not a value.
        var result = WithSource(new ScriptedPromptSource(1), () => Ui.Select("Modell?", [("A", 10)], allowBack: true));

        Assert.True(result.IsBack);
        Assert.False(result.IsValue);
    }

    [Fact]
    public void SelectMany_ReturnsPicksAsValues() {
        var chosen = WithSource(new ScriptedPromptSource(1), () => Ui.SelectMany("Videos?", [("A", "a.mp4"), ("B", "b.mp4")]));

        Assert.Equal(["b.mp4"], chosen);
    }

    [Fact]
    public void UnattendedRun_FailsAtTheMenu_WithTheMenusOwnTitle() {
        // The end-to-end shape of a headless run hitting a question it cannot answer.
        var ex = Assert.Throws<UnattendedPromptException>(
            () => WithSource(new PresetPromptSource(assumeYes: true), () => Ui.Select("Modus auswählen:", [("A", 1)])));

        Assert.Equal("Modus auswählen:", ex.PromptTitle);
    }
}
