using System.Collections.Generic;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction.Model;

/// <summary>
/// [AI Context] The payload handed from the FFmpeg producer task to the Gemini consumer loop in
/// both extraction sessions' bounded channel. Replaces the anonymous six-element tuple
/// `(string originalFile, string fileSpecificOutputFolder, string tmpFolderForFile,
/// List&lt;(string FilePath, double StartTime)&gt; parts, bool isCached, double fullOriginalVideoDuration)`
/// that made the channel's declaration line unreadable and gave every field a positional,
/// easy-to-transpose meaning instead of a name.
/// [Human] Das Datenpaket, das der FFmpeg-Producer-Task über den Channel an die Gemini-Konsumenten-
/// Schleife übergibt - ein vollständig vorbereitetes (geschnittenes) Video, bereit zum Hochladen.
/// </summary>
public sealed record PreparedVideo(
    string SourceVideoPath,
    string OutputFolder,
    string TempFolder,
    IReadOnlyList<VideoSegment> Segments,
    bool CameFromCache,
    double SourceDurationSeconds);
