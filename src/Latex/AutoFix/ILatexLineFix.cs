namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] One deterministic repair for exactly one LaTeX error class, applied line by line by
/// <see cref="LatexAutoFixer"/>. The runner hands each fix only the code part of a line (comments are
/// split off and re-attached by the runner), so a fix never needs to know about comments.
///
/// <para>Contract: return the line unchanged unless it cannot compile as written; be idempotent
/// (running a fix on its own output changes nothing); stay on the line you were given. A future fix
/// that needs the whole document gets its own interface rather than stretching this one.</para>
/// [Human] Eine eigene LaTeX-Korrektur für genau eine Fehlerart, Zeile für Zeile.
/// </summary>
public interface ILatexLineFix {
    /// <summary>Short German label for the console report, e.g. "optionales Argument in {...} gesetzt".</summary>
    string Description { get; }

    /// <summary>The repaired line, or <paramref name="code"/> itself when nothing needed fixing.</summary>
    string FixLine(string code);
}
