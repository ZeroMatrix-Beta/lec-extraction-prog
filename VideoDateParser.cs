using System;
using System.IO;

namespace AutoExtraction;

/// <summary>
/// [AI Context] Parses specific date/weekday formats from video filenames. 
/// Crucial for ensuring that overlapping lecture chunks are fed to the AI strictly in chronological order.
/// [Human] Liest das Datum aus dem Dateinamen aus, damit die Videos in der exakt richtigen Reihenfolge verarbeitet werden.
/// </summary>
internal static class VideoDateParser {
  public static (DateTime Date, string Weekday, string DateString) Parse(string filePath) {
    string fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
    var match = System.Text.RegularExpressions.Regex.Match(fileName, @"^(?:(\d{2,4})-)?(\d{2})-(\d{2})-([a-z]+)");

    int year = DateTime.Now.Year;
    int month = 1;
    int day = 1;
    string weekday = "Unknown";
    string dateString = "Unknown";

    if (match.Success) {
      if (match.Groups[1].Success && !string.IsNullOrEmpty(match.Groups[1].Value)) {
        year = int.Parse(match.Groups[1].Value);
        if (year < 100) year += 2000;
      }

      month = int.Parse(match.Groups[2].Value);
      day = int.Parse(match.Groups[3].Value);
      weekday = match.Groups[4].Value;
      weekday = char.ToUpper(weekday[0]) + weekday.Substring(1);

      dateString = match.Groups[1].Success && !string.IsNullOrEmpty(match.Groups[1].Value)
          ? $"{day:D2}.{month:D2}.{year}"
          : $"{day:D2}.{month:D2}.";
    }

    DateTime sortDate = DateTime.MaxValue;
    try { if (match.Success) sortDate = new DateTime(year, month, day); } catch { }

    return (sortDate, weekday, dateString);
  }
}