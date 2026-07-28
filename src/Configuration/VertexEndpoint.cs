namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] GCP Vertex AI endpoint details (Project ID, location, GCS bucket).
/// [Human] Einstellungen für den Google Cloud Vertex AI Endpoint (Projekt-ID, Region, Bucket).
/// </summary>
public class VertexEndpoint {
    public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
    public string Location { get; set; } = "global";
    public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
}
