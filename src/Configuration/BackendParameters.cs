using System.Text.Json.Serialization;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Composed container holding AI generation, model selection, and context caching parameters.
/// [Human] KI-Parameter-Container zusammengesetzt aus Generierung, Modellauswahl und Context Caching.
/// </summary>
public class BackendParameters {
    public GenerationParameters Generation { get; set; } = new();
    public ModelSelection ModelSelection { get; set; } = new();
    public ContextCacheSettings ContextCaching { get; set; } = new();

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float Temperature { get => Generation.Temperature; set => Generation.Temperature = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float TopP { get => Generation.TopP; set => Generation.TopP = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int TopK { get => Generation.TopK; set => Generation.TopK = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int MaxOutputTokens { get => Generation.MaxOutputTokens; set => Generation.MaxOutputTokens = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int? ThinkingBudget { get => Generation.ThinkingBudget; set => Generation.ThinkingBudget = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string? ThinkingLevel { get => Generation.ThinkingLevel; set => Generation.ThinkingLevel = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] Model { get => ModelSelection.Available; set => ModelSelection.Available = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int CurrentModelIndex { get => ModelSelection.CurrentIndex; set => ModelSelection.CurrentIndex = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string CurrentModel { get => ModelSelection.Current; set => ModelSelection.Current = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public bool UseContextCaching { get => ContextCaching.Enabled; set => ContextCaching.Enabled = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingMinutes { get => ContextCaching.Minutes; set => ContextCaching.Minutes = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingIncrementMinutes { get => ContextCaching.IncrementMinutes; set => ContextCaching.IncrementMinutes = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingMinimumRemainingMinutes { get => ContextCaching.MinimumRemainingMinutes; set => ContextCaching.MinimumRemainingMinutes = value; }
}
