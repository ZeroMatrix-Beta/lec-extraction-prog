using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex;

public enum LectureLanguage { Unknown, German, English }

/// <summary>
/// [AI Context] Decides whether a transcript is a German or an English lecture, so the PDF build can
/// pick the matching preamble (theorem names, babel hyphenation, box titles). First choice is the
/// `% Primary Language: ...` line that Stage 2 must write at the top of the merged file. Without it
/// (a Stage 1 part, a hand-made file), the words decide: frequent German function words against frequent
/// English ones, counted over the whole text with commands removed. A lecture has thousands of them, so
/// the ratio is clear; below <see cref="MinStopwordHits"/> hits or a ratio under 2:1 the answer is Unknown.
/// [Human] Erkennt, ob ein Transkript deutsch oder englisch ist (Kopfzeile, sonst Häufigkeit von Füllwörtern).
/// </summary>
public static partial class LectureLanguageDetector {
    public const int MinStopwordHits = 40;

    private static readonly HashSet<string> GermanWords = new(StringComparer.Ordinal) {
        "der", "die", "das", "und", "ist", "nicht", "wir", "ein", "eine", "zu", "mit", "auf", "für",
        "von", "den", "dem", "sich", "auch", "es", "ich", "dass", "wenn", "aber", "hier", "jetzt",
    };

    private static readonly HashSet<string> EnglishWords = new(StringComparer.Ordinal) {
        "the", "and", "is", "of", "to", "we", "in", "that", "this", "it", "for", "not", "are", "with",
        "be", "you", "so", "have", "can", "if", "but", "here", "now", "what", "they",
    };

    public static LectureLanguage Detect(string tex) {
        LectureLanguage declared = FromHeader(tex);
        return declared != LectureLanguage.Unknown ? declared : FromWords(tex);
    }

    /// <summary>The language named by the first `% Primary Language:` line, or Unknown.</summary>
    public static LectureLanguage FromHeader(string tex) {
        Match match = PrimaryLanguageRegex().Match(tex);
        if (!match.Success) return LectureLanguage.Unknown;
        return match.Groups[1].Value.ToLowerInvariant() switch {
            "german" or "deutsch" => LectureLanguage.German,
            "english" or "englisch" => LectureLanguage.English,
            _ => LectureLanguage.Unknown,
        };
    }

    /// <summary>German vs. English function words; Unknown when too few or too close.</summary>
    public static LectureLanguage FromWords(string tex) {
        string prose = CommandRegex().Replace(tex, " ");
        int german = 0, english = 0;
        foreach (Match word in WordRegex().Matches(prose)) {
            string w = word.Value.ToLowerInvariant();
            if (GermanWords.Contains(w)) german++;
            else if (EnglishWords.Contains(w)) english++;
        }
        if (german + english < MinStopwordHits) return LectureLanguage.Unknown;
        if (german >= 2 * english) return LectureLanguage.German;
        if (english >= 2 * german) return LectureLanguage.English;
        return LectureLanguage.Unknown;
    }

    [GeneratedRegex(@"^\s*%\s*Primary Language:\s*([A-Za-z]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PrimaryLanguageRegex();

    [GeneratedRegex(@"\\[A-Za-z@]+\*?")]
    private static partial Regex CommandRegex();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex WordRegex();
}
