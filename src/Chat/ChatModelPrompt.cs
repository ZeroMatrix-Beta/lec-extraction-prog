using System.Collections.Generic;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] The <c>set model</c> command's model picker, shared by both chat sessions. Both
/// carried a byte-identical copy of the hand-numbered list, the "Bitte Modell auswählen (1-N):"
/// prompt and the index-or-freetext parsing - the last hand-numbered menu in <c>src/</c> after
/// Phase 10, and duplicated at that.
///
/// <para><see cref="Resolve"/> is separated from <see cref="Pick"/> for the reason
/// <see cref="ChatCommandParser"/> exists: the argument rule ("a number in range selects, anything
/// else is a literal model name") is the part that can be wrong silently, and it is now testable
/// without a live session.</para>
/// [Human] Gemeinsame Modell-Auswahl für beide Chat-Sitzungen: Liste plus Freitext-Eintrag.
/// </summary>
public static class ChatModelPrompt {
    /// <summary>Label of the entry that asks for a model name not in the list.</summary>
    private const string ManualEntryLabel = "✍️ Modellname manuell eingeben";

    /// <summary>
    /// [AI Context] Shows the configured models plus a freetext entry. Returns an empty string when
    /// the user backs out, which the callers read as "no change".
    /// [Human] Zeigt die Modell-Liste; leerer Rückgabewert heisst "nichts geändert".
    /// </summary>
    public static string Pick(IReadOnlyList<string> availableModels) {
        var choices = new List<(string Label, string Value)>();
        foreach (string model in availableModels) {
            choices.Add((model, model));
        }
        choices.Add((ManualEntryLabel, ""));

        var selection = Ui.Select("Verfügbare Modelle:", choices);
        if (!selection.IsValue) return "";

        if (!string.IsNullOrEmpty(selection.Value)) return selection.Value;

        return Ui.Ask("Bitte Modellnamen eingeben:").Trim();
    }

    /// <summary>
    /// [AI Context] Turns the command's argument into a model name: a 1-based index into
    /// <paramref name="availableModels"/> when it is one, and the argument itself otherwise - which
    /// is how a freetext name such as a Gemma build is reachable from <c>set model</c>.
    /// [Human] Wandelt das Argument in einen Modellnamen um - Zahl heisst Listenposition, sonst
    /// wird der Text direkt als Modellname verwendet.
    /// </summary>
    public static string Resolve(string arg, IReadOnlyList<string> availableModels) {
        if (int.TryParse(arg, out int index) && index >= 1 && index <= availableModels.Count) {
            return availableModels[index - 1];
        }
        return arg;
    }
}
