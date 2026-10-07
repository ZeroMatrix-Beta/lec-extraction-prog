using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the redirected-input side of <see cref="InteractiveDelay"/>: an Enter on stdin skips the
/// wait, end of input does not. Before, EOF counted as Enter, so with closed stdin every rate-limit
/// wait was skipped; the first fix then ended the wait early and threw instead.
/// </summary>
public class InteractiveDelayTests {
    [Fact]
    public async Task WaitForEnterAsync_returns_true_for_a_line() {
        Assert.True(await InteractiveDelay.WaitForEnterAsync(new StringReader("\n"), CancellationToken.None));
    }

    [Fact]
    public async Task WaitForEnterAsync_keeps_waiting_at_end_of_input_until_cancelled() {
        using var cts = new CancellationTokenSource();

        var wait = InteractiveDelay.WaitForEnterAsync(new StringReader(""), cts.Token);
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);

        cts.Cancel();
        Assert.False(await wait);
    }
}
