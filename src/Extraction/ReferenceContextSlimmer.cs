using System.Text.RegularExpressions;
using LectureExtraction.Configuration;
using LectureExtraction.Latex;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Shrinks a preceding part before it is inlined as reference context. Each call gets a
/// ReferenceSlimProfile saying what goes: comments and `hidden` blocks, environments removed begin to
/// end (their `\label`s survive), environments emptied to a `% tokenOpt` marker, and commands removed
/// with their argument. The session picks the profile per file (ReferenceSlimmingConfig: OlderParts or
/// PrecedingPart). The reference context exists so the model knows what was already transcribed and
/// which labels exist; spoken text, didactic boxes and figures are its bulk and carry little of that.
/// Sectioning, `content` and theorem-like environments are never touched by the code - only what the
/// profile names.
///
/// Transcripts can be broken LaTeX. An environment is matched only up to its own `\end`, never across
/// a second `\begin` of the same name, so an unclosed block is left as it is instead of swallowing
/// the text after it.
///
/// The comment stripping runs first, which would otherwise remove the markers again. The preamble's
/// rule 5 (RuleText) tells the model, so it does not imitate the gaps.
/// [Human] Kürzt Vorgänger-Teile für den Kontext nach einem Profil pro Aufruf. Ungeschlossene
/// Umgebungen bleiben stehen.
/// </summary>
public static partial class ReferenceContextSlimmer {
    public const string Marker = "% tokenOpt";

    public static string Slim(string tex, ReferenceSlimProfile profile) {
        string result = profile.StripCommentsAndHidden ? LatexCommentStripper.StripCommentsAndHiddenBlocks(tex) : tex;
        foreach (string env in profile.EmptyEnvironments) {
            // The begin line keeps its optional argument; only the body goes (its labels stay).
            result = EnvironmentRegex(env, withArgument: true).Replace(result, m =>
                string.Join("\n", [m.Groups["begin"].Value, Marker, .. Labels(m.Value), $"\\end{{{env}}}"]));
        }
        foreach (string env in profile.RemoveEnvironments) {
            result = EnvironmentRegex(env, withArgument: false).Replace(result, m =>
                string.Join("\n", profile.MarkRemovedEnvironments ? [$"{Marker} ({env})", .. Labels(m.Value)] : Labels(m.Value)));
        }
        foreach (string command in profile.RemoveCommands) {
            // One level of nested braces is enough for \inlinemetanote{... \textit{x} ...}.
            result = Regex.Replace(result, $@"[ \t]*\\{Regex.Escape(command)}\{{(?:[^{{}}]|\{{[^{{}}]*\}})*\}}", "");
        }
        if (profile.RemoveEnvironments.Length > 0 || profile.RemoveCommands.Length > 0) {
            result = EmptyEnvironmentRegex().Replace(result, "");
            result = TrailingSpaceRegex().Replace(result, "");
            result = BlankLinesRegex().Replace(result, "$1$1");
        }
        return result;
    }

    /// <summary>
    /// [AI Context] The preamble's rule 5 for these profiles, or "" when neither removes anything. Built
    /// from the lists, so it names exactly what is missing and stays identical across one session's
    /// requests (cache prefix). A profile's comment stripping is not repeated when rule 4 already says so.
    /// [Human] Regel 5 der Kontext-Präambel, aus den Profilen erzeugt.
    /// </summary>
    /// <param name="older">Profile of the older parts, or null when they are not slimmed.</param>
    public static string RuleText(ReferenceSlimProfile? older, ReferenceSlimProfile preceding, bool commentsAlreadyStated) {
        string olderText = older is null ? "" : Describe(older, commentsAlreadyStated);
        string precedingText = Describe(preceding, commentsAlreadyStated);
        if (olderText == "" && precedingText == "") return "";

        var parts = new List<string>();
        if (precedingText != "") parts.Add($"in the most recent context file {precedingText}");
        if (olderText != "") parts.Add($"in every {(precedingText == "" ? "context file except the most recent one" : "older context file")} {olderText}");
        return "5. To save tokens, " + string.Join("; ", parts) + ". " +
               "Wherever something is missing, that was done on purpose: these files show what was already transcribed and which labels exist, nothing more. " +
               "They are NOT a style example, and the gaps are not to be imitated. Your own transcription must still contain every " +
               "environment, box, figure, comment and complete `speech` environment the system instructions require.\n";
    }

    private static string Describe(ReferenceSlimProfile profile, bool commentsAlreadyStated) {
        var removed = new List<string>();
        if (profile.StripCommentsAndHidden && !commentsAlreadyStated)
            removed.Add("all LaTeX comments and `hidden` environments may or may not have been removed");
        if (profile.RemoveEnvironments.Length > 0)
            removed.Add("every " + Join(profile.RemoveEnvironments.Select(e => $"`{e}`")) + " environment may or may not have been removed entirely" +
                        (profile.MarkRemovedEnvironments ? " (replaced by `% tokenOpt (name)`)" : ""));
        if (profile.EmptyEnvironments.Length > 0)
            removed.Add("the content of each " + Join(profile.EmptyEnvironments.Select(e => $"`{e}`")) + " environment may or may not have been replaced by `% tokenOpt`");
        if (profile.RemoveCommands.Length > 0)
            removed.Add("every " + Join(profile.RemoveCommands.Select(c => $"`\\{c}`")) + " may or may not have been removed");
        return Join(removed);
    }

    private static string[] Labels(string block) => LabelRegex().Matches(block).Select(l => l.Value).ToArray();

    private static string Join(IEnumerable<string> items) {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }

    private static Regex EnvironmentRegex(string env, bool withArgument) {
        string begin = $@"\\begin\{{{Regex.Escape(env)}\}}";
        string end = $@"\\end\{{{Regex.Escape(env)}\}}";
        string argument = withArgument ? @"(?:\[[^\]\n]*\])?" : "";
        // Tempered body: never crosses another \begin of the same environment (an unclosed block stays).
        return new Regex($@"(?<begin>{begin}{argument})(?:(?!{begin}).)*?{end}", RegexOptions.Singleline);
    }

    [GeneratedRegex(@"\\label(?:\[[^\]\n]*\])?\{[^{}\n]*\}")]
    private static partial Regex LabelRegex();

    // A wrapper left empty by a removal, e.g. \begin{center}\end{center} around a removed tikzpicture.
    [GeneratedRegex(@"\\begin\{(\w+)\}(?:\[[^\]\n]*\])?\s*\\end\{\1\}")]
    private static partial Regex EmptyEnvironmentRegex();

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline)]
    private static partial Regex TrailingSpaceRegex();

    // Collapses the blank lines a removal leaves behind to one; keeps the file's line ending.
    [GeneratedRegex(@"(\r?\n)(?:[ \t]*\r?\n){2,}")]
    private static partial Regex BlankLinesRegex();
}
