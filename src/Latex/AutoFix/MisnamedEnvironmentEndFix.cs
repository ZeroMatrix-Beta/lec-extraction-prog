using System;
using System.Collections.Generic;
using System.Text;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] An <c>\end{x}</c> written where the innermost open environment is a different one,
/// <c>y</c>, that never gets an <c>\end</c> of its own: the model closed <c>y</c> under the wrong name.
/// Seen in the 2026-09-22 algebra lecture: <c>\begin{speech}</c> ... <c>\end{student}</c>, and LaTeX
/// stops with "\begin{speech} on input line 1011 ended by \end{student}". The fix renames the end to
/// <c>\end{y}</c>.
///
/// <para>Deliberately narrow. Missing ends, run-over ends and stray ends are NOT repaired here - by
/// decision, a fix is written for an error class once it has actually occurred. Until then
/// LatexDiagnostics reports those with line numbers and the AI repair handles them. The rename only
/// happens when it is unambiguous: no environment called <c>x</c> is open at all, and the innermost
/// open <c>y</c> has no <c>\end</c> anywhere later (<see cref="LatexScan.MatchEnvironments"/>).</para>
///
/// <para>The static switches decide per category whether this fix may close an environment by renaming.
/// They are global state: tests that change them belong to the non-parallel AutoFixSettings collection
/// and call <see cref="RestoreDefaults"/>.</para>
/// [Human] Benennt ein falsch benanntes \end{...} um, damit es die offene Umgebung schliesst.
/// </summary>
public sealed class MisnamedEnvironmentEndFix : ILatexDocumentFix {
    public const string ProofWrapper = "proofWrapper";
    public const string Proof = "proof";

    /// <summary>Root-level containers of the transcription protocol (environments.md, environments-syntax-rules.md).</summary>
    public static IReadOnlySet<string> RootContainers { get; } = new HashSet<string>(StringComparer.Ordinal) {
        "speech", "content", "highlight", "overview", "note", "aiNote", "insight", "student", "pause"
    };

    /// <summary>The inner semantic classifiers of the transcription protocol (environments.md).</summary>
    public static IReadOnlySet<string> TheoremEnvironments { get; } = new HashSet<string>(StringComparer.Ordinal) {
        "definition", "theorem", "lemma", "corollary", "proposition", "axiom", "property", "principle", "fact",
        "claim", "remark", "observation", "example", "examples", "exercise", "notation", "question", "answer"
    };

    /// <summary>May close <c>speech</c>, <c>content</c>, <c>student</c>, ... (<see cref="RootContainers"/>).</summary>
    public static bool CloseRootContainers { get; set; } = true;

    /// <summary>May close a <c>proofWrapper</c>.</summary>
    public static bool CloseProofWrappers { get; set; } = true;

    /// <summary>May close a <c>proof</c>.</summary>
    public static bool CloseProofs { get; set; } = true;

    /// <summary>May close <c>definition</c>, <c>theorem</c>, <c>lemma</c>, ... (<see cref="TheoremEnvironments"/>).</summary>
    public static bool CloseTheoremEnvironments { get; set; } = true;

    /// <summary>May close every other environment (<c>itemize</c>, <c>align</c>, <c>steps</c>, ...).</summary>
    public static bool CloseOtherEnvironments { get; set; } = true;

    /// <summary>Sets every switch back to its default (all on).</summary>
    public static void RestoreDefaults() {
        CloseRootContainers = CloseProofWrappers = CloseProofs = CloseTheoremEnvironments = CloseOtherEnvironments = true;
    }

    /// <summary>Whether the switches allow closing an environment called <paramref name="name"/>.</summary>
    public static bool MayClose(string name) => name switch {
        ProofWrapper => CloseProofWrappers,
        Proof => CloseProofs,
        _ when RootContainers.Contains(name) => CloseRootContainers,
        _ when TheoremEnvironments.Contains(name) => CloseTheoremEnvironments,
        _ => CloseOtherEnvironments,
    };

    public DocumentFixResult Apply(string tex) {
        List<LatexScan.EnvironmentToken> tokens = LatexScan.EnvironmentTokens(tex);
        LatexScan.EnvironmentMatch match = LatexScan.MatchEnvironments(tokens);
        if (match.StrayEnds.Count == 0) return new DocumentFixResult(tex, []);

        var renames = new List<(LatexScan.EnvironmentToken End, string NewName)>();
        var changes = new List<string>();
        var stack = new List<int>();

        // Walk the pairing again; a rename pops the environment it closes, which leaves every later
        // pair exactly as MatchEnvironments saw it (the renamed environment sat below all of them).
        for (int i = 0; i < tokens.Count; i++) {
            LatexScan.EnvironmentToken token = tokens[i];
            if (token.IsBegin) {
                stack.Add(i);
                continue;
            }

            int position = stack.FindLastIndex(open => tokens[open].Name == token.Name);
            if (position >= 0) {
                stack.RemoveRange(position, stack.Count - position);
                continue;
            }

            if (stack.Count == 0 || !match.UnmatchedBegins.Contains(stack[^1])) continue;
            LatexScan.EnvironmentToken open = tokens[stack[^1]];
            if (!MayClose(open.Name)) continue;

            renames.Add((token, open.Name));
            changes.Add($"Zeile {token.Line}: \\end{{{token.Name}}} durch \\end{{{open.Name}}} ersetzt (schliesst \\begin{{{open.Name}}} aus Zeile {open.Line})");
            stack.RemoveAt(stack.Count - 1);
        }

        if (renames.Count == 0) return new DocumentFixResult(tex, []);

        var text = new StringBuilder(tex);
        for (int k = renames.Count - 1; k >= 0; k--) {
            var (end, newName) = renames[k];
            text.Remove(end.Index, end.Length).Insert(end.Index, $"\\end{{{newName}}}");
        }
        return new DocumentFixResult(text.ToString(), changes);
    }
}
