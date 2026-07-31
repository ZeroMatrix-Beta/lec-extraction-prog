using System;
using System.Collections.Generic;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// Where a prompt's answer comes from. <see cref="Ui"/> keeps the presentation and the
/// back/cancel bookkeeping; this decides only *what the user said*.
///
/// <para>The seam exists so the pipeline can run headless. Every decision in this app is a Spectre
/// prompt, and Spectre throws outright when the terminal is not interactive - so without this, a
/// scripted run dies at the first menu instead of at the first thing it actually cannot answer.</para>
/// </summary>
public interface IPromptSource {
    /// <summary>
    /// Picks one of <paramref name="labels"/> and returns its index. Labels arrive unescaped and in
    /// display order, including any back/cancel entries the caller appended.
    /// </summary>
    int SelectIndex(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText);

    /// <summary>
    /// Ticks any number of <paramref name="labels"/> and returns their indices, ascending. An empty
    /// result is meaningful - callers read it as "back".
    /// </summary>
    IReadOnlyList<int> SelectManyIndices(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText, string? instructionsText);

    bool Confirm(string question, bool defaultYes);

    string AskText(string question, string? defaultValue);

    T AskValue<T>(string question, T defaultValue);
}

/// <summary>
/// Thrown when an unattended run reaches a question it has no answer for.
///
/// <para>The alternative - quietly taking a default - is how an automated run buys the wrong model
/// or transcribes the wrong folder. Failing here costs a re-run; guessing costs money. The message
/// carries the prompt's own title so the caller is told *which* question blocked it, not merely
/// that one did.</para>
/// </summary>
public sealed class UnattendedPromptException(string promptTitle, string? hint = null)
    : Exception(BuildMessage(promptTitle, hint)) {

    public string PromptTitle { get; } = promptTitle;

    private static string BuildMessage(string promptTitle, string? hint) {
        string suffix = hint == null ? "" : $" {hint}";
        return $"No answer available for the prompt \"{promptTitle}\" in unattended mode.{suffix}";
    }
}
