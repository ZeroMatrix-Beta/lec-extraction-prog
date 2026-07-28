using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using static System.Console;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Specialized handler for parsing commands, discovering local files, and securely uploading them to Google APIs.
/// Abstracted from DirectAIInteraction to comply with Single Responsibility Principle.
/// [Human] Diese Klasse kümmert sich NUR um Dateien. Sie sucht sie auf deiner Festplatte und lädt sie für die KI hoch.
/// </summary>
/// <remarks>
/// [AI Context] Injects required runtime dependencies.
/// [Human] Konstruktor: Bekommt alle wichtigen Einstellungen (Pfade, Google Client) übergeben.
/// </remarks>
public class AttachmentUploader(Client client, string uploadFolder, string[] includePaths, bool isAiStudio, string gcsBucketName, double? googleVideoFps = null, bool inlineImages = false, int fileActivationDelaySeconds = 130, int uploadTimeoutSeconds = 240, int uploadMaxRetries = 10) {
    private static readonly HashSet<string> s_textExtensions = [".md", ".txt", ".cs", ".json", ".xml", ".html", ".py", ".js", ".ts", ".css", ".tex"];
    public static bool HasJustUploaded { get; set; } = false;
    private readonly string _uploadFolder = uploadFolder;
    private readonly string[] _includePaths = includePaths ?? [];
    private readonly bool _isAiStudio = isAiStudio;
    private readonly string _gcsBucketName = gcsBucketName;
    private readonly double? _googleVideoFps = googleVideoFps;
    private readonly int _fileActivationDelaySeconds = fileActivationDelaySeconds;
    private readonly int _uploadTimeoutSeconds = uploadTimeoutSeconds;
    private readonly int _uploadMaxRetries = uploadMaxRetries;
    // [AI Context] If true, image files are embedded as InlineData blobs instead of being uploaded via File API.
    // Inline blobs are part of the stable request prefix and enable Google's implicit prefix caching.
    private readonly bool _inlineImages = inlineImages;
    public Func<Client>? ClientFactory { get; set; }

    private Client _client = client;

    /// <summary>
    /// [AI Context] Allows dynamic replacement of the GenAI client, useful when switching API keys at runtime.
    /// [Human] Erlaubt es, den Google Client im laufenden Betrieb auszutauschen (z.B. wenn man den API Key ändert).
    /// </summary>
    public void UpdateClient(Client newClient) {
        _client = newClient;
    }

    /// <summary>
    /// [AI Context] Parses the multipart payload, resolves physical paths, and streams payloads to the model context.
    /// Returning boolean flag dictates if the parent loop should proceed or halt.
    /// [Human] Nimmt den 'attach' Befehl auseinander, zerlegt ihn in Dateien und die eigentliche Text-Frage.
    /// </summary>
    public async Task<(bool success, string promptText, List<Part> parts)> ProcessAttachmentsAsync(string input, bool asSystemInstruction = false, string? baseDirectory = null, CancellationToken cancellationToken = default) {
        var parts = new List<Part>();

        // [AI Context] Implements a custom syntax parser: "attach [file1, file2] | [prompt]"
        string payload = input[7..].Trim(); // Entfernt das "attach " am Anfang
        string[] payloadParts = payload.Split('|', 2);
        string filesPart = payloadParts[0];
        string promptText = payloadParts.Length > 1 ? payloadParts[1].Trim() : "";

        string[] fileNames = filesPart.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);
        bool anyFileLoaded = false;
        var loadedNames = new List<string>();

        // ====================================================================
        // NEUER, SAUBERER ANSATZ ZUR DATEISUCHE UND ZUM UPLOAD
        // ====================================================================
        foreach (var fileName in fileNames) {
            string rawName = fileName.Trim().Trim('"', '\'');
            string? resolvedPath = ResolveFilePath(rawName, out List<string> searchedLocations);

            if (resolvedPath != null) {
                bool loaded = await UploadAndAttachFileAsync(resolvedPath, parts, asSystemInstruction, baseDirectory, cancellationToken);
                if (loaded) {
                    anyFileLoaded = true;
                    loadedNames.Add(Path.GetFileName(resolvedPath));
                }
            }
            else {
                WriteLine($"\n[FEHLER] Die Datei '{rawName}' wurde absolut nirgends gefunden.");
                WriteLine("  Ich habe exakt hier gesucht:");
                foreach (var loc in searchedLocations) {
                    WriteLine($"   - {loc}");
                }
            }
        }

        if (loadedNames.Count > 0) {
            WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
        }

