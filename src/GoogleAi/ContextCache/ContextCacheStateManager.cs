using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using File = System.IO.File;
using Environment = System.Environment;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Manages the lifecycle and disk persistence of Google Cloud Context Cache states.
/// Handles creation, validation, TTL extension, and deletion of cached system instructions across restarts and recompiles.
/// Each caller MUST supply a unique stateFileName (e.g. "ContextCacheState_vertex.json") to avoid
/// cross-session state pollution between Vertex extraction, LaTeX refinement steps, etc.
/// [Human] Verleiht dem Programm ein Gedächtnis für Google-Kontext-Caches. Jede Session nutzt ihre eigene JSON-Datei.
/// </summary>
public static class ContextCacheStateManager {
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    // [AI Context] Named state files per session type – prevents cross-session state pollution.
    public const string StateFileVertex = "ContextCacheState_vertex.json";
    public const string StateFileLatexStep1 = "ContextCacheState_latex_step1.json";
    public const string StateFileLatexStep2 = "ContextCacheState_latex_step2.json";
    public const string StateFileLatexStep3 = "ContextCacheState_latex_step3.json";

    /// <summary>
    /// [AI Context] Loads persisted cache state from disk by the given state filename.
    /// Checks both CurrentDirectory and BaseDirectory so it survives clean rebuilds.
    /// </summary>
    public static ContextCacheState LoadState(string stateFileName) {
        try {
            string currentDirPath = Path.Combine(Environment.CurrentDirectory, stateFileName);
            string baseDirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, stateFileName);

            string? targetPath = File.Exists(currentDirPath) ? currentDirPath : (File.Exists(baseDirPath) ? baseDirPath : null);
            if (targetPath != null) {
                string json = File.ReadAllText(targetPath);
                var state = JsonSerializer.Deserialize<ContextCacheState>(json, _jsonOptions);
                if (state != null) return state;
            }
        }
        catch (Exception ex) {
            Ui.Error($"Fehler beim Laden von State '{stateFileName}': {ex.GetType().Name} - {ex.Message}", "ContextCache");
        }
        return new ContextCacheState();
    }

    /// <summary>
    /// [AI Context] Saves cache state to CurrentDirectory (and BaseDirectory if different) for the given state filename.
    /// </summary>
    public static void SaveState(ContextCacheState state, string stateFileName) {
        try {
            string json = JsonSerializer.Serialize(state, _jsonOptions);
            string currentDirPath = Path.Combine(Environment.CurrentDirectory, stateFileName);
            string baseDirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, stateFileName);

            File.WriteAllText(currentDirPath, json);
            if (!string.Equals(currentDirPath, baseDirPath, StringComparison.OrdinalIgnoreCase)) {
                File.WriteAllText(baseDirPath, json);
            }
        }
        catch (Exception ex) {
            Ui.Error($"Fehler beim Speichern von State '{stateFileName}': {ex.GetType().Name} - {ex.Message}", "ContextCache");
        }
    }

    /// <summary>
    /// [AI Context] Deletes state JSON files from disk for the given state filename.
    /// </summary>
    public static void ClearState(string stateFileName) {
        try {
            string currentDirPath = Path.Combine(Environment.CurrentDirectory, stateFileName);
            string baseDirPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, stateFileName);

            if (File.Exists(currentDirPath)) File.Delete(currentDirPath);
            if (File.Exists(baseDirPath)) File.Delete(baseDirPath);
        }
        catch (Exception ex) {
            Ui.Error($"Fehler beim Löschen von State '{stateFileName}': {ex.GetType().Name} - {ex.Message}", "ContextCache");
        }
    }

    /// <summary>
    /// [AI Context] Computes SHA256 checksum of system instruction text to detect content drift.
    /// </summary>
    public static string ComputeChecksum(string text) {
        if (string.IsNullOrEmpty(text)) return "";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// [AI Context] Returns the number of minutes remaining until the locally stored cache state expires.
    /// Returns a negative value if already expired.
    /// </summary>
    public static double GetRemainingMinutes(ContextCacheState state) {
        return (state.ExpireTimeUtc - DateTime.UtcNow).TotalMinutes;
    }

    /// <summary>
    /// [AI Context] Checks if local config generation parameters and system instructions match saved state.
    /// Returns false if any parameter differs, or if the local expiry time has passed.
    /// </summary>
    public static bool MatchesConfig(
        ContextCacheState state,
        string model,
        float temp,
        float topP,
        int topK,
        int maxTokens,
        int? budget,
        string? level,
        string checksum) {
        if (string.IsNullOrEmpty(state.CacheName)) return false;
        if (!string.Equals(state.Model, model, StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs(state.Temperature - temp) > 0.001f) return false;
        if (Math.Abs(state.TopP - topP) > 0.001f) return false;
        if (state.TopK != topK) return false;
        if (state.MaxOutputTokens != maxTokens) return false;
        if (state.ThinkingBudget != budget) return false;
        if (!string.Equals(state.ThinkingLevel, level, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(state.SystemInstructionChecksum, checksum, StringComparison.OrdinalIgnoreCase)) return false;
        if (DateTime.UtcNow >= state.ExpireTimeUtc) return false;
        return true;
    }

    /// <summary>
    /// [AI Context] Verifies remote cache validity via Google Cloud API.
    /// </summary>
    public static async Task<bool> IsValidRemoteAsync(Client client, string cacheName) {
        if (string.IsNullOrEmpty(cacheName)) return false;
        try {
            var cached = await client.Caches.GetAsync(cacheName, config: null);
            if (cached != null) {
                if (cached.ExpireTime.HasValue) {
                    return cached.ExpireTime.Value.ToUniversalTime() > DateTime.UtcNow;
                }
                return true;
            }
        }
        catch (Exception ex) {
            Ui.Detail($"Remote Cache '{cacheName}' nicht mehr aktiv ({ex.GetType().Name}: {ex.Message}).", "Cache");
        }
        return false;
    }

    /// <summary>
    /// [AI Context] Deletes remote context cache on Google's side.
    /// </summary>
    public static async Task DeleteRemoteAsync(Client client, string cacheName) {
        if (string.IsNullOrEmpty(cacheName)) return;
        try {
            await client.Caches.DeleteAsync(cacheName, config: null);
            Ui.Detail($"Cache '{cacheName}' bei Google gelöscht.", "Cache");
        }
        catch (Exception ex) {
            Ui.Detail($"Konnte Cache '{cacheName}' bei Google nicht löschen ({ex.GetType().Name}: {ex.Message}).", "Cache");
        }
    }

    /// <summary>
    /// [AI Context] Prolongs context cache TTL by given minutes and persists the updated state under the same stateFileName.
    /// </summary>
    public static async Task<ContextCacheState?> ExtendCacheAsync(Client client, ContextCacheState state, int extendMinutes, string stateFileName) {
        if (string.IsNullOrEmpty(state.CacheName)) return null;
        try {
            var updateConfig = new UpdateCachedContentConfig {
                Ttl = $"{extendMinutes * 60}s"
            };
            var updated = await client.Caches.UpdateAsync(state.CacheName, updateConfig);
            state.ExpireTimeUtc = DateTime.UtcNow.AddMinutes(extendMinutes);
            if (updated != null && updated.ExpireTime.HasValue) {
                state.ExpireTimeUtc = updated.ExpireTime.Value.ToUniversalTime();
            }
            SaveState(state, stateFileName);
            return state;
        }
        catch (Exception ex) {
            Ui.Error($"Verlängern des Caches fehlgeschlagen: {ex.GetType().Name} - {ex.Message}", "Cache");
            return null;
        }
    }
}

