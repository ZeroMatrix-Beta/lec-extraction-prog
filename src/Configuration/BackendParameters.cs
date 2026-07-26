using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

public class BackendParameters {
    public float Temperature { get; set; } = AppConfig.DefaultTemperature;
    public float TopP { get; set; } = 1.0f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 65535;
    public string[] Model { get; set; } = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"];
    // [AI Context] Zero-based index into Model[] indicating the currently chosen model. Persisted to JSON so the user's selection survives restarts.
    public int CurrentModelIndex { get; set; } = 0;
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string CurrentModel {
        get => Model.Length > 0 ? Model[Math.Clamp(CurrentModelIndex, 0, Model.Length - 1)] : "";
        set {
            int idx = Math.Clamp(CurrentModelIndex, 0, Model.Length > 0 ? Model.Length - 1 : 0);
            if (Model.Length == 0) Model = [value];
            else Model[idx] = value;
        }
    }
    public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
    public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;

    // [AI Context] If true, system instructions are cached on Google Cloud servers.
    // [Human] Wenn aktiviert, werden System Instructions im Cache gespeichert.
    public bool UseContextCaching { get; set; } = false;
    public int ContextCachingMinutes { get; set; } = 15;
    public int ContextCachingIncrementMinutes { get; set; } = 30;

    // [AI Context] Minimum remaining TTL in minutes before automatic pre-step cache extension is triggered.
    // [Human] Schwellenwert in Minuten: Wenn der Cache kürzer als dieser Wert gültig ist, wird er vor dem nächsten Schritt automatisch verlängert.
    public int ContextCachingMinimumRemainingMinutes { get; set; } = 10;
}
