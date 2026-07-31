using System.Collections.Generic;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// The unattended answer source. It answers exactly one class of question and refuses the rest.
///
/// <para>The rule is the value of this type: <b>a prompt that carries an explicit default value can
/// be auto-answered under <c>--yes</c>; a menu never can.</b> A default was written by whoever
/// wrote the prompt and is therefore a considered answer. A menu entry is not - picking one because
/// it happened to be listed first is how an automated run selects the wrong model, the wrong source
/// folder or the wrong API-key profile, and every one of those costs money before anyone notices.</para>
///
/// <para>So reaching a menu headlessly is a bug in the *caller*: the command should have supplied
/// that value as an argument instead of letting the flow ask. The exception names the question so
/// the missing argument is obvious.</para>
/// </summary>
public sealed class PresetPromptSource(bool assumeYes) : IPromptSource {
    private readonly bool _assumeYes = assumeYes;

    public int SelectIndex(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText) =>
        throw new UnattendedPromptException(
            title,
            $"A menu cannot be answered automatically ({labels.Count} choices). Pass the value as a command-line argument.");

    public IReadOnlyList<int> SelectManyIndices(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText, string? instructionsText) =>
        throw new UnattendedPromptException(
            title,
            $"A multi-select cannot be answered automatically ({labels.Count} choices). Pass the selection as a command-line argument.");

    public bool Confirm(string question, bool defaultYes) =>
        _assumeYes
            ? defaultYes
            : throw new UnattendedPromptException(question, $"Pass --yes to accept the default ({(defaultYes ? "Ja" : "Nein")}).");

    public string AskText(string question, string? defaultValue) {
        if (defaultValue != null && _assumeYes) {
            return defaultValue;
        }

        string hint = defaultValue == null
            ? "The question has no default; pass the value as a command-line argument."
            : $"Pass --yes to accept the default (\"{defaultValue}\").";
        throw new UnattendedPromptException(question, hint);
    }

    public T AskValue<T>(string question, T defaultValue) =>
        _assumeYes
            ? defaultValue
            : throw new UnattendedPromptException(question, $"Pass --yes to accept the default ({defaultValue}).");
}
