namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Resolves an API-key profile index (0 = dedicated key, 1-3 = test-project keys) to the
/// environment variable name holding that key, given an optional per-config env-name array. Extracted
/// (Phase 6) — this exact fallback logic was copy-pasted twice in Program.cs and once more (in an
/// equivalent, verified form) in ConfigurationPrompts.ConfirmOrChangeApiKeyProfile.
/// [Human] Löst ein API-Key-Profil (0 = dedizierter Key, 1-3 = Testprojekt-Keys) zum Namen der
/// Umgebungsvariable auf, die diesen Key enthält.
/// </summary>
public static class ApiKeyProfileResolver {
    public static string Resolve(int profile, string[]? envNames) {
        string? extractedEnvName = (envNames != null && profile >= 0 && profile < envNames.Length) ? envNames[profile] : null;
        return extractedEnvName ?? (profile == 0 ? "API_KEY-automated-content-extraction" : $"API_KEY-ai-studio-test-project-{profile}");
    }

    /// <summary>
    /// [AI Context] Resolves a profile specification (either an integer index like "0", "4" or a profile /
    /// environment variable name such as "latex-refinement", "automated-content-extraction",
    /// "API_KEY-ai-studio-test-project-1") to its corresponding profile index.
    /// [Human] Löst einen Profil-Namen oder Profil-Index zum passenden Index auf.
    /// </summary>
    public static int ResolveIndex(string? profileOrName, string[]? envNames, int defaultIndex = 0) {
        if (string.IsNullOrWhiteSpace(profileOrName)) {
            return defaultIndex;
        }

        if (int.TryParse(profileOrName, out int parsed)) {
            return parsed;
        }

        if (envNames != null && envNames.Length > 0) {
            string cleaned = profileOrName.Trim();
            string normalizedTarget = NormalizeKeyName(cleaned);

            for (int i = 0; i < envNames.Length; i++) {
                string candidate = envNames[i];
                if (string.Equals(candidate, cleaned, StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
                if (string.Equals(NormalizeKeyName(candidate), normalizedTarget, StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
            }

            for (int i = 0; i < envNames.Length; i++) {
                if (envNames[i].Contains(cleaned, StringComparison.OrdinalIgnoreCase)) {
                    return i;
                }
            }
        }

        return defaultIndex;
    }

    /// <summary>
    /// [AI Context] Resolves --profile for a stage that keeps its own key list (the refinement's
    /// <c>LatexRefinementSessionConfig.AiStudioApiKeyEnvNames</c>). A number always means the profile
    /// table's entry - the extraction config's list, which <c>lecx config profiles</c> shows - and that
    /// key is then looked up by name in the stage's list, because the two lists need not be in the same
    /// order (2026-10-26: the refinement list had latex-refinement first and automated-content-extraction
    /// last, so <c>--profile 4</c> sent the refinement to profile 0's key). A name is looked up as given.
    /// Returns the index in <paramref name="stageEnvNames"/>, or -1 when that key is not in it.
    /// [Human] Eine Profil-Nummer gilt immer laut Profil-Tabelle; der Key wird dann per Name in der
    /// eigenen Key-Liste des Schritts gesucht.
    /// </summary>
    public static int ResolveIndexInStageList(string profileOrName, string[]? profileTable, string[]? stageEnvNames) {
        string keyName = int.TryParse(profileOrName.Trim(), out int profile)
            ? Resolve(profile, profileTable)
            : profileOrName;
        return ResolveIndex(keyName, stageEnvNames, -1);
    }

    private static string NormalizeKeyName(string name) {
        if (name.StartsWith("API_KEY-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("API_KEY_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("API-KEY-", StringComparison.OrdinalIgnoreCase)) {
            return name[8..];
        }
        return name;
    }
}
