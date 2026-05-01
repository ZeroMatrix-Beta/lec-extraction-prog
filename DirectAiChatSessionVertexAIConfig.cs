using Config;

namespace DirectChatAiInteraction.Vertex;

/// <summary>
/// [AI Context] Localized generation parameters for the Vertex AI Enterprise session.
/// Ensures Vertex workloads can be tuned independently of AI Studio workloads.
/// </summary>
public class DirectAiChatSessionVertexAIConfig {
  // [AI Context] Temperature (0.0 - 2.0). 0.0 = purely deterministic.
  public float Temperature { get; set; } = 0.1f;
  // [AI Context] TopP (Nucleus Sampling). 0.0 - 1.0.
  public float TopP { get; set; } = 0.9f;
  // [AI Context] TopK. Limits the vocabulary. TopK=1 is greedy decoding.
  public int TopK { get; set; } = 10;
  // [AI Context] Hard cutoff limit for output generation.
  public int MaxOutputTokens { get; set; } = 65535;
  // [AI Context] Explicitly maps to Vertex Gemini 2.5 thinking budget.
  public int? ThinkingBudget { get; set; } = AppConfig.DefaultThinkingBudget;
  // [AI Context] Explicitly maps to Vertex Gemini 3.x reasoning effort.
  public string? ThinkingLevel { get; set; } = AppConfig.DefaultThinkingLevel;
}