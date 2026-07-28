using System;
using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Manages available models list and active model index.
/// [Human] Modellauswahl-Konfiguration mit automatischer Bereichsprüfung für das aktive Modell.
/// </summary>
public class ModelSelection {
    public string[] Available { get; set; } = ["gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"];
    public int CurrentIndex { get; set; } = 0;

    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    public string Current {
        get => Available.Length > 0 ? Available[Math.Clamp(CurrentIndex, 0, Available.Length - 1)] : "";
        set {
            int idx = Math.Clamp(CurrentIndex, 0, Available.Length > 0 ? Available.Length - 1 : 0);
            if (Available.Length == 0) Available = [value];
            else Available[idx] = value;
        }
    }
}
