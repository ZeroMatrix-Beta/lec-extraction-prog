using System;
using System.IO;
using System.Threading.Tasks;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Runs the optional parallel audio extraction for a source video, shared verbatim
/// between the AI Studio and Vertex extraction sessions. Audio extraction is a deterministic
/// derivative of the source video, so unlike the LLM-generated .tex parts it never goes stale —
/// no TTL, just existence + a sanity size check.
/// [Human] Startet die optionale, parallele Audio-Extraktion für ein Quellvideo.
/// </summary>
public sealed class AudioTrackExtractor(string sourceVideoPath, string fileSpecificOutputFolder) {
    private Task? _audioExtractionTask;

    public void EnsureStarted(bool generateAudioFile) {
        if (!generateAudioFile || _audioExtractionTask != null) {
            return;
        }

        string expectedAudioPath = Path.Combine(fileSpecificOutputFolder, $"{Path.GetFileNameWithoutExtension(sourceVideoPath)}_audio.aac");
        bool useCachedAudio = File.Exists(expectedAudioPath) && new FileInfo(expectedAudioPath).Length >= 1024;
        if (useCachedAudio) {
            Console.WriteLine($"\n[Cache] Vorhandene Audio-Datei gefunden: {Path.GetFileName(expectedAudioPath)}. Überspringe Audio-Extraktion.");
            return;
        }

        _audioExtractionTask = Task.Run(async () => {
            Console.WriteLine($"\n[FFmpeg] Starte parallele Audio-Extraktion im Hintergrund für {Path.GetFileName(sourceVideoPath)}...");
            await FfmpegToolkit.ExtractAudioAsAacAsync(sourceVideoPath, fileSpecificOutputFolder);
            Console.WriteLine($"\n[FFmpeg] Audio-Extraktion für {Path.GetFileName(sourceVideoPath)} abgeschlossen.");
        });
    }

    public Task? PendingTask => _audioExtractionTask;
}
