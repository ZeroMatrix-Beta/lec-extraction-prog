using System;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Financial guardrail shared by the Vertex extraction session and the LaTeX
/// refinement session: purges every object in the configured GCS bucket after processing, so
/// temporary video/audio uploads never accumulate storage costs.
/// [Human] Löscht temporäre Dateien im Google Cloud Storage Bucket, damit am Ende des Monats
/// keine überraschenden Kosten entstehen.
/// </summary>
public static class GcsWorkspace {
    public static async Task PurgeAsync(string bucketName) {
        if (string.IsNullOrWhiteSpace(bucketName)) return;
        try {
            Ui.Blank();
            Ui.Detail($"Starte Cleanup: Lösche temporäre Dateien im Bucket '{bucketName}'...", "GCS");
            var storageClient = await StorageClient.CreateAsync();
            var objects = storageClient.ListObjectsAsync(bucketName);
            int count = 0;
            await foreach (var obj in objects) {
                await storageClient.DeleteObjectAsync(bucketName, obj.Name);
                count++;
            }
            if (count > 0) Ui.Detail($"{count} temporäre Datei(en) gelöscht, um Storage-Kosten zu sparen.", "GCS");
        }
        catch (Exception ex) {
            Ui.Warn($"Konnte Bucket nicht bereinigen. Art der Exception: {ex.GetType().Name}, Fehler: {ex.Message}", "GCS");
        }
    }
}
