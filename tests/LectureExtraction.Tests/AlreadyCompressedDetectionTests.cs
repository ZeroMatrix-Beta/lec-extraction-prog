using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using LectureExtraction.Extraction;
using LectureExtraction.Media;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers <see cref="ExtractionHelpers.IsAlreadyCompressedAsync"/>: a 1 FPS video counts as compressed
/// whatever its name, a "-compressed" name counts without looking at the file, and a raw 25 FPS
/// recording does not. Needs ffmpeg on PATH; skipped without it.
/// </summary>
public class AlreadyCompressedDetectionTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lecx-fps-" + Guid.NewGuid().ToString("N"));

    public AlreadyCompressedDetectionTests() => Directory.CreateDirectory(_directory);

    public void Dispose() {
        try {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) {
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RequiresFfmpegFact]
    public async Task OneFpsVideoWithAPlainName_CountsAsCompressed() {
        string video = await MakeVideoAsync("02-22-2024-thursday-Information_Security.mp4", fps: 1);

        Assert.Equal(1.0, await FfmpegToolkit.GetVideoFrameRateAsync(video), precision: 3);
        Assert.True(await ExtractionHelpers.IsAlreadyCompressedAsync(video));
    }

    [RequiresFfmpegFact]
    public async Task RawRecording_IsNotCompressed() {
        string video = await MakeVideoAsync("02-22-2024-thursday-Information_Security.mp4", fps: 25);

        Assert.Equal(25.0, await FfmpegToolkit.GetVideoFrameRateAsync(video), precision: 3);
        Assert.False(await ExtractionHelpers.IsAlreadyCompressedAsync(video));
    }

    [RequiresFfmpegFact]
    public async Task CompressedName_WinsEvenAtFullFrameRate() {
        string video = await MakeVideoAsync("lecture-speed-1-compressed.mp4", fps: 25);

        Assert.True(await ExtractionHelpers.IsAlreadyCompressedAsync(video));
    }

    [RequiresFfmpegFact]
    public async Task FileWithoutVideoStream_IsNotCompressed() {
        string audioOnly = Path.Combine(_directory, "audio-only.mp4");
        await RunFfmpegAsync($"-f lavfi -i sine=frequency=440 -t 3 -c:a aac \"{audioOnly}\"");

        Assert.Equal(-1, await FfmpegToolkit.GetVideoFrameRateAsync(audioOnly));
        Assert.False(await ExtractionHelpers.IsAlreadyCompressedAsync(audioOnly));
    }

    private async Task<string> MakeVideoAsync(string fileName, int fps) {
        string path = Path.Combine(_directory, fileName);
        await RunFfmpegAsync($"-f lavfi -i testsrc=size=320x240:rate={fps} -t 5 -c:v libx264 \"{path}\"");
        return path;
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
