using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using static System.Console;

namespace AiInteraction;

public class AttachmentHandlerConfig
{
  public string UploadFolder { get; set; } = @"D:\gemin-upload-folder";
  public string[] IncludePaths { get; set; } = Array.Empty<string>();
  public bool IsAiStudio { get; set; } = true;
  public string GcsBucketName { get; set; } = "";
}

/// <summary>
/// [AI Context] Specialized handler for parsing commands, discovering local files, and securely uploading them to Google APIs.
/// Abstracted from DirectAIInteraction to comply with Single Responsibility Principle.
/// [Human] Diese Klasse kümmert sich NUR um Dateien. Sie sucht sie auf deiner Festplatte und lädt sie für die KI hoch.
/// </summary>
public class AttachmentHandler
{
  private readonly string _uploadFolder;
  private readonly string[] _includePaths;
  private readonly bool _isAiStudio;
  private readonly string _gcsBucketName;
  private Client _client;

  // [AI Context] Injects required runtime dependencies.
  public AttachmentHandler(Client client, AttachmentHandlerConfig config)
  {
    _client = client;
    _uploadFolder = config.UploadFolder;
    _includePaths = config.IncludePaths ?? Array.Empty<string>();
    _isAiStudio = config.IsAiStudio;
    _gcsBucketName = config.GcsBucketName;
  }

  public void UpdateClient(Client newClient)
  {
    _client = newClient;
  }

  /// <summary>
  /// [AI Context] Parses the multipart payload, resolves physical paths, and streams payloads to the model context.
  /// Returning boolean flag dictates if the parent loop should proceed or halt.
  /// [Human] Nimmt den 'attach' Befehl auseinander, zerlegt ihn in Dateien und die eigentliche Text-Frage.
  /// </summary>
  public async Task<(bool success, string promptText, List<Part> parts)> ProcessAttachmentsAsync(string input)
  {
    var parts = new List<Part>();

    // [AI Context] Implements a custom syntax parser: "attach [file1, file2] | [prompt]"
    string payload = input.Substring(7).Trim(); // Entfernt das "attach " am Anfang
    string[] payloadParts = payload.Split('|', 2);
    string filesPart = payloadParts[0];
    string promptText = payloadParts.Length > 1 ? payloadParts[1].Trim() : "";

    string[] fileNames = filesPart.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
    bool anyFileLoaded = false;
    var loadedNames = new List<string>();

    // ====================================================================
    // NEUER, SAUBERER ANSATZ ZUR DATEISUCHE UND ZUM UPLOAD
    // ====================================================================
    foreach (var fileName in fileNames)
    {
      string rawName = fileName.Trim().Trim('"', '\'');
      string? resolvedPath = ResolveFilePath(rawName, out List<string> searchedLocations);

      if (resolvedPath != null)
      {
        bool loaded = await UploadAndAttachFileAsync(resolvedPath, parts);
        if (loaded)
        {
          anyFileLoaded = true;
          loadedNames.Add(Path.GetFileName(resolvedPath));
        }
      }
      else
      {
        WriteLine($"\n[Fehler] Die Datei '{rawName}' wurde absolut nirgends gefunden.");
        WriteLine("  Ich habe exakt hier gesucht:");
        foreach (var loc in searchedLocations)
        {
          WriteLine($"   - {loc}");
        }
      }
    }

    if (loadedNames.Count > 0)
    {
      WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
    }

    return (anyFileLoaded, promptText, parts);
  }

  /// <summary>
  /// [AI Context] Implements a fallback strategy pattern for file resolution. 
  /// Prioritizes absolute paths -> base execution directory -> upload directory -> configured include directories.
  /// [Human] Die "Trüffelschwein"-Methode. Sie sucht in allen konfigurierten Ordnern nach der Datei, bis sie sie findet.
  /// </summary>
  private string? ResolveFilePath(string originalPath, out List<string> searchedLocations)
  {
    searchedLocations = new List<string>();

    // 1. Direkter Pfad (Arbeitsverzeichnis)
    string currentDirFile = Path.GetFullPath(originalPath);
    searchedLocations.Add(currentDirFile);
    if (System.IO.File.Exists(currentDirFile)) return currentDirFile;

    // 2. Base Directory (Wo die .exe liegt)
    string exeDirFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, originalPath);
    if (!exeDirFile.Equals(currentDirFile, StringComparison.OrdinalIgnoreCase))
    {
      searchedLocations.Add(exeDirFile);
      if (System.IO.File.Exists(exeDirFile)) return exeDirFile;
    }

    // 3. UploadFolder
    if (!string.IsNullOrWhiteSpace(_uploadFolder))
    {
      string uploadPath = Path.Combine(_uploadFolder, originalPath);
      searchedLocations.Add(uploadPath);
      if (System.IO.File.Exists(uploadPath)) return uploadPath;
    }

