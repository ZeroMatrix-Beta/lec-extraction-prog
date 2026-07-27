using System.Collections.Generic;
using Google.GenAI.Types;

namespace LectureExtraction.Extraction.Model;

/// <summary>
/// [AI Context] Result of uploading one video segment (and building its accompanying prompt
/// text) to the Google backend, before the transcription call is made. Replaces the anonymous
/// `(bool success, string? parsedPrompt, List&lt;Part&gt; attachmentParts)` tuple returned by
/// `PrepareAndUploadPartAsync` in both extraction sessions.
/// [Human] Ergebnis des Hochladens eines Videosegments samt dazugehörigem Prompt-Text, bevor der
/// eigentliche Transkriptions-Aufruf erfolgt.
/// </summary>
public sealed record SegmentUpload(bool Succeeded, string? Prompt, List<Part> Attachments);