        return (anyFileLoaded, promptText, parts);
    }

    /// <summary>
    /// [AI Context] Implements a fallback strategy pattern for file resolution. 
    /// Prioritizes absolute paths -> base execution directory -> upload directory -> configured include directories.
    /// [Human] Die "Trüffelschwein"-Methode. Sie sucht in allen konfigurierten Ordnern nach der Datei, bis sie sie findet.
    /// </summary>
    private string? ResolveFilePath(string originalPath, out List<string> searchedLocations) {
        searchedLocations = [];

        // 1. Direkter Pfad (Arbeitsverzeichnis)
        string currentDirFile = Path.GetFullPath(originalPath);
        searchedLocations.Add(currentDirFile);
        if (System.IO.File.Exists(currentDirFile)) return currentDirFile;

        // 2. Base Directory (Wo die .exe liegt)
        string exeDirFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, originalPath);
        if (!exeDirFile.Equals(currentDirFile, StringComparison.OrdinalIgnoreCase)) {
            searchedLocations.Add(exeDirFile);
            if (System.IO.File.Exists(exeDirFile)) return exeDirFile;
        }

        // 3. UploadFolder
        if (!string.IsNullOrWhiteSpace(_uploadFolder)) {
            string uploadPath = Path.Combine(_uploadFolder, originalPath);
            searchedLocations.Add(uploadPath);
            if (System.IO.File.Exists(uploadPath)) return uploadPath;
        }

        // 4. IncludePaths
        // [AI Context] Evaluates configured arrays of static fallback directories (e.g., where FFmpeg typically drops processed videos).
        foreach (var inc in _includePaths) {
            if (System.IO.File.Exists(inc)) {
                if (Path.GetFileName(inc).Equals(originalPath, StringComparison.OrdinalIgnoreCase))
                    return inc;
            }
            else if (System.IO.Directory.Exists(inc)) {
                string dirPath = Path.Combine(inc, originalPath);
                searchedLocations.Add(dirPath);
                if (System.IO.File.Exists(dirPath)) return dirPath;
            }
        }

        return null;
    }

    /// <summary>
    /// [AI Context] Orchestrates the actual data transfer. 
    /// Local text files are embedded raw as strings to save upload bandwidth. Media is pushed via Google File API or GCS buckets.
    /// [Human] Entscheidet anhand der Dateiendung: Ist es Text, wird es direkt in den Chat kopiert. Ist es Media, wird es hochgeladen.
    /// </summary>
    private async Task<bool> UploadAndAttachFileAsync(string filePath, List<Part> parts, bool asSystemInstruction = false, string? baseDirectory = null, CancellationToken cancellationToken = default) {
        string ext = Path.GetExtension(filePath).ToLower();
        string rawDisplayPath = !string.IsNullOrEmpty(baseDirectory)
            ? Path.GetRelativePath(baseDirectory, filePath)
            : filePath;
        string displayPath = FileTreeRenderer.NormalizeRelativePath(rawDisplayPath);

        if (s_textExtensions.Contains(ext)) {
            if (asSystemInstruction) {
                WriteLine($"  [Lokal] Lese Textdokument '{Path.GetFileName(filePath)}' für System Instruction ein...");
                string fileContent = await System.IO.File.ReadAllTextAsync(filePath, cancellationToken);
                parts.Add(new Part { Text = $"\n<file path=\"{displayPath}\">\n{fileContent}\n</file>\n" });
                return true;
            }
            else {
                WriteLine($"  [Lokal] Lese Textdokument '{Path.GetFileName(filePath)}' ein...");
                string fileContent = await System.IO.File.ReadAllTextAsync(filePath, cancellationToken);
                parts.Add(new Part { Text = $"<attached_file name=\"{Path.GetFileName(filePath)}\">\n{fileContent}\n</attached_file>\n\n" });
                return true;
            }
        }

        string? mimeType = ext switch {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".mp3" => "audio/mpeg",
            ".aac" => "audio/aac",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            _ => null
        };

        if (mimeType == null) {
            WriteLine($"[FEHLER] Der Dateityp '{ext}' von '{Path.GetFileName(filePath)}' wird nicht unterstützt.");
            return false;
        }

        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp") {
            // [AI Context] Inline images when: (a) used as system instruction (always), or (b) _inlineImages flag is set.
            // Inline blobs are part of the stable request prefix and enable Google's implicit prefix caching.
            // File API URIs are external references that break prefix caching.
            // [Human] Bilder werden als Blob eingebettet, wenn sie in der System Instruction sind ODER InlineHistoryImages=true ist.
            if (asSystemInstruction || _inlineImages) {
                string context = asSystemInstruction ? "Inline System Instruction" : "Inline History/Prefix-Cache";
                WriteLine($"  [Lokal] Lese Bilddatei '{Path.GetFileName(filePath)}' für {context} ein...");
                byte[] imageBytes = await System.IO.File.ReadAllBytesAsync(filePath, cancellationToken);
                if (asSystemInstruction) {
                    parts.Add(new Part { Text = $"\n<image path=\"{displayPath}\">\n" });
                }
                parts.Add(new Part { InlineData = new Blob { Data = imageBytes, MimeType = mimeType } });
                if (asSystemInstruction) {
                    parts.Add(new Part { Text = "\n</image>\n" });
                }
                return true;
            }
        }

        if (_isAiStudio) {
            // [AI Context] Integrates with Google's newer File API specifically designed for AI Studio.
            // [Human] Das ist der direkte Datei-Upload über die Google AI Studio API (ohne GCS Buckets).
            WriteLine($"  [AI Studio] Lade '{Path.GetFileName(filePath)}' über die Google File API hoch...");
            try {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var uploadConfig = new Google.GenAI.Types.UploadFileConfig { MimeType = mimeType };
                var uploadedFile = await ApiRetryPolicy.ExecuteWithRetryAsync(
                    async () => {
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        attemptCts.CancelAfter(TimeSpan.FromSeconds(_uploadTimeoutSeconds));
                        try {
                            var activeClient = ClientFactory != null ? ClientFactory() : _client;
                            return await activeClient.Files.UploadAsync(filePath, config: uploadConfig, cancellationToken: attemptCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                            throw new TimeoutException($"Upload-Versuch für '{Path.GetFileName(filePath)}' hat nach {_uploadTimeoutSeconds}s nicht reagiert (Timeout).");
                        }
                    },
                    maxRetries: _uploadMaxRetries,
                    retryContext: $"File API Upload: {Path.GetFileName(filePath)}"
                );

                if (uploadedFile?.Name == null) {
                    WriteLine($"  [FEHLER] Die Dateireferenz (Name) vom Server für '{Path.GetFileName(filePath)}' ist null oder Upload nach mehreren Versuchen fehlgeschlagen.");
                    return false;
                }

                stopwatch.Stop();
                string remoteFileName = uploadedFile.Name;

                WriteLine($"  [AI Studio] Upload abgeschlossen. URI: {uploadedFile.Uri}");
                WriteLine($"  [AI Studio] Upload-Dauer: {stopwatch.Elapsed.Minutes} Minuten und {stopwatch.Elapsed.Seconds} Sekunden.");
                Write("  [AI Studio] Warte auf serverseitige Verarbeitung ");

                var fileInfo = await ApiRetryPolicy.ExecuteWithRetryAsync(
                    () => _client.Files.GetAsync(remoteFileName, config: null, cancellationToken: cancellationToken),
                    maxRetries: 10,
                    retryContext: $"Check File State: {Path.GetFileName(filePath)}"
                );
                while (string.Equals(fileInfo?.State?.ToString(), "PROCESSING", StringComparison.OrdinalIgnoreCase)) {
                    Write(".");
                    for (int i = 0; i < 50; i++) {
                        await Task.Delay(100, cancellationToken);
                        if (!InteractiveDelay.IsInSmartDelay && !Console.IsInputRedirected && Console.KeyAvailable) {
                            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                            Write("\n[System] Still waiting for the acknowledgment / processing...\n  [AI Studio] Warte auf serverseitige Verarbeitung ");
                        }
                    }
                    fileInfo = await ApiRetryPolicy.ExecuteWithRetryAsync(
                        () => _client.Files.GetAsync(remoteFileName, config: null, cancellationToken: cancellationToken),
                        maxRetries: 10,
                        retryContext: $"Check File State: {Path.GetFileName(filePath)}"
                    );
                }
                WriteLine();

                if (fileInfo == null || string.Equals(fileInfo?.State?.ToString(), "FAILED", StringComparison.OrdinalIgnoreCase)) {
                    WriteLine($"  [FEHLER] Die serverseitige Verarbeitung von '{Path.GetFileName(filePath)}' ist fehlgeschlagen.");
                    return false;
                }

                WriteLine("  [AI Studio] Datei ist ACTIVE und bereit für Gemini.");
                // [AI Context] Financial & Rate-Limit Guardrail: When using the standard AI Studio API (_isAiStudio == true),
                // we historically waited here. However, as Gemini is stateless, token consumption (TPM) only happens upon the
                // actual GenerateContent request, not during file upload. Thus, waiting after uploading a system instruction is unnecessary.
                if (!asSystemInstruction && (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))) {
                    // [Human] Bei der AI Studio Version warten wir nach großen Datei-Aktivierungen (Videos/Audio).
                    // Für Bilder, PDFs und andere kleine Dateien (z.B. History-Uploads) überspringen wir das,
                    // da der Token-Verbrauch erst beim GenerateContent-Call passiert, nicht beim File-Upload.
                    if (!await InteractiveDelay.SmartDelayAsync(_fileActivationDelaySeconds, $"Warte {_fileActivationDelaySeconds} Sekunden nach Datei-Aktivierung (Token-Refill bei AI Studio, um Max-Token-Fehler zu verhindern)...")) {
                        return false;
                    }
                }
                HasJustUploaded = true;
                var fileDataPart = new Part { FileData = new FileData { FileUri = uploadedFile.Uri, MimeType = mimeType } };
                if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && _googleVideoFps.HasValue) {
                    fileDataPart.VideoMetadata = new() { Fps = _googleVideoFps.Value };
                }
                if (asSystemInstruction) {
                    parts.Add(new Part { Text = $"\n<file path=\"{displayPath}\">\n" });
                    parts.Add(fileDataPart);
                    parts.Add(new Part { Text = "\n</file>\n" });
                }
                else {
                    parts.Add(fileDataPart);
                }
                return true;
            }
            catch (Exception ex) {
                if (ex is OperationCanceledException || ex.InnerException is OperationCanceledException) {
                    WriteLine("\n[INFO] Datei-Upload vom Benutzer abgebrochen.");
                    return false;
                }

                WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                WriteLine($"Originaler Fehlertext: {ex.Message}");

                if (ApiRetryPolicy.IsNetworkConnectionError(ex)) {
                    WriteLine("  [Netzwerk-Fehler] Der Google-Server konnte nicht erreicht werden.");
                    WriteLine("  Bitte prüfe deine Internetverbindung oder DNS-Einstellungen.");
                }

                WriteLine($"  [FEHLER] Upload über File API endgültig fehlgeschlagen.");
                return false;
            }
        }
        else {
            // [AI Context] Integrates with Google Cloud Storage for Enterprise Vertex AI workloads.
            // [Human] Das ist der Upload in deinen (ggf. kostenpflichtigen) Google Cloud Storage Bucket.
            WriteLine($"  [GCS] Lade '{Path.GetFileName(filePath)}' in den Google Cloud Storage hoch...");
            try {
                var uploadResult = await ApiRetryPolicy.ExecuteWithRetryAsync(async () => {
                    var storageClient = await StorageClient.CreateAsync();
                    storageClient.Service.HttpClient.Timeout = TimeSpan.FromMinutes(20);
                    string objectName = $"{Guid.NewGuid()}_{Path.GetFileName(filePath)}";

                    using var fileStream = System.IO.File.OpenRead(filePath);
                    await storageClient.UploadObjectAsync(_gcsBucketName, objectName, mimeType, fileStream, cancellationToken: cancellationToken);
                    return $"gs://{_gcsBucketName}/{objectName}";
                }, maxRetries: 10, retryContext: $"GCS Upload: {Path.GetFileName(filePath)}");

                if (uploadResult == null) {
                    WriteLine($"  [FEHLER] Upload in GCS nach mehreren Versuchen fehlgeschlagen.");
                    return false;
                }

                string gcsUri = uploadResult;
                WriteLine($"  [GCS] Upload abgeschlossen. Sende URI an Gemini: {gcsUri}");

                var fileDataPart = new Part { FileData = new FileData { FileUri = gcsUri, MimeType = mimeType } };
                if (mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && _googleVideoFps.HasValue) {
                    fileDataPart.VideoMetadata = new() { Fps = _googleVideoFps.Value };
                }
                if (asSystemInstruction) {
                    parts.Add(new Part { Text = $"\n<file path=\"{displayPath}\">\n" });
                    parts.Add(fileDataPart);
                    parts.Add(new Part { Text = "\n</file>\n" });
                }
                else {
                    parts.Add(fileDataPart);
                }
                return true;
            }
            catch (Exception ex) {
                if (ex is OperationCanceledException || ex.InnerException is OperationCanceledException) {
                    WriteLine("\n[INFO] GCS Datei-Upload vom Benutzer abgebrochen.");
                    return false;
                }

                WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                WriteLine($"Originaler Fehlertext: {ex.Message}");

                if (ApiRetryPolicy.IsNetworkConnectionError(ex)) {
                    WriteLine("  [Netzwerk-Fehler] Der Google Cloud Storage konnte nicht erreicht werden.");
                    WriteLine("  Bitte prüfe deine Internetverbindung.");
                }

                WriteLine($"  [FEHLER] Upload in GCS endgültig fehlgeschlagen.");
                return false;
            }
        }
    }
}