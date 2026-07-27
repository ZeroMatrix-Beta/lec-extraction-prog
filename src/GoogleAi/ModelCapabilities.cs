using System;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Was copy-pasted byte-identically 5x (both extraction sessions, both chat
/// sessions, LatexRefinementSession) before being consolidated here.
/// [Human] Prueft, ob ein Gemini-Modell die "Thinking"-Parameter unterstuetzt. War vorher 5x dupliziert.
/// </summary>
public static class ModelCapabilities {
    public static bool SupportsThinking(string modelName) {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        return modelName.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) ||
               modelName.Contains("thinking", StringComparison.OrdinalIgnoreCase);
    }
}
