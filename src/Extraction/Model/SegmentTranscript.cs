namespace LectureExtraction.Extraction.Model;

/// <summary>
/// [AI Context] Result of transcribing one uploaded video segment: the generated LaTeX text plus
/// the token accounting for that call. Replaces the anonymous
/// `(string texOutput, int inputTokens, int outputTokens, int cachedTokens)` tuple returned by
/// `GenerateTexFromUploadedPartAsync` in both extraction sessions.
/// [Human] Ergebnis der Transkription eines hochgeladenen Videosegments: der generierte LaTeX-Text
/// plus die dabei verbrauchten Tokens.
/// </summary>
public sealed record SegmentTranscript(string LatexBody, TokenUsage Usage);
