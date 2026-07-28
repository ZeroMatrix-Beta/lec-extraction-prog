namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] API key profile selection and environment variable name mapping.
/// [Human] API-Key Profilverwaltung und Abbildung von Umgebungsvariablen.
/// </summary>
public class ApiKeyProfile {
    public int ActiveProfile { get; set; } = 0;
    public string[] EnvNames { get; set; } = [
        "API_KEY-automated-content-extraction",
        "API_KEY-ai-studio-test-project-1",
        "API_KEY-ai-studio-test-project-2",
        "API_KEY-ai-studio-test-project-3"
    ];
}
