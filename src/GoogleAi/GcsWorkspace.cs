using System;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Financial guardrail: purges every object in the configured GCS bucket, so temporary
/// video/audio/chat uploads never accumulate storage costs. Called by the Vertex extraction session,
/// the LaTeX refinement session, and both chat sessions at start (crash leftovers) and end.
///
/// <para>This is now the single implementation, down from three differently-named copies
/// (<c>CleanupBucketAsync</c>, <c>CleanupGcsBucketAsync</c>, <c>ForcePurgeGcsBucketAsync</c>). The
/// chat variant was merged into the extraction one on 2026-07-29 as the union of both, in German
/// (the user's call); it stayed separate only until the chat sessions joined the <see cref="Ui"/>
/// layer, which is what this change does. Everything either copy could report, this one reports:
/// the placeholder-name guard, the empty-bucket line, the billing-account and network branches, and
/// the full exception dump behind <paramref name="verbose"/>.</para>
///
/// <para>The free-tier guard is deliberately <i>not</i> here: whether a backend has a bucket at all
/// is the caller's knowledge, not this method's.</para>
/// [Human] Löscht temporäre Dateien im Google Cloud Storage Bucket, damit am Ende des Monats keine
/// überraschenden Kosten entstehen. Die einzige Fassung - alle Schutzabfragen und Diagnosen vereint.
/// </summary>
public static class GcsWorkspace {
    /// <param name="verbose">Prints the full exception object on failure, not just its message.</param>
    public static async Task PurgeAsync(string bucketName, bool verbose = false) {
        if (string.IsNullOrWhiteSpace(bucketName) || bucketName == "DEIN_BUCKET_NAME_HIER_EINTRAGEN") return;

        try {
            Ui.Blank();
            Ui.Detail($"Starte Cleanup: Lösche temporäre Dateien im Bucket '{bucketName}'...", "GCS");

            // StorageClient utilizes Application Default Credentials
            var storageClient = await StorageClient.CreateAsync();
            var objects = storageClient.ListObjectsAsync(bucketName);
            int count = 0;
            await foreach (var obj in objects) {
                await storageClient.DeleteObjectAsync(bucketName, obj.Name);
                count++;
            }

            if (count > 0) {
                Ui.Detail($"{count} temporäre Datei(en) gelöscht, um Storage-Kosten zu sparen.", "GCS");
            }
            else {
                Ui.Detail("Bucket ist bereits leer.", "GCS");
            }
        }
        catch (Exception ex) {
            Ui.Error($"[Exception gefangen] {ex.GetType().Name}: {ex.Message}");

            if (verbose) {
                Ui.Detail("--- GCS ERROR DUMP ---");
                Ui.Detail($"{ex}");
                Ui.Detail("----------------------");
            }

            if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.Sockets.SocketException ||
                ex.Message.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("host is known", StringComparison.OrdinalIgnoreCase)) {
                Ui.Error($"Netzwerkfehler beim Zugriff auf '{bucketName}'. Möglicherweise sind Sie nicht mit dem Internet verbunden! Originalfehler: {ex.Message}", "GCS");
            }
            else if (ex.Message.Contains("billing account", StringComparison.OrdinalIgnoreCase)) {
                Ui.Error($"Zugriff auf Bucket '{bucketName}' verweigert. Dem Projekt fehlt ein aktives Rechnungskonto (Billing Account)! Originalfehler: {ex.Message}", "GCS");
            }
            else {
                Ui.Error($"Bucket '{bucketName}' konnte nicht bereinigt oder erreicht werden: {ex.Message}", "GCS");
            }
        }
    }
}
