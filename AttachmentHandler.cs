using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;

namespace AiInteraction;

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
  private readonly Client _client;
  private readonly IUserInterface _ui;

  // [AI Context] Injects required runtime dependencies.
  public AttachmentHandler(Client client, string uploadFolder, string[] includePaths, bool isAiStudio, string gcsBucketName, IUserInterface ui)
  {
    _client = client;
    _uploadFolder = uploadFolder;
    _includePaths = includePaths ?? Array.Empty<string>();
    _isAiStudio = isAiStudio;
    _gcsBucketName = gcsBucketName;
    _ui = ui;
  }

  /// <summary>
  /// [AI Context] Parses the multipart payload, resolves physical paths, and streams payloads to the model context.
  /// Returning boolean flag dictates if the parent loop should proceed or halt.
  /// [Human] Nimmt den 'attach' Befehl auseinander, zerlegt ihn in Dateien und die eigentliche Text-Frage.
  /// </summary>
  public async Task<(bool success, string promptText, List<Part> parts)> ProcessAttachmentsAsync(string input)
  {
    var parts = new List<Part>();

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
        _ui.WriteLine($"\n[Fehler] Die Datei '{rawName}' wurde absolut nirgends gefunden.");
        _ui.WriteLine("  Ich habe exakt hier gesucht:");
        foreach (var loc in searchedLocations)
        {
          _ui.WriteLine($"   - {loc}");
        }

        // Magic Debugging Trick: Zeige, was WIRKLICH im aktuellen Arbeitsverzeichnis liegt!
        _ui.WriteLine($"\n  -> [DEBUG] Dateien im aktuellen Arbeitsverzeichnis ({System.Environment.CurrentDirectory}):");
        try
        {
          var currentFiles = Directory.GetFiles(System.Environment.CurrentDirectory).Select(Path.GetFileName).ToArray();
          if (currentFiles.Length > 0)
          {
            _ui.WriteLine($"     {string.Join(", ", currentFiles)}");
          }
          else
          {
            _ui.WriteLine("     (Ordner ist leer!)");
          }
        }
        catch { }
      }
    }

    if (loadedNames.Count > 0)
    {
      _ui.WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
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
      _ui.WriteLine($"  [Lokal] Lese Textdokument '{Path.GetFileName(filePath)}' ein...");
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
      _ui.WriteLine($"[Fehler] Der Dateityp '{ext}' von '{Path.GetFileName(filePath)}' wird nicht unterstützt.");
      return false;
    }

    if (_isAiStudio)
    {
      // [AI Context] Integrates with Google's newer File API specifically designed for AI Studio.
      // [Human] Das ist der direkte Datei-Upload über die Google AI Studio API (ohne GCS Buckets).
      _ui.WriteLine($"  [AI Studio] Lade '{Path.GetFileName(filePath)}' über die Google File API hoch...");
      try
      {
        var uploadConfig = new Google.GenAI.Types.UploadFileConfig { MimeType = mimeType };
        var uploadedFile = await _client.Files.UploadAsync(filePath, config: uploadConfig);

        _ui.WriteLine($"  [AI Studio] Upload abgeschlossen. URI: {uploadedFile.Uri}");
        _ui.Write("  [AI Studio] Warte auf serverseitige Verarbeitung ");

        var fileInfo = await _client.Files.GetAsync(uploadedFile.Name);
        while (fileInfo.State != null && fileInfo.State.ToString().Equals("PROCESSING", StringComparison.OrdinalIgnoreCase))
        {
          _ui.Write(".");
          await Task.Delay(5000); // 5 Sekunden warten und erneut abfragen
          fileInfo = await _client.Files.GetAsync(uploadedFile.Name);
        }
        _ui.WriteLine();

        if (fileInfo.State != null && fileInfo.State.ToString().Equals("FAILED", StringComparison.OrdinalIgnoreCase))
        {
          _ui.WriteLine($"  [Fehler] Die serverseitige Verarbeitung von '{Path.GetFileName(filePath)}' ist fehlgeschlagen.");
          return false;
        }

        _ui.WriteLine("  [AI Studio] Datei ist ACTIVE und bereit für Gemini.");
        parts.Add(new Part { FileData = new FileData { FileUri = uploadedFile.Uri, MimeType = mimeType } });
        return true;
      }
      catch (Exception ex)
      {
        _ui.WriteLine($"  [Fehler] Beim Upload über File API ist ein Fehler aufgetreten: {ex.Message}");
        return false;
      }
    }
    else
    {
      // [AI Context] Integrates with Google Cloud Storage for Enterprise Vertex AI workloads.
      // [Human] Das ist der Upload in deinen (ggf. kostenpflichtigen) Google Cloud Storage Bucket.
      _ui.WriteLine($"  [GCS] Lade '{Path.GetFileName(filePath)}' in den Google Cloud Storage hoch...");
      try
      {
        var storageClient = await StorageClient.CreateAsync();
        string objectName = $"{Guid.NewGuid()}_{Path.GetFileName(filePath)}";

        using var fileStream = System.IO.File.OpenRead(filePath);
        await storageClient.UploadObjectAsync(_gcsBucketName, objectName, mimeType, fileStream);

        string gcsUri = $"gs://{_gcsBucketName}/{objectName}";
        _ui.WriteLine($"  [GCS] Upload abgeschlossen. Sende URI an Gemini: {gcsUri}");

        parts.Add(new Part { FileData = new FileData { FileUri = gcsUri, MimeType = mimeType } });
        return true;
      }
      catch (Exception ex)
      {
        _ui.WriteLine($"  [Fehler] Beim Upload in GCS ist ein Fehler aufgetreten: {ex.Message}");
        return false;
      }
    }
  }
}