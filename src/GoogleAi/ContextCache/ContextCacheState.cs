using System;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Persistent state model for Google Cloud Context Caching.
/// Stores the remote cache resource name and generation parameters to detect configuration drift across recompiles.
/// [Human] Speichert den Status des Kontext-Caches auf der Festplatte, damit das Programm nach einem Neustart weiß, ob der Cache bei Google noch gültig ist.
/// </summary>
public class ContextCacheState {
    // [AI Context] The remote Google Cloud resource name (e.g. "cachedContents/123456789").
    public string? CacheName { get; set; }

    // [AI Context] The exact Gemini model used when creating the cache.
    public string? Model { get; set; }

    // [AI Context] Generation parameters. If these change in config, the cache must be invalidated.
    public float Temperature { get; set; }
    public float TopP { get; set; }
    public int TopK { get; set; }
    public int MaxOutputTokens { get; set; }
    public int? ThinkingBudget { get; set; }
    public string? ThinkingLevel { get; set; }

    // [AI Context] SHA256 checksum of the combined system instructions text.
    public string? SystemInstructionChecksum { get; set; }

    // [AI Context] Expiration timestamp in UTC.
    public DateTime ExpireTimeUtc { get; set; }
}
