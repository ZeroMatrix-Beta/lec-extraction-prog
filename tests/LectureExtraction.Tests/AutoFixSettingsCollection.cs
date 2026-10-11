using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Serializes every test that changes the static switches of the LaTeX auto-fixes (for example
/// <c>MisnamedEnvironmentEndFix.CloseProofs</c>). Those switches are global, and xUnit runs test classes
/// in parallel - a class flipping a switch would change what another class's fixes do mid-test.
/// DisableParallelization runs this collection on its own, after the parallel ones. Tests in it must
/// restore the defaults when they finish (<c>MisnamedEnvironmentEndFix.RestoreDefaults()</c>).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AutoFixSettingsCollection {
    public const string Name = "AutoFixSettings";
}
