using System;
using System.Collections.Generic;
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
        set => SelectOrAdd(value);
    }

    /// <summary>
    /// [AI Context] Selects an existing model by name or appends it to Available[] if absent, setting CurrentIndex accordingly.
    /// [Human] Wählt ein vorhandenes Modell aus oder fügt ein neues Modell zur Liste hinzu und setzt CurrentIndex.
    /// </summary>
    public void SelectOrAdd(string name) {
        if (string.IsNullOrWhiteSpace(name)) return;

        int existingIndex = Array.IndexOf(Available, name);
        if (existingIndex >= 0) {
            CurrentIndex = existingIndex;
        } else {
            var list = new List<string>(Available) { name };
            Available = [.. list];
            CurrentIndex = Available.Length - 1;
        }
    }
}
