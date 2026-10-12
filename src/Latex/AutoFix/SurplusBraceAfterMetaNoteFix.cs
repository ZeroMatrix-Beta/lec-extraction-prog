using System.Collections.Generic;
using System.Text;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Removes a '}' that closes nothing ("Too many }'s") when it sits directly after the
/// closing brace of an <c>\inlinemetanote{...}</c>. Seen in week 11 of the spring-2025 Grundstrukturen
/// lecture: a note with set braces, <c>\inlinemetanote{schreibt $E = \{\{x, y\} \mid ...\}$ ... \qt{...}}}</c>,
/// where the model lost count of the braces and wrote one too many.
///
/// <para>A document fix, not a line fix, on purpose: whether a '}' closes nothing depends on every '{'
/// before it, and a group may open lines earlier (a <c>\footnote{</c> whose text ends with a note and
/// its own '}'). The brace depth is counted over the whole document, outside comments and verbatim
/// content (<see cref="LatexScan.CodeSegments"/>), so only a '}' that cannot compile is removed.</para>
/// [Human] Entfernt eine überzählige } direkt nach \inlinemetanote{...}, wenn sie keine Gruppe schliesst.
/// </summary>
public sealed class SurplusBraceAfterMetaNoteFix : ILatexDocumentFix {
    private const string Note = @"\inlinemetanote{";

    public DocumentFixResult Apply(string tex) {
        var removals = new List<int>();
        var changes = new List<string>();
        int depth = 0;

        foreach (LatexScan.CodeSegment segment in LatexScan.CodeSegments(tex)) {
            string code = segment.Text;
            for (int i = 0; i < code.Length; i++) {
                char c = code[i];
                if (c == '\\') { i++; continue; }
                if (c == '{') { depth++; continue; }
                if (c != '}') continue;
                if (depth > 0) { depth--; continue; }

                // This '}' closes nothing. Only the one right after a note's own closing brace is ours.
                if (!ClosesMetaNote(code, i - 1)) continue;
                removals.Add(segment.Start + i);
                changes.Add($"Zeile {segment.Line}: überzählige }} nach \\inlinemetanote entfernt");
            }
        }

        if (removals.Count == 0) return new DocumentFixResult(tex, []);

        var text = new StringBuilder(tex);
        for (int k = removals.Count - 1; k >= 0; k--) text.Remove(removals[k], 1);
        return new DocumentFixResult(text.ToString(), changes);
    }

    /// <summary>True when the '}' at <paramref name="close"/> ends an <c>\inlinemetanote{...}</c> on this line.</summary>
    private static bool ClosesMetaNote(string code, int close) {
        if (close < 0 || code[close] != '}') return false;
        for (int start = code.IndexOf(Note, System.StringComparison.Ordinal);
             start >= 0 && start < close;
             start = code.IndexOf(Note, start + 1, System.StringComparison.Ordinal)) {
            if (LatexScan.IsEscaped(code, start)) continue;
            if (LatexScan.FindMatchingBrace(code, start + Note.Length - 1) == close) return true;
        }
        return false;
    }
}
