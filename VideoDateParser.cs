using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AutoExtraction;

/// <summary>
/// Helper class to parse date and weekday information from video filenames.
/// Assumes a format like "YYYY-MM-DD-weekday.mp4" or "MM-DD-weekday.mp4".
/// </summary>
public static class VideoDateParser {
    public class VideoDateInfo {
        public DateTime Date { get; init; }
        public string Weekday { get; init; } = "";
        public string DateString { get; init; } = "";
    }

    public static VideoDateInfo Parse(string filePath) {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);

        // Regex to match "YYYY-MM-DD-weekday" or "MM-DD-weekday"
        // Group 1: Optional Year (YYYY)
        // Group 2: Month (MM)
        // Group 3: Day (DD)
        // Group 4: Weekday (German or English)
        string pattern = @"^(?:(\d{4})-)?(\d{2})-(\d{2})-(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)$";
        Match match = Regex.Match(fileNameWithoutExtension, pattern, RegexOptions.IgnoreCase);

        if (match.Success) {
            string yearGroup = match.Groups[1].Value; // YYYY if present
            int month = int.Parse(match.Groups[2].Value);
            int day = int.Parse(match.Groups[3].Value);
            string weekday = match.Groups[4].Value;

            int year;
            string parsedDateStringForInfo; // Will store YYYY-MM-DD or MM-DD

            if (!string.IsNullOrEmpty(yearGroup)) {
                year = int.Parse(yearGroup);
                parsedDateStringForInfo = $"{year:D4}-{month:D2}-{day:D2}";
            }
            else {
                // Infer year for MM-DD format
                year = DateTime.Now.Year;
                parsedDateStringForInfo = $"{month:D2}-{day:D2}";
            }

            DateTime parsedDate;
            try {
                parsedDate = new DateTime(year, month, day);

                // Heuristic for MM-DD format: if the date is in the future, assume it's from the previous year.
                // This makes more sense for lecture videos that are typically historical or current.
                if (string.IsNullOrEmpty(yearGroup) && parsedDate > DateTime.Today) {
                    parsedDate = parsedDate.AddYears(-1);
                }
            }
            catch (ArgumentOutOfRangeException) {
                // Date is genuinely invalid (e.g., Feb 30).
                return new VideoDateInfo { Date = DateTime.MinValue, Weekday = weekday, DateString = fileNameWithoutExtension };
            }

            return new VideoDateInfo {
                Date = parsedDate,
                Weekday = weekday,
                DateString = parsedDateStringForInfo
            };
        }

        // Fallback if regex doesn't match
        // Return MinValue to ensure consistent sorting for unparseable filenames,
        // placing them at the beginning.
        return new VideoDateInfo {
            Date = DateTime.MinValue,
            Weekday = "", // Unknown
            DateString = fileNameWithoutExtension // Use full filename as date string for debug
        };
    }
}