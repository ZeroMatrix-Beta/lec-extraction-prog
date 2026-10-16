using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Persistent tracker for Google AI Studio Free-Tier daily quotas (20 requests/day per project
/// per model). Tracks requests sent and daily quota exhaustion (429 errors), automatically resetting at 00:00 UTC.
/// [Human] Verwaltet und speichert das tägliche Kontingent der Google AI Studio API-Keys (20 Anfragen pro Tag pro Modell).
/// Setzt sich jeden Tag um 00:00 UTC (02:00 Uhr deutscher Zeit) automatisch zurück.
/// </summary>
public static partial class ApiKeyQuotaTracker {
    public const int DefaultDailyFreeTierLimit = 20;
    private static readonly object SyncLock = new();
    private static QuotaState? _cachedState;

    [GeneratedRegex(@"model:\s*(?<model>[\w\.\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ModelExtractionRegex();

    public record QuotaEntry {
        public string EnvName { get; init; } = "";
        public string Model { get; init; } = "";
        public int RequestsToday { get; set; }
        public bool IsExhausted { get; set; }
        public DateTime? ExhaustedAtUtc { get; set; }
        public string? Note { get; set; }
    }

    public class QuotaState {
        public string LastResetDateUtc { get; set; } = "";
        public List<QuotaEntry> Entries { get; set; } = [];
    }

    public static DateTime GetNextDailyResetUtc() {
        return DateTime.UtcNow.Date.AddDays(1);
    }

    public static TimeSpan GetTimeUntilDailyReset() {
        var remaining = GetNextDailyResetUtc() - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public static string FormatTimeUntilReset() {
        var remaining = GetTimeUntilDailyReset();
        return $"{remaining.Hours}h {remaining.Minutes:D2}m";
    }

    private static string GetFilePath() {
        string dir = ConfigStore.ResolveDirectory();
        return Path.Combine(dir, "quota-tracker.json");
    }

    private static QuotaState LoadStateLocked() {
        string todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string filePath = GetFilePath();

        if (_cachedState != null && string.Equals(_cachedState.LastResetDateUtc, todayUtc, StringComparison.Ordinal)) {
            return _cachedState;
        }

        QuotaState? state = null;
        if (File.Exists(filePath)) {
            try {
                string json = File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(json)) {
                    state = JsonSerializer.Deserialize<QuotaState>(json);
                }
            }
            catch (Exception ex) {
                Ui.Warn($"Quota-Tracker konnte nicht gelesen werden: {ex.Describe()}", "Quota");
            }
        }

        state ??= CreateInitialState(todayUtc);

        // Auto-reset if the recorded date is in the past
        if (!string.Equals(state.LastResetDateUtc, todayUtc, StringComparison.Ordinal)) {
            state.LastResetDateUtc = todayUtc;
            state.Entries.Clear();
            SaveStateLocked(state);
        }

        _cachedState = state;
        return state;
    }

    private static QuotaState CreateInitialState(string todayUtc) {
        var state = new QuotaState { LastResetDateUtc = todayUtc };

        // Seed initial known states for 2026-10-11 if file is first created
        if (string.Equals(todayUtc, "2026-10-11", StringComparison.Ordinal)) {
            state.Entries.Add(new() {
                EnvName = "API_KEY-automated-content-extraction",
                Model = "gemini-3.8-flash",
                RequestsToday = 20,
                IsExhausted = true,
                ExhaustedAtUtc = DateTime.UtcNow,
                Note = "Vorlesungen 1–4 & 03-17"
            });
            state.Entries.Add(new() {
                EnvName = "API_KEY-ai-studio-test-project-1",
                Model = "gemini-3.8-flash",
                RequestsToday = 12,
                IsExhausted = false,
                Note = "03-24 & 03-31 komplett"
            });
            state.Entries.Add(new() {
                EnvName = "API_KEY-ai-studio-test-project-2",
                Model = "gemini-3.8-flash",
                RequestsToday = 20,
                IsExhausted = true,
                ExhaustedAtUtc = DateTime.UtcNow,
                Note = "Refinement 1–3"
            });
            state.Entries.Add(new() {
                EnvName = "API_KEY-ai-studio-test-project-3",
                Model = "gemini-3.8-flash",
                RequestsToday = 20,
                IsExhausted = true,
                ExhaustedAtUtc = DateTime.UtcNow,
                Note = "04-14 Teil 1"
            });
            state.Entries.Add(new() {
                EnvName = "API_KEY-latex-refinement",
                Model = "gemini-3.8-flash",
                RequestsToday = 20,
                IsExhausted = true,
                ExhaustedAtUtc = DateTime.UtcNow,
                Note = "03-31 Teile 1–3"
            });
        }

        SaveStateLocked(state);
        return state;
    }

    private static void SaveStateLocked(QuotaState state) {
        try {
            string filePath = GetFilePath();
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            _cachedState = state;
        }
        catch (Exception ex) {
            Ui.Warn($"Quota-Tracker konnte nicht gespeichert werden: {ex.Describe()}", "Quota");
        }
    }

    /// <summary>
    /// [AI Context] Records a generation request for an AI Studio client whose API key profile was
    /// resolved via <see cref="GoogleAiClientBuilder"/>. No-op for Vertex clients and test doubles.
    /// [Human] Zählt eine Anfrage für das API-Key-Profil des verwendeten AI-Studio-Clients.
    /// </summary>
    public static void RecordClientRequest(Google.GenAI.Client client, string model, string? note = null) {
        string? envName = GoogleAiClientBuilder.GetEnvVarName(client);
        if (!string.IsNullOrEmpty(envName)) {
            GoogleAiClientBuilder.LastActiveEnvVarName = envName;
            RecordRequest(envName, model, note);
        }
    }

    public static void RecordRequest(string envName, string model, string? note = null) {
        lock (SyncLock) {
            var state = LoadStateLocked();
            var entry = state.Entries.Find(e =>
                string.Equals(e.EnvName, envName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase));

            if (entry == null) {
                entry = new() {
                    EnvName = envName,
                    Model = model,
                    RequestsToday = 1,
                    Note = note
                };
                state.Entries.Add(entry);
            }
            else {
                entry.RequestsToday++;
                if (!string.IsNullOrWhiteSpace(note)) {
                    entry.Note = note;
                }
            }

            SaveStateLocked(state);
        }
    }

    public static void RecordQuotaExhausted(string envName, string model, string? note = null) {
        lock (SyncLock) {
            var state = LoadStateLocked();
            var entry = state.Entries.Find(e =>
                string.Equals(e.EnvName, envName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase));

            if (entry == null) {
                entry = new() {
                    EnvName = envName,
                    Model = model,
                    RequestsToday = DefaultDailyFreeTierLimit,
                    IsExhausted = true,
                    ExhaustedAtUtc = DateTime.UtcNow,
                    Note = note
                };
                state.Entries.Add(entry);
            }
            else {
                entry.IsExhausted = true;
                entry.ExhaustedAtUtc = DateTime.UtcNow;
                if (entry.RequestsToday < DefaultDailyFreeTierLimit) {
                    entry.RequestsToday = DefaultDailyFreeTierLimit;
                }
                if (!string.IsNullOrWhiteSpace(note)) {
                    entry.Note = note;
                }
            }

            SaveStateLocked(state);
        }
    }

    public static void RecordQuotaExhaustedFromException(Exception ex, string? defaultModel = null, string? defaultEnvName = null) {
        string text = ex.ToString();
        string model = defaultModel ?? "gemini-3.8-flash";

        var match = ModelExtractionRegex().Match(text);
        if (match.Success && match.Groups["model"].Success) {
            model = match.Groups["model"].Value;
        }

        string? envName = defaultEnvName ?? GoogleAiClientBuilder.LastActiveEnvVarName;
        if (!string.IsNullOrEmpty(envName)) {
            RecordQuotaExhausted(envName, model);
        }
    }

    public static (int UsedToday, int Limit, bool IsExhausted, string? Note) GetStatus(string envName, string model) {
        lock (SyncLock) {
            var state = LoadStateLocked();
            var entry = state.Entries.Find(e =>
                string.Equals(e.EnvName, envName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase));

            if (entry == null) {
                return (0, DefaultDailyFreeTierLimit, false, null);
            }

            return (entry.RequestsToday, DefaultDailyFreeTierLimit, entry.IsExhausted, entry.Note);
        }
    }

    public static void ResetAll() {
        lock (SyncLock) {
            var state = LoadStateLocked();
            state.Entries.Clear();
            SaveStateLocked(state);
        }
    }
}
