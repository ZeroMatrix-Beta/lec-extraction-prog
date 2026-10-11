using System.Text;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex;

/// <summary>
/// [AI Context] Shrinks a .tex file before it is inlined as read-only reference context: drops every
/// `hidden` environment and every LaTeX comment. Those carry the transcriber's own bookkeeping (the
/// part header, chain-of-thought comments, checkpoint blocks) - wanted in the output, dead weight as
/// context for a later part. Lines that held only a comment disappear; blank lines the document had
/// stay, so paragraph breaks survive. An escaped \% is text, not a comment.
/// [Human] Entfernt Kommentare und hidden-Umgebungen aus einer .tex-Datei, bevor sie als Kontext
/// eingebettet wird.
/// </summary>
public static partial class LatexCommentStripper {
    public static string StripCommentsAndHiddenBlocks(string tex) {
        string withoutHidden = HiddenEnvironmentRegex().Replace(tex, "");

        var builder = new StringBuilder(withoutHidden.Length);
        foreach (string rawLine in withoutHidden.Split('\n')) {
            string line = rawLine.TrimEnd('\r');
            int commentStart = LatexScan.FindCommentStart(line);
            if (commentStart < 0) {
                builder.Append(line).Append('\n');
                continue;
            }

            string code = line[..commentStart].TrimEnd();
            if (code.Length > 0) {
                builder.Append(code).Append('\n');
            }
        }

        string collapsed = ExcessBlankLinesRegex().Replace(builder.ToString(), "\n\n");
        return collapsed.TrimStart('\n').TrimEnd() + "\n";
    }

    [GeneratedRegex(@"[ \t]*\\begin\{hidden\}.*?\\end\{hidden\}[ \t]*(?:\r?\n)?", RegexOptions.Singleline)]
    private static partial Regex HiddenEnvironmentRegex();

    [GeneratedRegex(@"\n(?:[ \t]*\n){2,}")]
    private static partial Regex ExcessBlankLinesRegex();
}
