using System;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Collects a streamed response's usage metadata and turns it into a line that
/// distinguishes <b>"not reported"</b> from <b>"zero"</b>.
///
/// <para>Review finding F9: the warm-up's token line printed only <c>if (inputTokens > 0)</c>, and
/// usage arrives on the final chunk of a streamed response - if it never arrived, the line was
/// silently skipped and the handshake <i>looked</i> free. Silence there means "not reported", not
/// "not charged", and the two must never render identically again.</para>
///
/// <para>It also surfaces <see cref="GenerateContentResponseUsageMetadata.ThoughtsTokenCount"/>,
/// which nothing in this app read before. That is the direct answer to "output was only 2 tokens":
/// reasoning tokens are billed but are <b>not</b> part of <c>CandidatesTokenCount</c>, so a thinking
/// model's real output cost was invisible on every path, not just the warm-up. TotalTokenCount is
/// reported alongside because it is the server's own arithmetic, and it settles empirically whether
/// the existing "(inkl. Thinking Tokens)" labels elsewhere in this codebase were ever accurate.</para>
/// [Human] Sammelt die Nutzungsdaten eines Streams und macht sichtbar, ob überhaupt welche kamen -
/// inklusive der bisher nirgends ausgewerteten Denk-Tokens.
/// </summary>
public sealed class UsageReport {
    /// <summary>True once any chunk carried usage metadata at all.</summary>
    public bool WasReported { get; private set; }

    public int PromptTokens { get; private set; }
    public int CandidateTokens { get; private set; }
    public int CachedTokens { get; private set; }
    public int ThoughtTokens { get; private set; }
    public int TotalTokens { get; private set; }

    /// <summary>
    /// [AI Context] Takes the last non-null metadata seen. Usage is cumulative-to-date per chunk
    /// rather than incremental, so the final chunk's values are the request's totals - accumulating
    /// them would multiply the reported cost.
    /// [Human] Übernimmt die zuletzt gemeldeten Werte; Aufsummieren würde die Kosten vervielfachen.
    /// </summary>
    public void Absorb(GenerateContentResponseUsageMetadata? metadata) {
        if (metadata == null) {
            return;
        }

        WasReported = true;
        if (metadata.PromptTokenCount.HasValue) PromptTokens = metadata.PromptTokenCount.Value;
        if (metadata.CandidatesTokenCount.HasValue) CandidateTokens = metadata.CandidatesTokenCount.Value;
        if (metadata.CachedContentTokenCount.HasValue) CachedTokens = metadata.CachedContentTokenCount.Value;
        if (metadata.ThoughtsTokenCount.HasValue) ThoughtTokens = metadata.ThoughtsTokenCount.Value;
        if (metadata.TotalTokenCount.HasValue) TotalTokens = metadata.TotalTokenCount.Value;
    }

    /// <summary>
    /// [AI Context] Renders <paramref name="caller"/>'s own token line, appending what it could not
    /// know: whether the server reported anything, and the thinking/total figures.
    /// [Human] Ergänzt die Token-Zeile des Aufrufers um Denk-Tokens, Gesamtsumme und den Hinweis,
    /// falls gar nichts gemeldet wurde.
    /// </summary>
    /// <param name="tag">
    /// The line's leading tag. Callers pass their own - including its padding - because the verbose
    /// report aligns "[Request Tokens]" with "[Part Total Tokens]" and "[Session Total Tokens]" in a
    /// column, and a fixed tag here would break that alignment.
    /// </param>
    public string Describe(string caller, string tag = "[Tokens]") {
        if (!WasReported) {
            return $"{tag} Keine Nutzungsdaten vom Server erhalten - die Anfrage war trotzdem kostenpflichtig.";
        }

        string line = $"{tag} {caller}";
        if (ThoughtTokens > 0) {
            line += $" | Denk-Tokens: {ThoughtTokens:N0}";
        }
        if (TotalTokens > 0) {
            line += $" | Gesamt: {TotalTokens:N0}";
        }
        return line;
    }
}
