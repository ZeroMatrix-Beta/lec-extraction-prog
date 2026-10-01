using System;
using System.IO;
using System.Threading.Tasks;
using LectureExtraction.Configuration;

using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Infrastructure;

/// <summary>
/// [AI Context] Handles file I/O operations for a chat session.
/// Responsible for creating timestamped session folders and saving Markdown/LaTeX logs.
/// </summary>
public class SessionLogger(SessionLoggerConfig config) {
    private readonly string _logFolderPath = config.LogFolderPath;
    private string _currentSessionLogPath = "";
    public string CurrentSessionLogPath => _currentSessionLogPath;
    private string _currentSessionDateSuffix = "";
    private int _responseCount = 1;
    private bool _loadedSystemInstruction;
    private bool _loadedHistory;

    public void InitializeSession() {
        _currentSessionDateSuffix = GetFormattedDateString(DateTime.Now);

        if (!string.IsNullOrWhiteSpace(_logFolderPath)) {
            string effectiveLogPath = _logFolderPath;
            try {
                if (!Directory.Exists(effectiveLogPath)) {
                    Directory.CreateDirectory(effectiveLogPath);
                }
            }
            catch (Exception ex) {
                Ui.Warn($"Log-Verzeichnis '{effectiveLogPath}' konnte nicht erstellt werden: {ex.GetType().Name} - {ex.Message}. Versuche Fallback.", "SessionLogger");
                effectiveLogPath = ResolveFallbackLogFolder();
                if (!string.IsNullOrWhiteSpace(effectiveLogPath) && !Directory.Exists(effectiveLogPath)) {
                    try {
                        Directory.CreateDirectory(effectiveLogPath);
                    }
                    catch (Exception fallbackEx) {
                        Ui.Warn($"Fallback-Log-Verzeichnis '{effectiveLogPath}' konnte nicht erstellt werden: {fallbackEx.GetType().Name} - {fallbackEx.Message}. Logging deaktiviert.", "SessionLogger");
                        effectiveLogPath = "";
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(effectiveLogPath)) {
                try {
                    int maxIndex = 0;
                    foreach (var dir in Directory.GetDirectories(effectiveLogPath)) {
                        string dirName = Path.GetFileName(dir);
                        if (dirName.StartsWith("folder-", StringComparison.OrdinalIgnoreCase)) {
                            string[] dirParts = dirName.Split('-');
                            if (dirParts.Length >= 2 && int.TryParse(dirParts[1], out int parsedIndex)) {
                                if (parsedIndex > maxIndex) maxIndex = parsedIndex;
                            }
                        }
                    }

                    int folderIndex = maxIndex + 1;
                    _currentSessionLogPath = Path.Combine(effectiveLogPath, $"folder-{folderIndex}-{_currentSessionDateSuffix}");
                    Directory.CreateDirectory(_currentSessionLogPath);
                }
                catch (Exception ex) {
                    Ui.Warn($"Session-Log-Ordner konnte nicht erstellt werden: {ex.GetType().Name} - {ex.Message}. Logging deaktiviert.", "SessionLogger");
                    _currentSessionLogPath = "";
                }
            }
        }
    }

    private static string ResolveFallbackLogFolder() {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile)) {
            string userLogs = Path.Combine(userProfile, "gemini-logs");
            try {
                if (Directory.Exists(userLogs)) return userLogs;
                Directory.CreateDirectory(userLogs);
                return userLogs;
            }
            catch (Exception ex) {
                Ui.Warn($"Konnte Fallback '{userLogs}' nicht erstellen: {ex.GetType().Name} - {ex.Message}. Versuche System-Laufwerk...", "SessionLogger");
            }
        }

        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        string primaryCandidate = Path.Combine(systemDrive, "gemini-logs");
        try {
            if (Directory.Exists(primaryCandidate)) return primaryCandidate;
            Directory.CreateDirectory(primaryCandidate);
            return primaryCandidate;
        }
        catch (Exception ex) {
            Ui.Warn($"Konnte Fallback '{primaryCandidate}' nicht erstellen: {ex.GetType().Name} - {ex.Message}. Versuche Arbeitsverzeichnis...", "SessionLogger");
            return Path.Combine(Directory.GetCurrentDirectory(), "gemini-logs");
        }
    }

    public void SetSessionMetadata(bool loadedSystemInstruction, bool loadedHistory) {
        _loadedSystemInstruction = loadedSystemInstruction;
        _loadedHistory = loadedHistory;
    }

    private string GetChatLogFilePath() => !string.IsNullOrWhiteSpace(_currentSessionLogPath) ? Path.Combine(_currentSessionLogPath, "chat_log.md") : "chat_log.md";

    public async Task LogSessionSetupAsync() {
        if (string.IsNullOrWhiteSpace(_currentSessionLogPath)) return;
        string setupLog = $"\n=== Neue Chat-Sitzung ({DateTime.Now}) ===\n- System Prompt geladen: {_loadedSystemInstruction}\n- History geladen: {_loadedHistory}\n---\n";
        await File.AppendAllTextAsync(GetChatLogFilePath(), setupLog);
    }

    public async Task LogChatAsync(string input, string promptText, string selectedModel, string fullResponse, string userName, int inputTokens = 0, int outputTokens = 0, int cachedTokens = 0) {
        if (string.IsNullOrWhiteSpace(_currentSessionLogPath)) return;

        // Markdown Verlauf mitprotokollieren
        string logInput = input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase) ? $"[Dateien] {promptText}" : input;
        int freshTokens = Math.Max(0, inputTokens - cachedTokens);
        string tokenInfo = (inputTokens > 0 || outputTokens > 0) ? $"\n\n*(Tokens: Total Prompt {inputTokens:N0}, Gecacht {cachedTokens:N0}, Frisch {freshTokens:N0}, Output {outputTokens:N0})*" : "";
        await File.AppendAllTextAsync(GetChatLogFilePath(), $"\n**{userName}:** {logInput}\n\n**{selectedModel}:** {fullResponse}{tokenInfo}\n---\n");

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
                               $"% Tokens: Total Prompt {inputTokens:N0}, Gecacht {cachedTokens:N0}, Frisch {freshTokens:N0}, Output {outputTokens:N0}\n" +
                               $"% \n" +
                               $"% {userName} Prompt:\n" +
                               $"% {formattedPrompt}\n" +
                               $"% ==========================================\n\n";

            await File.WriteAllTextAsync(texFilePath, texHeader + fullResponse.FixMalformedEndTags());
            _responseCount++;
        }
    }

    private static string GetFormattedDateString(DateTime date) {
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