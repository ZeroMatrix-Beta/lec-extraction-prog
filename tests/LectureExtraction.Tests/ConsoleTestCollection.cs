namespace LectureExtraction.Tests;

/// <summary>
/// Serializes every test that swaps <c>AnsiConsole.Console</c> for a <c>TestConsole</c>.
///
/// <para>That property is a global static, and xUnit runs test <i>classes</i> in parallel by
/// default - so two classes each installing their own console will intermittently read each
/// other's output or steal each other's queued keystrokes. The failure looks like a flaky
/// assertion on console text, not like a race. Any new test class touching
/// <c>AnsiConsole.Console</c> must join this collection.</para>
/// </summary>
public static class ConsoleTestCollection {
    public const string Name = "AnsiConsole";
}
