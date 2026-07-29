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

    /// <summary>
    /// [AI Context] True when the model rejects the dedicated <c>system</c> role and the system
    /// instruction must instead be prepended into the first user turn. Gemma before v4 is the only
    /// such family here.
    ///
    /// <para>This capability is unusually easy to lose: "gemma" appears in no <c>AvailableModels</c>
    /// array, so it is reachable only by typing the model name as freetext, which means no menu
    /// exercises it and no smoke test would notice it disappearing. It was duplicated verbatim in
    /// both chat sessions, expressed inline as
    /// <c>StartsWith("gemma") &amp;&amp; !Contains("gemma-4")</c> in the middle of a method that also
    /// reads session state - so it could not be tested without constructing a chat session. Pulled
    /// out here because it is what it always was: a pure function of the model name.</para>
    /// [Human] Gemma vor v4 kennt keine "system"-Rolle - die System-Instruction muss dann in die
    /// erste Nutzer-Nachricht wandern. Nur über Freitext-Modellnamen erreichbar, daher leicht zu
    /// verlieren und deshalb hier separat und getestet.
    /// </summary>
    public static bool RequiresSystemInstructionInFirstUserTurn(string modelName) {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        return modelName.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
            && !modelName.Contains("gemma-4", StringComparison.OrdinalIgnoreCase);
    }
}
