using System;

namespace LectureExtraction.Latex.AutoFix;

/// <summary>
/// [AI Context] <c>\texorpdfstring{TeX}{plain}</c> whose plain-text argument lost a ']' that came
/// out as a surplus '}': <c>{R[[x]}}}]</c> instead of <c>{R[[x]]}}]</c>. The extra brace closes a
/// group that was never opened ("Argument of \environment content has an extra }"). When
/// <c>plain</c> has more '[' than ']' and the line has surplus closing braces directly after it,
/// the missing brackets go back into <c>plain</c> and as many surplus braces are dropped.
/// [Human] Repariert den Klartext-Teil von \texorpdfstring, wenn aus einem ']' ein '}' wurde.
/// </summary>
public sealed class TexorpdfstringBracketFix : ILatexLineFix {
    private const string Command = "\\texorpdfstring{";

    public string Description => "\\texorpdfstring-Klartext repariert";

    public string FixLine(string code) {
        int searchFrom = 0;
        while (true) {
            int start = code.IndexOf(Command, searchFrom, StringComparison.Ordinal);
            if (start < 0) return code;

            int firstOpen = start + Command.Length - 1;
            int firstClose = LatexScan.FindMatchingBrace(code, firstOpen);
            int secondOpen = firstClose + 1;
            if (firstClose < 0 || secondOpen >= code.Length || code[secondOpen] != '{') {
                searchFrom = start + 1;
                continue;
            }

            int secondClose = LatexScan.FindMatchingBrace(code, secondOpen);
            if (secondClose < 0) return code;

            string plain = code[(secondOpen + 1)..secondClose];
            int missingBrackets = LatexScan.CountUnescaped(plain, '[') - LatexScan.CountUnescaped(plain, ']');
            int surplusBraces = LatexScan.CountUnescaped(code, '}') - LatexScan.CountUnescaped(code, '{');
            int followingBraces = 0;
            while (secondClose + 1 + followingBraces < code.Length && code[secondClose + 1 + followingBraces] == '}') {
                followingBraces++;
            }

            int moved = Math.Min(missingBrackets, Math.Min(surplusBraces, followingBraces));
            if (moved <= 0) {
                searchFrom = secondClose + 1;
                continue;
            }

            code = code[..secondClose] + new string(']', moved) + "}" + code[(secondClose + 1 + moved)..];
            searchFrom = secondClose + moved + 1;
        }
    }
}
