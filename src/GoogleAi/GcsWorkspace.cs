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

    /// <summary>
    /// [AI Context] The chat sessions' bucket purge, run at session start and again at session end.
    /// Same job as <see cref="PurgeAsync"/>, kept separate for two reasons that both dissolve later:
    /// the chat sessions still write through <c>System.Console</c> rather than <c>Ui</c>, and they
    /// carry deeper diagnostics because a chat session is where a misconfigured bucket first shows up.
    /// Merge the two once the chat sessions join the Spectre layer.
    ///
    /// <para>This is the union of two implementations that had drifted: AI Studio's placeholder-name
    /// guard, and Vertex's billing-account branch, empty-bucket line and full exception dump. The dump
    /// is the only piece now conditional - it is a stack trace, useful when diagnosing and noise
    /// otherwise. Vertex's half was written in English against the project's German convention and is
    /// translated here.</para>
    ///
    /// <para>The free-tier guard is deliberately <i>not</i> here: whether a backend has a bucket at
    /// all is the caller's knowledge, not this method's.</para>
    /// [Human] Leert den GCS-Bucket einer Chat-Sitzung. Vereint die beiden auseinandergelaufenen
    /// Fassungen - alle Schutzabfragen und alle Diagnosen bleiben erhalten.
    /// </summary>
    /// <param name="verbose">Prints the full exception object on failure, not just its message.</param>
    public static async Task PurgeChatWorkspaceAsync(string bucketName, bool verbose) {
        if (string.IsNullOrWhiteSpace(bucketName) || bucketName == "DEIN_BUCKET_NAME_HIER_EINTRAGEN") return;

        try {
            // StorageClient utilizes Application Default Credentials
            var storageClient = await StorageClient.CreateAsync();
            Console.WriteLine($"  [GCS] Prüfe Bucket '{bucketName}' und lösche ALLE Dateien...");

            var objects = storageClient.ListObjectsAsync(bucketName);
            int count = 0;
            await foreach (var obj in objects) {
                await storageClient.DeleteObjectAsync(bucketName, obj.Name);
                count++;
            }

            if (count > 0) {
                Console.WriteLine($"  [GCS] {count} Datei(en) erfolgreich gelöscht, um Kosten zu vermeiden.");
            }
            else {
                Console.WriteLine("  [GCS] Bucket ist bereits leer.");
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");

            if (verbose) {
                Console.WriteLine("\n  --- GCS ERROR DUMP ---");
                Console.WriteLine($"{ex}");
                Console.WriteLine("  ----------------------\n");
            }

            if (ex is System.Net.Http.HttpRequestException || ex.InnerException is System.Net.Sockets.SocketException ||
                ex.Message.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("host is known", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine($"  [GCS FEHLER] Netzwerkfehler beim Zugriff auf '{bucketName}'. Möglicherweise sind Sie nicht mit dem Internet verbunden! Originalfehler: {ex.Message}");
            }
            else if (ex.Message.Contains("billing account", StringComparison.OrdinalIgnoreCase)) {
                Console.WriteLine($"  [GCS FEHLER] Zugriff auf Bucket '{bucketName}' verweigert. Dem Projekt fehlt ein aktives Rechnungskonto (Billing Account)! Originalfehler: {ex.Message}");
            }
            else {
                Console.WriteLine($"  [GCS FEHLER] Bucket '{bucketName}' konnte nicht bereinigt oder erreicht werden: {ex.Message}");
            }
        }
    }
}
