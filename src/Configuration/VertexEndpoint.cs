namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] GCP Vertex AI endpoint details (Project ID, location, GCS bucket). Project and bucket
/// belong to one Google Cloud account, so they have no default: the JSON sets them.
/// [Human] Einstellungen für den Google Cloud Vertex AI Endpoint (Projekt-ID, Region, Bucket).
/// Projekt-ID und Bucket stehen in der JSON, nicht im Code.
/// </summary>
public class VertexEndpoint {
    public string ProjectId { get; set; } = "";
    public string Location { get; set; } = "global";
    public string GcsBucketName { get; set; } = "";
}
