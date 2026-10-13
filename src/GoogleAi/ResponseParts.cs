using System.Text;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Splits one streamed chunk into the answer text and the thought-summary text.
///
/// <para>With <c>IncludeThoughts</c> on, Gemini sends its thought summary as text parts marked
/// <c>Thought = true</c>, usually before the answer and sometimes inside the same chunk. The old
/// readers took <c>chunk.Text ?? Parts[0].Text</c>; the SDK does not document whether
/// <c>chunk.Text</c> skips thought parts, and <c>Parts[0]</c> would be the thought itself, so either
/// would put the summary into the .tex output and into the continue history. Every reader goes
/// through this instead. Without thought parts the answer is all text parts in order - what
/// <c>chunk.Text</c> returned before.</para>
/// [Human] Trennt Antwort-Text und Gedanken-Text eines gestreamten Chunks.
/// </summary>
public static class ResponseParts {
    public static (string Answer, string Thought) Split(GenerateContentResponse? chunk) {
        var parts = chunk?.Candidates is { Count: > 0 } candidates ? candidates[0]?.Content?.Parts : null;
        if (parts == null || parts.Count == 0) {
            return (chunk?.Text ?? "", "");
        }

        var answer = new StringBuilder();
        var thought = new StringBuilder();
        foreach (var part in parts) {
            if (string.IsNullOrEmpty(part?.Text)) {
                continue;
            }
            (part.Thought == true ? thought : answer).Append(part.Text);
        }
        return (answer.ToString(), thought.ToString());
    }
}
