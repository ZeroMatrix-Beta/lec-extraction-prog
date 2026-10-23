using System.Collections.Generic;
using System.Linq;
using Google.GenAI.Types;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] What the AI Studio free tier's 250k input-tokens-per-minute gate appears to check a request
/// against, as measured by the gemini-cache-probe repository on 2026-10-23: the text characters of the
/// request (system instruction + contents) divided by 4, plus the real tokens already charged that minute.
/// Video and images (FileData, InlineData) do not count, JSON escaping does not count, the implicit cache
/// does not lower it. Computed locally, so it costs no request, and logged next to the CountTokens figure
/// so each refusal can be explained. An empirical estimate, not a documented rule.
/// [Human] Schätzung, womit das Minutenlimit des Free Tiers einen Request misst: Textzeichen ÷ 4.
/// </summary>
public static class MinuteGateEstimate {
    public const int FreeTierLimit = 250_000;

    public static long TextCharacters(Content? systemInstruction, IEnumerable<Content> contents) =>
        (systemInstruction is null ? [] : new[] { systemInstruction }).Concat(contents)
            .SelectMany(c => c.Parts ?? [])
            .Sum(p => (long)(p.Text?.Length ?? 0));

    /// <summary>The log line: characters, characters ÷ 4 and its share of the limit.</summary>
    public static string Describe(Content? systemInstruction, IEnumerable<Content> contents) {
        long chars = TextCharacters(systemInstruction, contents);
        long estimate = chars / 4;
        return $"Minutenlimit-Schätzung: {chars:N0} Textzeichen ÷ 4 = {estimate:N0} " +
               $"({estimate * 100 / FreeTierLimit} % von {FreeTierLimit:N0}; Video/Bilder zählen nicht, " +
               "Requests derselben Minute kommen mit ihren echten Tokens dazu)";
    }
}
