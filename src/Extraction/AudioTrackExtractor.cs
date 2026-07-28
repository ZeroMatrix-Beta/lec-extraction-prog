using System;
using System.IO;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;
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
            Ui.Info($"Vorhandene Audio-Datei gefunden: {Path.GetFileName(expectedAudioPath)}. Überspringe Audio-Extraktion.", "Cache");
            return;
        }

        _audioExtractionTask = Task.Run(async () => {
            Ui.Info($"Starte parallele Audio-Extraktion im Hintergrund für {Path.GetFileName(sourceVideoPath)}...", "FFmpeg");
            await FfmpegToolkit.ExtractAudioAsAacAsync(sourceVideoPath, fileSpecificOutputFolder);
            Ui.Success($"Audio-Extraktion für {Path.GetFileName(sourceVideoPath)} abgeschlossen.", "FFmpeg");
        });
    }

    public Task? PendingTask => _audioExtractionTask;
}
