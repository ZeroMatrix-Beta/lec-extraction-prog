using System.Collections.Generic;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] A repair that needs the whole document rather than one line - for example pairing
/// <c>\begin</c>/<c>\end</c> across hundreds of lines. Same contract as <see cref="ILatexLineFix"/>:
/// change only what cannot compile, be idempotent, and report every change (German, with the line
/// number of the input) so the console shows what was touched. Comments and verbatim content must be
/// respected via <see cref="LatexScan.CodeSegments"/> / <see cref="LatexScan.EnvironmentTokens"/>.
/// [Human] Eine Korrektur, die das ganze Dokument braucht (z.B. Umgebungen paaren).
/// </summary>
public interface ILatexDocumentFix {
    DocumentFixResult Apply(string tex);
}

/// <summary>The repaired document and one report line per change; <paramref name="Text"/> is the input when nothing changed.</summary>
public sealed record DocumentFixResult(string Text, IReadOnlyList<string> Changes);
