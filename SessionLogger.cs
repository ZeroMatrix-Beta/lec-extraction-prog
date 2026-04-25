using System;
using System.IO;
using System.Threading.Tasks;

namespace AiInteraction;

/// <summary>
/// [AI Context] Handles file I/O operations for a chat session.
/// Responsible for creating timestamped session folders and saving Markdown/LaTeX logs.
/// </summary>
public class SessionLogger
{
  private readonly string _logFolderPath;
  private string _currentSessionLogPath = "";
  private string _currentSessionDateSuffix = "";
  private int _responseCount = 1;

  public SessionLogger(string logFolderPath)
  {
    _logFolderPath = logFolderPath;
  }

  public void InitializeSession()
  {
    _currentSessionDateSuffix = GetFormattedDateString(DateTime.Now);

    if (!string.IsNullOrWhiteSpace(_logFolderPath))
    {
      if (!Directory.Exists(_logFolderPath))
      {
        Directory.CreateDirectory(_logFolderPath);
      }

      int maxIndex = 0;
      foreach (var dir in Directory.GetDirectories(_logFolderPath))
      {
        string dirName = Path.GetFileName(dir);
        if (dirName.StartsWith("folder-", StringComparison.OrdinalIgnoreCase))
        {
          string[] dirParts = dirName.Split('-');
          if (dirParts.Length >= 2 && int.TryParse(dirParts[1], out int parsedIndex))
          {
            if (parsedIndex > maxIndex) maxIndex = parsedIndex;
          }
        }
      }

      int folderIndex = maxIndex + 1;
      _currentSessionLogPath = Path.Combine(_logFolderPath, $"folder-{folderIndex}-{_currentSessionDateSuffix}");
      Directory.CreateDirectory(_currentSessionLogPath);
    }
  }

  public async Task LogChatAsync(string input, string promptText, string selectedModel, string fullResponse)
  {
    // Markdown Verlauf mitprotokollieren
    string logInput = input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase) ? $"[Dateien] {promptText}" : input;
    await File.AppendAllTextAsync("chat_log.md", $"\n**Du:** {logInput}\n\n**{selectedModel}:** {fullResponse}\n---\n");

    // LaTeX Response speichern
    if (!string.IsNullOrWhiteSpace(_currentSessionLogPath))
    {
      string texFilePath = Path.Combine(_currentSessionLogPath, $"response-{_responseCount}-{_currentSessionDateSuffix}.tex");
      await File.WriteAllTextAsync(texFilePath, fullResponse);
      _responseCount++;
    }
  }

  private string GetFormattedDateString(DateTime date)
  {
    string month = date.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture).ToLower();
    int day = date.Day;
    string suffix = (day % 10 == 1 && day != 11) ? "st"
                  : (day % 10 == 2 && day != 12) ? "nd"
                  : (day % 10 == 3 && day != 13) ? "rd"
                  : "th";
    string year = date.ToString("yyyy");
    return $"{month}-{day}{suffix}-{year}";
  }
}