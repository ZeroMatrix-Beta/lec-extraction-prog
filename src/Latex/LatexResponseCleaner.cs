using System.Text;
using System.Text.RegularExpressions;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Latex;

/// <summary>
/// [AI Context] Strips markdown code-fence wrappers and system-chatter markers from a raw model
/// response, so what remains is compilable LaTeX. Regex-based cleanup ensures that even if the
/// output is split across multiple continuation chunks, all markdown blocks and system messages
/// are fully stripped, preventing compilation errors.
/// </summary>
public static partial class LatexResponseCleaner {
    public static string CleanLatexResponse(string rawResponse) {
        string cleanTex = rawResponse;

        // We use a non-greedy regex to capture everything inside the blocks, allowing multiple blocks.
        var matches = MyRegex().Matches(cleanTex);
        if (matches.Count > 0) {
            var sb = new StringBuilder();
            foreach (Match match in matches) {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(match.Groups[1].Value);
            }
            cleanTex = sb.ToString();
        }

        // Always strip any remaining markdown code block markers (even if we extracted a block),
        // because the model might have split the LaTeX code into multiple consecutive markdown blocks.
        cleanTex = LatexBlockRegex().Replace(cleanTex, "");
        cleanTex = CodeBlockRegex().Replace(cleanTex, "");

        // Fuzzy regex to catch variations like "**[SYSTEM] Segment complete.**" with leading spaces or bold markers
        // Updated to use Source-Generated Regex to improve performance and resolve IDE warnings
        cleanTex = SystemMessageRegex().Replace(cleanTex, "");
        return cleanTex.Trim().FixMalformedEndTags();
    }

    [GeneratedRegex(@"```(?:latex|tex)?\s*\n(.*?)\n```", RegexOptions.IgnoreCase | RegexOptions.Singleline, "de-CH")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"```(?:latex|tex)?\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex LatexBlockRegex();

    [GeneratedRegex(@"```\r?\n?")]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"(?im)^[ \t]*(?:\*|_|%)*[ \t]*\[(?:SYSTEM|AI-MODEL)[^\]]*\][^\r\n]*(?:Segment|Video)\s*complete[^\r\n]*\r?\n?")]
    private static partial Regex SystemMessageRegex();
}
