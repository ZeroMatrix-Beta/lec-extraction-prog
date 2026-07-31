namespace LectureExtraction.Tests;

/// <summary>
/// Serializes every test that swaps a console global: <c>AnsiConsole.Console</c> (for a
/// <c>TestConsole</c>) or <c>Ui.PromptSource</c> (for a scripted or unattended answer source).
///
/// <para>Both are global statics, and xUnit runs test <i>classes</i> in parallel by
/// default - so two classes each installing their own will intermittently read each
/// other's output or steal each other's queued keystrokes. The failure looks like a flaky
/// assertion on console text, not like a race. Any new test class touching either
/// must join this collection.</para>
///
/// <para>Swapping <c>Ui.PromptSource</c> outside this collection does not merely produce a flaky
/// assertion: a class driving a real Spectre prompt through a <c>TestConsole</c> can be handed a
/// source that consumes none of its queued keys, leaving the prompt waiting on an empty input
/// queue - the whole test run hangs rather than failing.</para>
///
/// <para>Note the attribute takes <see cref="Name"/>, not <c>nameof(ConsoleTestCollection)</c>;
/// the latter compiles, names a different collection, and silently opts the class out.</para>
/// </summary>
public static class ConsoleTestCollection {
    public const string Name = "AnsiConsole";
}