    // 4. IncludePaths
    // [AI Context] Evaluates configured arrays of static fallback directories (e.g., where FFmpeg typically drops processed videos).
    foreach (var inc in _includePaths)
    {
      if (System.IO.File.Exists(inc))
      {
        if (Path.GetFileName(inc).Equals(originalPath, StringComparison.OrdinalIgnoreCase))
          return inc;
      }
      else if (System.IO.Directory.Exists(inc))
      {
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
  private async Task<bool> UploadAndAttachFileAsync(string filePath, List<Part> parts)
  {
    string ext = Path.GetExtension(filePath).ToLower();

    if (new[] { ".md", ".txt", ".cs", ".json", ".xml", ".html", ".py", ".js", ".ts", ".css", ".tex" }.Contains(ext))
    {
      WriteLine($"  [Lokal] Lese Textdokument '{Path.GetFileName(filePath)}' ein...");
      string fileContent = await System.IO.File.ReadAllTextAsync(filePath);
      parts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(filePath)}) ---\n{fileContent}\n--- DOKUMENT ENDE ---" });
      return true;
    }

    string? mimeType = ext switch
    {
      ".jpg" or ".jpeg" => "image/jpeg",
      ".png" => "image/png",
      ".webp" => "image/webp",
      ".pdf" => "application/pdf",
      ".mp3" => "audio/mpeg",
      ".wav" => "audio/wav",
      ".mp4" => "video/mp4",
      _ => null
    };

    if (mimeType == null)
    {
      WriteLine($"[Fehler] Der Dateityp '{ext}' von '{Path.GetFileName(filePath)}' wird nicht unterstützt.");
      return false;
    }

    if (_isAiStudio)
    {
      // [AI Context] Integrates with Google's newer File API specifically designed for AI Studio.
      // [Human] Das ist der direkte Datei-Upload über die Google AI Studio API (ohne GCS Buckets).
      WriteLine($"  [AI Studio] Lade '{Path.GetFileName(filePath)}' über die Google File API hoch...");
      try
      {
        var uploadConfig = new Google.GenAI.Types.UploadFileConfig { MimeType = mimeType };
        var uploadedFile = await _client.Files.UploadAsync(filePath, config: uploadConfig);

        if (uploadedFile.Name == null)
        {
          WriteLine($"  [Fehler] Die Dateireferenz (Name) vom Server für '{Path.GetFileName(filePath)}' ist null.");
          return false;
        }

        string remoteFileName = uploadedFile.Name;

        WriteLine($"  [AI Studio] Upload abgeschlossen. URI: {uploadedFile.Uri}");
        Write("  [AI Studio] Warte auf serverseitige Verarbeitung ");

        var fileInfo = await _client.Files.GetAsync(remoteFileName);
        while (string.Equals(fileInfo?.State?.ToString(), "PROCESSING", StringComparison.OrdinalIgnoreCase))
        {
          Write(".");
          for (int i = 0; i < 50; i++)
          {
            await Task.Delay(100);
            if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
              while (Console.KeyAvailable) Console.ReadKey(intercept: true);
              Write("\n[System] Still waiting for the acknowledgment / processing...\n  [AI Studio] Warte auf serverseitige Verarbeitung ");
            }
          }
          fileInfo = await _client.Files.GetAsync(remoteFileName);
        }
        WriteLine();

        if (string.Equals(fileInfo?.State?.ToString(), "FAILED", StringComparison.OrdinalIgnoreCase))
        {
          WriteLine($"  [Fehler] Die serverseitige Verarbeitung von '{Path.GetFileName(filePath)}' ist fehlgeschlagen.");
          return false;
        }

        WriteLine("  [AI Studio] Datei ist ACTIVE und bereit für Gemini.");
        parts.Add(new Part { FileData = new FileData { FileUri = uploadedFile.Uri, MimeType = mimeType } });
        return true;
      }
      catch (Exception ex)
      {
        // [AI Context] RULE: Always include the original exception message (ex.Message or ex.ToString()) in error outputs to aid debugging.
        WriteLine($"  [Fehler] Beim Upload über File API ist ein Fehler aufgetreten: {ex.Message}");
        return false;
      }
    }
    else
    {
      // [AI Context] Integrates with Google Cloud Storage for Enterprise Vertex AI workloads.
      // [Human] Das ist der Upload in deinen (ggf. kostenpflichtigen) Google Cloud Storage Bucket.
      WriteLine($"  [GCS] Lade '{Path.GetFileName(filePath)}' in den Google Cloud Storage hoch...");
      try
      {
        var storageClient = await StorageClient.CreateAsync();
        string objectName = $"{Guid.NewGuid()}_{Path.GetFileName(filePath)}";

        using var fileStream = System.IO.File.OpenRead(filePath);
        await storageClient.UploadObjectAsync(_gcsBucketName, objectName, mimeType, fileStream);

        string gcsUri = $"gs://{_gcsBucketName}/{objectName}";
        WriteLine($"  [GCS] Upload abgeschlossen. Sende URI an Gemini: {gcsUri}");

        parts.Add(new Part { FileData = new FileData { FileUri = gcsUri, MimeType = mimeType } });
        return true;
      }
      catch (Exception ex)
      {
        // [AI Context] RULE: Always include the original exception message (ex.Message or ex.ToString()) in error outputs to aid debugging.
        WriteLine($"  [Fehler] Beim Upload in GCS ist ein Fehler aufgetreten:\n{ex}");
        return false;
      }
    }
  }
}