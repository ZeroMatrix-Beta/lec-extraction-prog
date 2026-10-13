using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Collects one stage output's thought summaries across its requests (the first one and
/// every continue) and writes them to the targets its <see cref="ThoughtSummaryConfig"/> names.
///
/// <para>Use per stream: <see cref="Absorb"/> on every chunk (it returns the chunk's answer text, so
/// thought text can never reach the output or the history), <see cref="DiscardRequest"/> from the
/// retry callback, <see cref="EndRequestAsync"/> once per completed request. Files are written per
/// request, so a run that dies later keeps what it already has.</para>
///
/// <para>With <c>Enabled</c> off every method is a no-op apart from <see cref="Absorb"/>'s split,
/// which then returns exactly the text the old readers did.</para>
/// [Human] Sammelt die Gedanken-Zusammenfassungen einer Ausgabe und schreibt sie in Konsole, Log oder Datei.
/// </summary>
public sealed class ThoughtLog {
    private readonly ThoughtSummaryConfig _settings;
    private readonly ThoughtFileHeader _header;
    private readonly string? _outputPath;
    private readonly string? _existingLogPath;
    private readonly StringBuilder _current = new();
    private readonly List<ThoughtRequestRecord> _requests = [];
    private bool _thoughtOpenOnConsole;
    private bool _warnedNoExistingLog;

    /// <param name="settings">The stage's settings; a disabled config makes this log inert.</param>
    /// <param name="stageKey">Stable short name, e.g. <c>speech</c> or <c>extract-part2</c>.</param>
    /// <param name="stageLabel">Long label, as in the .tex header.</param>
    /// <param name="model">Model the stage runs on.</param>
    /// <param name="outputPath">The output file the thoughts belong to; the thoughts file goes beside it.</param>
    /// <param name="existingLogPath">The stage's own log, appended to per request; null when it has none
    /// it can write to as the request ends (see <see cref="AppendAllToAsync"/>).</param>
    public ThoughtLog(ThoughtSummaryConfig? settings, string stageKey, string stageLabel, string model, string? outputPath, string? existingLogPath) {
        _settings = settings ?? new ThoughtSummaryConfig();
        _outputPath = outputPath;
        _existingLogPath = existingLogPath;
        _header = new ThoughtFileHeader(stageKey, stageLabel, outputPath != null ? Path.GetFileName(outputPath) : stageKey, model, DateTimeOffset.Now);
    }

    /// <summary>A log that only splits chunks - for requests that never ask for thoughts.</summary>
    public static ThoughtLog Inert() => new(null, "none", "none", "", null, null);

    public bool IsEnabled => _settings.Enabled;

    /// <summary>The thoughts file once the first request has been written to it, otherwise null.</summary>
    public string? FilePath { get; private set; }

    public IReadOnlyList<ThoughtRequestRecord> Requests => _requests;

    /// <summary>Splits <paramref name="chunk"/>; keeps the thought text and returns the answer text.</summary>
    public string Absorb(GenerateContentResponse? chunk) {
        var (answer, thought) = ResponseParts.Split(chunk);
        if (!IsEnabled) {
            return answer;
        }

        if (thought.Length > 0) {
            _current.Append(thought);
            if (_settings.ToConsole) {
                if (!_thoughtOpenOnConsole) {
                    Ui.Detail("Gedanken-Zusammenfassung:", "Thoughts");
                    _thoughtOpenOnConsole = true;
                }
                Ui.Thought(thought);
            }
        }

        // The answer starts on its own line, not glued to the end of the dimmed summary.
        if (answer.Length > 0 && _thoughtOpenOnConsole) {
            Ui.RawLine();
            _thoughtOpenOnConsole = false;
        }
        return answer;
    }

    /// <summary>Drops what the current request collected - call it where the stream's retry resets its text.</summary>
    public void DiscardRequest() {
        _current.Clear();
        _thoughtOpenOnConsole = false;
    }

    /// <summary>Closes the current request and writes it to every configured file target.</summary>
    public async Task EndRequestAsync(int requestNumber, UsageReport usage) {
        if (!IsEnabled) {
            return;
        }

        if (_thoughtOpenOnConsole) {
            Ui.RawLine();
            _thoughtOpenOnConsole = false;
        }

        var record = new ThoughtRequestRecord(requestNumber, DateTimeOffset.Now, usage.ThoughtTokens, usage.CandidateTokens, usage.FinishReason, _current.ToString().Trim());
        _current.Clear();
        _requests.Add(record);

        if (_settings.ToSeparateFile && _outputPath != null) {
            await WriteToSeparateFileAsync(record);
        }

        if (_settings.ToExistingLog) {
            if (_existingLogPath != null) {
                await AppendAsync(_existingLogPath, ThoughtFile.FormatForExistingLog(_header, record));
            }
            else if (!_warnedNoExistingLog) {
                _warnedNoExistingLog = true;
                Ui.Detail($"ToExistingLog: Für {_header.Stage} gibt es kein Log, in das jetzt geschrieben werden kann.", "Thoughts");
            }
        }

        string where = FilePath != null ? $" -> {Path.GetFileName(FilePath)}" : "";
        Ui.Detail($"Request {requestNumber}: {usage.ThoughtTokens:N0} Denk-Tokens, Zusammenfassung {record.Text.Length:N0} Zeichen{where}", "Thoughts");
    }

    /// <summary>
    /// Appends every request collected so far to <paramref name="logPath"/>. For a stage whose log is
    /// written only after the requests (the PDF repair's compile log), so it cannot be appended to
    /// per request. Does nothing unless <c>ToExistingLog</c> is on.
    /// </summary>
    public async Task AppendAllToAsync(string logPath) {
        if (!IsEnabled || !_settings.ToExistingLog) {
            return;
        }
        var builder = new StringBuilder();
        foreach (var record in _requests) {
            builder.Append(ThoughtFile.FormatForExistingLog(_header, record));
        }
        if (builder.Length > 0) {
            await AppendAsync(logPath, builder.ToString());
        }
    }

    private async Task WriteToSeparateFileAsync(ThoughtRequestRecord record) {
        try {
            if (FilePath == null) {
                string path = ThoughtFile.ResolveNewPath(_outputPath!);
                string? folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) {
                    Directory.CreateDirectory(folder);
                }
                await System.IO.File.WriteAllTextAsync(path, ThoughtFile.FormatHeader(_header));
                FilePath = path;
            }
            await System.IO.File.AppendAllTextAsync(FilePath, ThoughtFile.FormatRequest(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Ui.Warn($"Gedanken-Datei konnte nicht geschrieben werden: {ex.Describe()}", "Thoughts");
        }
    }

    private static async Task AppendAsync(string path, string text) {
        try {
            await System.IO.File.AppendAllTextAsync(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Ui.Warn($"Gedanken konnten nicht an '{Path.GetFileName(path)}' angehängt werden: {ex.Describe()}", "Thoughts");
        }
    }
}
