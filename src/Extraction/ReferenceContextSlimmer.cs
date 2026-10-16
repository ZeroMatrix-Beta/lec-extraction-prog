using System.Text.RegularExpressions;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Shrinks an older preceding part before it is inlined as reference context
/// (SlimOlderReferenceParts): the body of every `speech` environment and every `tikzpicture` is
/// replaced by a `% tokenOpt` marker. The reference context exists so the model knows what was
/// already transcribed and which labels exist; the spoken text and the figures are its bulk and
/// carry little of that. Only parts older than the most recent one are slimmed - the part directly
/// before the current one stays complete, because the current part continues it. Runs after the
/// comment stripping, which would otherwise remove the markers again. The preamble's rule 5 tells
/// the model, so it does not imitate empty speech blocks.
/// [Human] Kürzt ältere Vorgänger-Teile für den Kontext: speech-Inhalte und TikZ-Bilder werden durch
/// "% tokenOpt" ersetzt; der letzte Vorgänger-Teil bleibt vollständig.
/// </summary>
public static partial class ReferenceContextSlimmer {
    public const string Marker = "% tokenOpt";

    public static string Slim(string tex) {
        // The begin line keeps its optional arguments; only the body goes.
        string withoutSpeech = SpeechBodyRegex().Replace(tex, m => $"{m.Groups[1].Value}\n{Marker}\n{m.Groups[3].Value}");
        return TikzRegex().Replace(withoutSpeech, $"{Marker} (tikzpicture)");
    }

    [GeneratedRegex(@"(\\begin\{speech\}(?:\[[^\]\n]*\])?)(.*?)(\\end\{speech\})", RegexOptions.Singleline)]
    private static partial Regex SpeechBodyRegex();

    [GeneratedRegex(@"\\begin\{tikzpicture\}.*?\\end\{tikzpicture\}", RegexOptions.Singleline)]
    private static partial Regex TikzRegex();
}
