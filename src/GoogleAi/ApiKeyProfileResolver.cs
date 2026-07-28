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
}
