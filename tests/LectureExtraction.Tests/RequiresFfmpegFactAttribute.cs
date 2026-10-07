using System;
using System.IO;
using System.Linq;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly, not as a silent pass) when ffmpeg is not on
/// PATH. The pipeline needs ffmpeg anyway, but a fresh checkout's test run should not fail on it.
/// </summary>
public sealed class RequiresFfmpegFactAttribute : FactAttribute {
    public RequiresFfmpegFactAttribute() {
        if (!IsOnPath("ffmpeg")) {
            Skip = "ffmpeg is not on PATH.";
        }
    }

    private static bool IsOnPath(string tool) {
        string[] names = OperatingSystem.IsWindows() ? [tool + ".exe", tool + ".cmd", tool + ".bat"] : [tool];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(name => File.Exists(Path.Combine(dir.Trim('"'), name))));
    }
}
