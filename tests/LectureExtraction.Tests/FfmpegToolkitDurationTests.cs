using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LectureExtraction.Media;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers <see cref="FfmpegToolkit.GetVideoDurationAsync"/> on the raw .aac files that the audio
/// extraction writes.
///
/// <para>ADTS has no duration header, so ffprobe estimates the length from the bitrate. On a real
/// 01:30:51 lecture it reported 01:30:33, and the merge stage then told Gemini the audio ended there
/// and derived every part boundary from it, 14 s off the real offsets by part 4. The fixture opens
/// with 20 s of silence, which pushes ffprobe's estimate to about 980 s for a 60 s file, so a
/// regression cannot pass by luck. Needs ffmpeg on PATH, like the pipeline itself.</para>
/// </summary>
public class FfmpegToolkitDurationTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lecx-dur-" + Guid.NewGuid().ToString("N"));

    public FfmpegToolkitDurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() {
        try {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) {
            // A leftover temp folder must not fail the suite, but it should be visible.
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public async Task GetVideoDurationAsync_MeasuresARawAacFileExactly() {
        string aacFile = Path.Combine(_directory, "silence-then-noise.aac");
        await RunFfmpegAsync("-f lavfi -i anullsrc=r=48000:cl=mono -f lavfi -i anoisesrc=r=48000:a=0.5 " +
                             "-filter_complex \"[0]atrim=0:20[a];[1]atrim=0:40[b];[a][b]concat=n=2:v=0:a=1\" " +
                             $"-c:a aac -b:a 96k -ac 1 -ar 48000 \"{aacFile}\"");

        double duration = await FfmpegToolkit.GetVideoDurationAsync(aacFile);

        // 60 s of audio plus the encoder's priming frame (1024 samples, about 0.02 s).
        Assert.InRange(duration, 60.0, 60.1);
    }

    private static async Task RunFfmpegAsync(string arguments) {
        var startInfo = new ProcessStartInfo {
            FileName = "ffmpeg",
            Arguments = $"-nostdin -v error -y {arguments}",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)!;
        string errors = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ffmpeg failed to build the fixture: {errors}");
    }
}
