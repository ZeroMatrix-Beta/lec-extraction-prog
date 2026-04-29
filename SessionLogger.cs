using System;
using System.IO;
using System.Threading.Tasks;

namespace DirectChatAiInteraction;

public class SessionLoggerConfig {
  public string LogFolderPath { get; set; } = @"D:\gemini-logs";
}

/// <summary>
/// [AI Context] Handles file I/O operations for a chat session.
/// Responsible for creating timestamped session folders and saving Markdown/LaTeX logs.
/// </summary>
public class SessionLogger {
  private readonly string _logFolderPath;
  private string _currentSessionLogPath = "";
  private string _currentSessionDateSuffix = "";
  private int _responseCount = 1;
  private bool _loadedSystemInstruction;
  private bool _loadedHistory;

  public SessionLogger(SessionLoggerConfig config) {
    _logFolderPath = config.LogFolderPath;
  }

  public void InitializeSession() {
    _currentSessionDateSuffix = GetFormattedDateString(DateTime.Now);

    if (!string.IsNullOrWhiteSpace(_logFolderPath)) {
      // [AI Context] Scans the designated log directory for existing "folder-X-" prefixes.
      // Dynamically extracts the numeric index (X) to generate a monotonically increasing session ID, ensuring no logs are overwritten.
      if (!Directory.Exists(_logFolderPath)) {
        Directory.CreateDirectory(_logFolderPath);
      }

      int maxIndex = 0;
      foreach (var dir in Directory.GetDirectories(_logFolderPath)) {
        string dirName = Path.GetFileName(dir);
        if (dirName.StartsWith("folder-", StringComparison.OrdinalIgnoreCase)) {
          string[] dirParts = dirName.Split('-');
          if (dirParts.Length >= 2 && int.TryParse(dirParts[1], out int parsedIndex)) {
            if (parsedIndex > maxIndex) maxIndex = parsedIndex;
          }
        }
      }

      int folderIndex = maxIndex + 1;
      _currentSessionLogPath = Path.Combine(_logFolderPath, $"folder-{folderIndex}-{_currentSessionDateSuffix}");
      Directory.CreateDirectory(_currentSessionLogPath);
    }
  }

  public void SetSessionMetadata(bool loadedSystemInstruction, bool loadedHistory) {
    _loadedSystemInstruction = loadedSystemInstruction;
    _loadedHistory = loadedHistory;
  }

  public async Task LogSessionSetupAsync() {
    string setupLog = $"\n=== Neue Chat-Sitzung ({DateTime.Now}) ===\n- System Prompt geladen: {_loadedSystemInstruction}\n- History geladen: {_loadedHistory}\n---\n";
    await File.AppendAllTextAsync("chat_log.md", setupLog);
  }

  public async Task LogChatAsync(string input, string promptText, string selectedModel, string fullResponse, string userName, int inputTokens = 0, int outputTokens = 0) {
    // Markdown Verlauf mitprotokollieren
    string logInput = input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase) ? $"[Dateien] {promptText}" : input;
    string tokenInfo = (inputTokens > 0 || outputTokens > 0) ? $"\n\n*(Tokens: Input {inputTokens}, Output {outputTokens})*" : "";
    await File.AppendAllTextAsync("chat_log.md", $"\n**{userName}:** {logInput}\n\n**{selectedModel}:** {fullResponse}{tokenInfo}\n---\n");

    // LaTeX Response speichern
    // [AI Context] Isolates the raw model output into dedicated .tex files.
    // This is a core feature for academic workflows, allowing immediate compilation of the AI's response without copy-pasting from a Markdown log.
    if (!string.IsNullOrWhiteSpace(_currentSessionLogPath)) {
      string texFilePath = Path.Combine(_currentSessionLogPath, $"response-{_responseCount}-{_currentSessionDateSuffix}.tex");

      string formattedPrompt = logInput.Replace("\r\n", "\n").Replace("\n", "\n% ");
      string texHeader = $"% ==========================================\n" +
                         $"% Session Info:\n" +
                         $"% System Prompt loaded: {_loadedSystemInstruction}\n" +
                         $"% History loaded: {_loadedHistory}\n" +
                         $"% Tokens: Input {inputTokens}, Output {outputTokens}\n" +
                         $"% \n" +
                         $"% {userName} Prompt:\n" +
                         $"% {formattedPrompt}\n" +
                         $"% ==========================================\n\n";

      await File.WriteAllTextAsync(texFilePath, texHeader + fullResponse);
      _responseCount++;
    }
  }

  private string GetFormattedDateString(DateTime date) {
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