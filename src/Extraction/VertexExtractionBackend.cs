using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Vertex AI: ADC-authenticated clients, uploads to a GCS bucket that must be purged
/// before and after every run (storage is billed), a text-only system instruction, and the explicit
/// CachedContent cache. Not exercised by a real run since the 2026-10-15 merge; see Documentation.md
/// section 9 before relying on it.
/// [Human] Das Vertex-Backend der Extraktions-Session.
/// </summary>
public sealed class VertexExtractionBackend(Client client, VertexAutoExtractionConfig config) : IExtractionBackend {
    public Client Client { get; } = client;

    public string Name => "Vertex AI";

    public string ProviderName => "Vertex AI";

    public bool SystemInstructionAcceptsFiles => false;

    public bool RefinementUsesVertex => AppConfig.IsVertexAiEnabled;

    /// <summary>The CachedContent cache part requests use while it is active.</summary>
    public ExplicitContextCache ContextCache { get; } = new(client, config);

    public void DescribeEndpoint() {
        if (!string.IsNullOrWhiteSpace(config.ProjectId)) {
            Ui.Detail($"API-Projekt:     {config.ProjectId} ({config.Location})");
        }
    }

    /// <summary>
    /// [AI Context] Financial guardrail: purges the bucket so temporary video files do not accumulate
    /// storage costs.
    /// [Human] Leert den GCS-Bucket, damit keine Speicherkosten auflaufen.
    /// </summary>
    public Task CleanUpRemoteFilesAsync() => GcsWorkspace.PurgeAsync(config.GcsBucketName);

    public AttachmentUploader CreateAudioUploader(Client? refinementClient, string folder) =>
        new(Client, folder, [folder], false, config.GcsBucketName);

    public Client CreateRefinementClient(LatexRefinementSessionConfig refinementConfig) =>
        GoogleAiClientBuilder.BuildVertexClient(refinementConfig?.VertexProjectId ?? "", refinementConfig?.VertexLocation ?? "");
}
