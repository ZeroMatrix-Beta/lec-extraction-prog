using System;
using System.IO;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The parts of <see cref="ConsoleCancelScope"/> that can run without a real Ctrl+C: a fresh scope
/// is not cancelled, and disposing twice (an explicit Dispose inside a <c>using</c>) is harmless.
/// </summary>
public class ConsoleCancelScopeTests {
    [Fact]
    public void A_new_scope_is_not_cancelled_and_can_be_disposed_twice() {
        var scope = new ConsoleCancelScope();

        Assert.False(scope.IsCancellationRequested);
        Assert.False(scope.Token.IsCancellationRequested);

        scope.Dispose();
        scope.Dispose();
        Assert.False(scope.IsCancellationRequested);
    }

    [Fact]
    public void Describe_names_the_type_and_the_message() {
        Assert.Equal("IOException: disk full", new IOException("disk full").Describe());
    }
}
