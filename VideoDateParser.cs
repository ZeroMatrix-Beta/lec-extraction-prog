using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Helper class to parse date and weekday information from video filenames.
/// Supports flexible formats like "MM-DD-YYYY-weekday...", "MM-DD-YY-...", and "YYYY-MM-DD-...".
/// </summary>
public static partial class VideoDateParser {
    public class VideoDateInfo {
        public DateTime Date { get; init; }
        public string? Weekday { get; init; }
        public string DateString { get; init; } = "";
    }

    // Matches MM-DD-YYYY or MM-DD-YY followed by optional token
    [GeneratedRegex(@"^(\d{2})-(\d{2})-(\d{4}|\d{2})(?:[-_]([a-zA-Z]+))?(?:[-_.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MmDdYyyyRegex();

    // Matches YYYY-MM-DD followed by optional token
    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})(?:[-_]([a-zA-Z]+))?(?:[-_.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex YyyyMmDdRegex();

    public static VideoDateInfo Parse(string filePath) {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);

        int year, month, day;
        string? token = null;

        Match matchYyyy = YyyyMmDdRegex().Match(fileNameWithoutExtension);
        Match matchMmDd = MmDdYyyyRegex().Match(fileNameWithoutExtension);

        if (matchYyyy.Success) {
            year = int.Parse(matchYyyy.Groups[1].Value);
            month = int.Parse(matchYyyy.Groups[2].Value);
            day = int.Parse(matchYyyy.Groups[3].Value);
            if (matchYyyy.Groups[4].Success) {
                token = matchYyyy.Groups[4].Value;
            }
        }
        else if (matchMmDd.Success) {
            month = int.Parse(matchMmDd.Groups[1].Value);
            day = int.Parse(matchMmDd.Groups[2].Value);
            int rawYear = int.Parse(matchMmDd.Groups[3].Value);
            year = rawYear < 100 ? 2000 + rawYear : rawYear;
            if (matchMmDd.Groups[4].Success) {
                token = matchMmDd.Groups[4].Value;
            }
        }
        else {
            Console.WriteLine($"[Warning] Date format mismatch for file '{fileNameWithoutExtension}': Expected format MM-DD-YYYY or YYYY-MM-DD at the beginning.");
            return new() {
                Date = DateTime.MinValue,
                Weekday = null,
                DateString = fileNameWithoutExtension
            };
        }

        TryParseWeekday(token, out string? weekday);

        DateTime parsedDate;
        try {
            parsedDate = new DateTime(year, month, day);
        }
        catch (ArgumentOutOfRangeException ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
            Console.WriteLine($"[Warning] Invalid date values ({year:D4}-{month:D2}-{day:D2}) in filename '{fileNameWithoutExtension}'.");
            return new() {
                Date = DateTime.MinValue,
                Weekday = weekday,
                DateString = fileNameWithoutExtension
            };
        }

        string parsedDateStringForInfo = $"{year:D4}-{month:D2}-{day:D2}";

        return new() {
            Date = parsedDate,
            Weekday = weekday,
            DateString = parsedDateStringForInfo
        };
    }

    private static bool TryParseWeekday(string? token, out string? normalizedWeekday) {
        if (string.IsNullOrWhiteSpace(token)) {
            normalizedWeekday = null;
            return false;
        }

        string lower = token.ToLowerInvariant();
        if (lower is "monday" or "tuesday" or "wednesday" or "thursday" or "friday" or "saturday" or "sunday" or
                     "montag" or "dienstag" or "mittwoch" or "donnerstag" or "freitag" or "samstag" or "sonntag") {
            normalizedWeekday = char.ToUpperInvariant(lower[0]) + lower.Substring(1);
            return true;
        }

        normalizedWeekday = null;
        return false;
    }
}