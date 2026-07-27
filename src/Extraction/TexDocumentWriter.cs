using System;
using System.Globalization;
using LectureExtraction.Extraction.Model;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Builds the metadata headers written into every .tex file produced by the extraction
/// pipeline. Shared verbatim between the AI Studio and Vertex extraction sessions — AI Studio already
/// had these as separate methods (BuildTexPartHeader / BuildTexCombinedHeader / BuildTexModelParameterBlock);
/// Vertex had the identical string templates inlined directly in ProcessFilesAsync.
/// [Human] Baut die Metadaten-Header, die in jede erzeugte .tex-Datei geschrieben werden.
/// </summary>
public static class TexDocumentWriter {
    /// <summary>
    /// [AI Context] Builds the metadata header for an individual .tex part file.
    /// Includes model parameters, timestamp offset, and per-part token usage statistics.
    /// [Human] Baut den Metadaten-Header für eine einzelne .tex-Teildatei.
    /// </summary>
    public static string BuildPartHeader(string sourcePartFileName, double partStartTimeSeconds, TokenUsage usage,
        string model, float temperature, float topP, int topK, int maxOutputTokens, int? thinkingBudget, string? thinkingLevel) {
        return $"% ==========================================\n" +
               $"% AutoExtraction Source Part: {sourcePartFileName}\n" +
               BuildModelParameterBlock(model, temperature, topP, topK, maxOutputTokens, thinkingBudget, thinkingLevel) +
               $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
               $"% PART_START_SECONDS: {partStartTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}\n" +
               $"% ------------------------------------------\n" +
               $"% Token Usage Analysis (Google GenAI):\n" +
               $"%   - Total Prompt Tokens : {usage.Input:N0} (Gesamtumfang des Aufmerksamkeitshorizonts)\n" +
               $"%   - Cached Context      : {usage.Cached:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
               $"%   - Fresh Input Tokens  : {usage.Fresh:N0} (Echter neuer Payload: Video-Segment + Prompt)\n" +
               $"%   - Generated Output    : {usage.Output:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
               $"% ==========================================\n\n";
    }

    /// <summary>
    /// [AI Context] Builds the metadata header for the combined (-all) .tex file.
    /// Includes model parameters and aggregated token usage across all parts.
    /// [Human] Baut den Metadaten-Header für die zusammengeführte (-all) .tex-Datei.
    /// </summary>
    public static string BuildCombinedHeader(string sourceFileName, int totalParts, TokenUsage totalUsage,
        string model, float temperature, float topP, int topK, int maxOutputTokens, int? thinkingBudget, string? thinkingLevel) {
        return $"% ==========================================\n" +
               $"% AutoExtraction Combined Source: {sourceFileName}\n" +
               BuildModelParameterBlock(model, temperature, topP, topK, maxOutputTokens, thinkingBudget, thinkingLevel) +
               $"% Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
               $"% ------------------------------------------\n" +
               $"% Token Usage Summary across {totalParts} Part(s):\n" +
               $"%   - Total Prompt Tokens : {totalUsage.Input:N0} (Summe aller Prompts über alle Teile)\n" +
               $"%   - Cached Context      : {totalUsage.Cached:N0} (Aus Google Context-Cache recycelt, rabattiert)\n" +
               $"%   - Fresh Input Tokens  : {totalUsage.Fresh:N0} (Echter neuer Payload für alle Video-Teile)\n" +
               $"%   - Total Output Tokens : {totalUsage.Output:N0} (Generiertes LaTeX + Thinking Tokens)\n" +
               $"% ==========================================\n\n";
    }

    /// <summary>
    /// [AI Context] Builds the common model parameter block used in all .tex headers.
    /// [Human] Gemeinsamer Block mit Modell-Parametern für alle .tex-Header.
    /// </summary>
    private static string BuildModelParameterBlock(string model, float temperature, float topP, int topK,
        int maxOutputTokens, int? thinkingBudget, string? thinkingLevel) {
        return $"% Model: {model}\n" +
               $"% Temperature: {temperature}\n" +
               $"% TopP: {topP}\n" +
               $"% TopK: {topK}\n" +
               $"% MaxOutputTokens: {maxOutputTokens}\n" +
               (thinkingBudget.HasValue ? $"% ThinkingBudget: {thinkingBudget.Value}\n" : "") +
               (!string.IsNullOrEmpty(thinkingLevel) ? $"% ThinkingLevel: {thinkingLevel}\n" : "");
    }
}
