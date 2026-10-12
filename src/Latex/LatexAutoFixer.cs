using System.Collections.Generic;
using LectureExtraction.Latex.AutoFix;

namespace LectureExtraction.Latex;

/// <summary>
/// [AI Context] HIGHLY EXPERIMENTAL. Runs our own deterministic LaTeX repairs after a failed PDF
/// compile, before any AI repair. Opt-in via PdfCompilation.ExperimentalLatexAutoFix; it rewrites the
/// .tex in place (the original survives as -orig.tex) and every change is printed with an
/// [EXPERIMENTELL] tag. Treat its output as a proposal that happened to compile, not as verified LaTeX.
///
/// <para>Why it exists: the AI repair loop sees only the first fatal error per round and has to
/// return the whole document, and it kept failing on a few recurring error classes. Each class is one
/// fix class in src/Latex/AutoFix. The playbook for humans and agents, including how to add a fix and
/// how to repair a PDF by hand, is docs/latex-auto-fix.md.</para>
///
/// <para>Layout:</para>
/// <code>
/// src/Latex/
/// ├── LatexAutoFixer.cs          ← runner: lines, comments, CRLF, report, fix order (this class)
/// ├── LatexScan.cs               ← shared scanning rules: escapes, comments, braces, optional args,
/// │                                 environments, verbatim (the "parsing tricks")
/// ├── LatexDiagnostics.cs        ← read-only findings with line numbers for console + AI repair prompt
/// └── AutoFix/
///     ├── ILatexLineFix.cs                    ← interface for line-local fixes: Description + FixLine(code)
///     ├── ILatexDocumentFix.cs                ← interface for whole-document fixes: Apply(tex)
///     ├── BraceNestedOptionalArgumentFix.cs   ← line fix: [Units in $R[x]$] → [{Units in $R[x]$}]
///     ├── TexorpdfstringBracketFix.cs         ← line fix: {R[[x]}}} → {R[[x]]}}
///     └── MisnamedEnvironmentEndFix.cs        ← document fix: \end{student} closing a speech → \end{speech},
///                                                with static switches per environment category
/// </code>
///
/// <para>Rule for growing this: a fix is written for an error class once it has actually occurred in a
/// lecture - not in advance. Missing, run-over and stray \end{...} are therefore NOT auto-repaired yet;
/// LatexDiagnostics reports them with line numbers to the AI repair until one of them shows up.</para>
///
/// <para>The runner owns everything the fixes share: splitting into lines (CRLF survives, since the
/// '\r' stays on its line), keeping comments out of reach of line fixes (they see only the code before
/// the first unescaped %), running document fixes after line fixes, and the report. Adding a fix is one
/// class, one entry in <see cref="DefaultLineFixes"/> or <see cref="DefaultDocumentFixes"/>, and one test
/// class - nothing here changes.</para>
/// [Human] HOCHGRADIG EXPERIMENTELL: führt die eigenen LaTeX-Korrekturen vor der KI-Reparatur aus.
/// </summary>
public static class LatexAutoFixer {
    /// <summary>
    /// Line fixes in the order they run on each line. Order matters where one error masks another:
    /// the \texorpdfstring repair removes the surplus brace that would otherwise stop the
    /// optional-argument scan from seeing its line at all.
    /// </summary>
    public static IReadOnlyList<ILatexLineFix> DefaultLineFixes { get; } = [
        new TexorpdfstringBracketFix(),
        new BraceNestedOptionalArgumentFix(),
        new TrailingDollarAfterParenFix(),
        new UnclosedEnvironmentEndFix(),
        // before InlineMetaNoteMathFix: it acts only on a line with an odd number of '$', so it never
        // touches a line that compiled, and a dollar that fix adds cannot set it off
        new UnclosedInlineMathBeforeProseFix(),
        new InlineMetaNoteMathFix(),
        new BareHiddenOpeningFix(),
        new UndefinedMathCommandFix(),
        new HighlightOptionalColorFix(),
    ];

    /// <summary>Document fixes, run after all line fixes in this order.</summary>
    public static IReadOnlyList<ILatexDocumentFix> DefaultDocumentFixes { get; } = [
        new MisnamedEnvironmentEndFix(),
        new SurplusBraceAfterMetaNoteFix(),
    ];

    /// <summary>The repaired text and one German, user-facing line per change.</summary>
    public sealed record Result(string Text, IReadOnlyList<string> Changes) {
        public bool Changed => Changes.Count > 0;
    }

    public static Result Apply(string tex) => Apply(tex, DefaultLineFixes, DefaultDocumentFixes);

    public static Result Apply(string tex, IReadOnlyList<ILatexLineFix> lineFixes, IReadOnlyList<ILatexDocumentFix>? documentFixes = null) {
        string[] lines = tex.Split('\n');
        var changes = new List<string>();

        for (int i = 0; i < lines.Length; i++) {
            int commentStart = LatexScan.FindCommentStart(lines[i]);
            string code = commentStart < 0 ? lines[i] : lines[i][..commentStart];
            string comment = commentStart < 0 ? "" : lines[i][commentStart..];

            var applied = new List<string>();
            foreach (ILatexLineFix fix in lineFixes) {
                string fixedCode = fix.FixLine(code);
                if (fixedCode == code) continue;
                applied.Add(fix.Description);
                code = fixedCode;
            }

            if (applied.Count == 0) continue;
            lines[i] = code + comment;
            changes.Add($"Zeile {i + 1}: {string.Join(", ", applied)}: {lines[i].TrimEnd('\r').Trim()}");
        }

        string text = string.Join('\n', lines);
        foreach (ILatexDocumentFix fix in documentFixes ?? []) {
            DocumentFixResult result = fix.Apply(text);
            if (result.Changes.Count == 0) continue;
            text = result.Text;
            changes.AddRange(result.Changes);
        }

        return new Result(text, changes);
    }
}
