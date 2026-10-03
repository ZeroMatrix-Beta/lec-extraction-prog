using System;
using System.Text.RegularExpressions;

namespace LectureExtraction.Latex;

public static partial class LatexTimestampAdjuster {
    /// <summary>
    /// Removes the PART_START_SECONDS comment from the beginning of the LaTeX content, if present.
    /// </summary>
    /// <param name="latexContent">The LaTeX content.</param>
    /// <returns>The LaTeX content without the PART_START_SECONDS comment.</returns>
    public static string ExtractContentWithoutTimestampHeader(string latexContent) {
        // Regex to find and remove the comment line, handling different line endings
        return MyRegex().Replace(latexContent, "").TrimStart();
    }

    /// <summary>
    /// Adjusts timestamps within \begin{speech}[HH:MM:SS - HH:MM:SS] blocks by adding a given offset.
    /// The pre-rename name \begin{spoken-clean} is shifted too, so older output still works.
    /// </summary>
    /// <param name="latexContent">The LaTeX content of a single part.</param>
    /// <param name="offsetSeconds">The time offset in seconds to add to each timestamp.</param>
    /// <returns>The LaTeX content with adjusted timestamps.</returns>
    public static string AdjustTimestamps(string latexContent, double offsetSeconds) {
        if (offsetSeconds == 0) {
            return latexContent; // No adjustment needed
        }

        // Regex to find \begin{speech}[HH:MM:SS - HH:MM:SS] (or the old \begin{spoken-clean}[...])
        // Group 1: environment name
        // Group 2: Start HH, Group 3: Start MM, Group 4: Start SS
        // Group 5: End HH, Group 6: End MM, Group 7: End SS
        string pattern = @"\\begin{(speech|spoken-clean)}\[(\d{2}):(\d{2}):(\d{2})\s*-\s*(\d{2}):(\d{2}):(\d{2})\]";

        return Regex.Replace(latexContent, pattern, match => {
            string environment = match.Groups[1].Value;

            // Parse start time
            int startHour = int.Parse(match.Groups[2].Value);
            int startMinute = int.Parse(match.Groups[3].Value);
            int startSecond = int.Parse(match.Groups[4].Value);
            double currentStartSeconds = (startHour * 3600) + (startMinute * 60) + startSecond;

            // Parse end time
            int endHour = int.Parse(match.Groups[5].Value);
            int endMinute = int.Parse(match.Groups[6].Value);
            int endSecond = int.Parse(match.Groups[7].Value);
            double currentEndSeconds = (endHour * 3600) + (endMinute * 60) + endSecond;

            // Add offset
            double newStartSeconds = currentStartSeconds + offsetSeconds;
            double newEndSeconds = currentEndSeconds + offsetSeconds;

            // Convert back to HH:MM:SS
            TimeSpan newStartTime = TimeSpan.FromSeconds(newStartSeconds);
            TimeSpan newEndTime = TimeSpan.FromSeconds(newEndSeconds);

            string newStartTimestamp = $"{(int)newStartTime.TotalHours:D2}:{newStartTime.Minutes:D2}:{newStartTime.Seconds:D2}";
            string newEndTimestamp = $"{(int)newEndTime.TotalHours:D2}:{newEndTime.Minutes:D2}:{newEndTime.Seconds:D2}";

            return $"\\begin{{{environment}}}[{newStartTimestamp} - {newEndTimestamp}]";
        });
    }

    [GeneratedRegex(@"^% PART_START_SECONDS: \d+(\.\d+)?\r?\n?", RegexOptions.Multiline)]
    private static partial Regex MyRegex();
}