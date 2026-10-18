using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] A <c>table</c> or <c>figure</c> float inside one of the transcript's boxes (<c>content</c>,
/// <c>highlight</c>, <c>steps</c>, ... - all tcolorboxes) stops pdflatex with "LaTeX Error: Not in outer
/// par mode": a float cannot be placed inside a box. Seen in Information Security 2024-02-29 (step 4,
/// <c>\begin{table}[h!]</c> inside <c>content</c>), which then needed an AI repair round. The fix turns
/// the float into a <c>center</c> block in place - <c>\begin{table}[h!]</c> becomes <c>\begin{center}</c>,
/// its <c>\end</c> becomes <c>\end{center}</c> - and a <c>\caption{...}</c> inside it becomes
/// <c>\captionof{table}{...}</c> (capt-of is loaded by every pipeline preamble). A float outside every box
/// compiles and is left alone.
/// [Human] Macht aus einem table/figure in einer Box einen center-Block, weil Floats in Boxen nicht gehen.
/// </summary>
public sealed partial class FloatInBoxFix : ILatexDocumentFix {
    /// <summary>The transcript environments that are boxes: a float may not stand inside them.</summary>
    public static IReadOnlySet<string> BoxEnvironments { get; } = new HashSet<string>(StringComparer.Ordinal) {
        "speech", "content", "highlight", "overview", "note", "aiNote", "insight", "student", "pause",
        "steps", "proofWrapper", "proof", "recap"
    };

    private static readonly HashSet<string> Floats = new(StringComparer.Ordinal) { "table", "table*", "figure", "figure*" };

    public DocumentFixResult Apply(string tex) {
        List<LatexScan.EnvironmentToken> tokens = LatexScan.EnvironmentTokens(tex);
        var edits = new List<(int Start, int Length, string Text)>();
        var changes = new List<string>();
        var stack = new List<(string Name, int Token)>();

        for (int i = 0; i < tokens.Count; i++) {
            LatexScan.EnvironmentToken t = tokens[i];
            if (t.IsBegin) {
                stack.Add((t.Name, i));
                continue;
            }
            int open = stack.FindLastIndex(s => s.Name == t.Name);
            if (open < 0) continue;
            var (name, beginIndex) = stack[open];
            stack.RemoveRange(open, stack.Count - open);
            if (!Floats.Contains(name)) continue;
            bool insideBox = stack.Exists(s => BoxEnvironments.Contains(s.Name));
            if (!insideBox) continue;

            LatexScan.EnvironmentToken begin = tokens[beginIndex];
            // the placement argument [h!] right after \begin{table} goes with it
            int beginEnd = begin.Index + begin.Length;
            Match placement = PlacementRegex().Match(tex, beginEnd);
            int beginLength = begin.Length + (placement.Success && placement.Index == beginEnd ? placement.Length : 0);
            string kind = name.TrimEnd('*');
            edits.Add((begin.Index, beginLength, "\\begin{center}"));
            int bodyStart = begin.Index + beginLength;
            foreach (Match caption in CaptionRegex().Matches(tex[bodyStart..t.Index])) {
                edits.Add((bodyStart + caption.Index, caption.Length, $"\\captionof{{{kind}}}{{"));
            }
            edits.Add((t.Index, t.Length, "\\end{center}"));
            changes.Add($"Zeile {begin.Line}: \\begin{{{name}}} in einer Box durch \\begin{{center}} ersetzt (Float in Box)");
        }
        if (edits.Count == 0) return new DocumentFixResult(tex, []);

        edits.Sort((a, b) => b.Start.CompareTo(a.Start));
        string result = tex;
        foreach (var (start, length, text) in edits) {
            result = result[..start] + text + result[(start + length)..];
        }
        return new DocumentFixResult(result, changes);
    }

    [GeneratedRegex(@"\G\s*\[[^\]\n]*\]")]
    private static partial Regex PlacementRegex();

    [GeneratedRegex(@"\\caption\{")]
    private static partial Regex CaptionRegex();
}
