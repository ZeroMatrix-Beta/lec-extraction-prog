using LectureExtraction.Configuration;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Synchronizes the model selected in the AutoExtraction session to all refinement steps
/// in the LatexRefinementSessionConfig, and persists both configurations so the entire pipeline stays unified.
/// [Human] Synchronisiert das ausgewählte Modell auf alle Schritte des LaTeX-Refinements und speichert beide Config-Dateien ab.
/// </summary>
public static class ModelSyncService {
    public static void SyncModelToRefinementConfig(string modelName, bool isVertex, LatexRefinementSessionConfig? inMemoryConfig = null) {
        if (string.IsNullOrWhiteSpace(modelName)) return;
        var refConfig = inMemoryConfig ?? ConfigLoader<LatexRefinementSessionConfig>.Load();
        if (isVertex) {
            refConfig.Step1MergeAndTimestamp.Vertex.CurrentModel = modelName;
            refConfig.Step2SpeechRefinement.Vertex.CurrentModel = modelName;
            refConfig.Step3LastRefinement.Vertex.CurrentModel = modelName;
        }
        else {
            refConfig.Step1MergeAndTimestamp.AiStudio.CurrentModel = modelName;
            refConfig.Step2SpeechRefinement.AiStudio.CurrentModel = modelName;
            refConfig.Step3LastRefinement.AiStudio.CurrentModel = modelName;
        }
        ConfigLoader<LatexRefinementSessionConfig>.Save(refConfig);
    }
}
