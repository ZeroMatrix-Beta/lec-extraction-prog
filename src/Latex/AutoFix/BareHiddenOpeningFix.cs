using System.Text.RegularExpressions;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] Replaces bare '\hidden' or '\hidden{' with '\begin{hidden}', which occurs when Gemini
/// writes '\hidden' as a command instead of opening the environment with '\begin{hidden}'.
/// Observed in 10-20-2025 ('\hidden') and 11-05-2025 ('\hidden{').
/// [Human] Ersetzt ein fehlerhaftes '\hidden' oder '\hidden{' durch '\begin{hidden}'.
/// </summary>
public sealed partial class BareHiddenOpeningFix : ILatexLineFix {
    public string Description => @"'\hidden' zu '\begin{hidden}' korrigiert";

    public string FixLine(string code) {
        if (!code.Contains(@"\hidden")) return code;
        return BareHiddenRegex().Replace(code, @"\begin{hidden}");
    }

    [GeneratedRegex(@"(?<!\\(?:begin|end)\{)\\hidden(?:\{(?:\})?)?(?=\s*$)")]
    private static partial Regex BareHiddenRegex();
}
