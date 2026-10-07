using System.Text;
using System.Text.Json;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] Pulls the answer text out of a <c>v1beta/interactions</c> (Antigravity agent)
/// response. Pure and side-effect free so the shapes it accepts are pinned by tests rather than
/// rediscovered against the live API.
///
/// <para>Accepted, in this order: a top-level <c>output_text</c> string; a <c>steps</c> array whose
/// non-thought steps carry text in <c>content[].text</c>, <c>parts[].text</c> or <c>text</c>; an
/// <c>outputs[].text</c> array. Steps of type <c>thought</c> hold the agent's reasoning summary, not
/// LaTeX, and are skipped. Fenced ```latex blocks and leftover prose are handled afterwards by
/// <see cref="Latex.LatexResponseCleaner"/>.</para>
/// [Human] Liest den Antworttext aus der Antwort des Antigravity-Agenten (ohne Denkschritte).
/// </summary>
public static class AntigravityResponseParser {
    public static string ExtractText(JsonElement root) {
        if (root.ValueKind != JsonValueKind.Object) {
            return "";
        }

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String) {
            string text = outputText.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(text)) {
                return text;
            }
        }

        var sb = new StringBuilder();
        if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array) {
            foreach (var step in steps.EnumerateArray()) {
                if (step.ValueKind != JsonValueKind.Object || IsThought(step)) {
                    continue;
                }
                if (!AppendTexts(sb, step, "content") && !AppendTexts(sb, step, "parts")) {
                    AppendText(sb, step);
                }
            }
        }

        if (sb.Length == 0 && root.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array) {
            foreach (var output in outputs.EnumerateArray()) {
                AppendText(sb, output);
            }
        }

        return sb.ToString();
    }

    public static string ExtractText(string responseJson) {
        using var document = JsonDocument.Parse(responseJson);
        return ExtractText(document.RootElement);
    }

    private static bool IsThought(JsonElement step) =>
        step.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), "thought", StringComparison.OrdinalIgnoreCase);

    /// <summary>Appends the text of every element of the array property; false if there is no such array.</summary>
    private static bool AppendTexts(StringBuilder sb, JsonElement owner, string arrayProperty) {
        if (!owner.TryGetProperty(arrayProperty, out var array) || array.ValueKind != JsonValueKind.Array) {
            return false;
        }
        foreach (var item in array.EnumerateArray()) {
            AppendText(sb, item);
        }
        return true;
    }

    private static void AppendText(StringBuilder sb, JsonElement item) {
        if (item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String) {
            sb.AppendLine(text.GetString());
        }
    }
}
