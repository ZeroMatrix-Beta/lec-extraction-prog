using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;

namespace AiInteraction.AutoExtraction;

// ==========================================
// 1. Google AI Studio (Free/Developer Tier)
// ==========================================

public class AiStudioAutoExtractionConfig
{
  public int ActiveApiProfile { get; set; } = 1;
  public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string Model { get; set; } = "gemini-2.5-pro";
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}

public class AiStudioAutoExtractionSession
{
  private readonly Client _client;
  private readonly AiStudioAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;

  public AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config, AttachmentHandler attachmentHandler)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
  }

  public async Task StartAsync()
  {
    Console.WriteLine($"\n[AutoExtraction] Starte AI Studio Extraction Session (Profil {_config.ActiveApiProfile})...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    if (!Directory.Exists(_config.SourceFolder))
    {
      Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
      return;
    }

    if (!Directory.Exists(_config.TargetFolder))
    {
      Directory.CreateDirectory(_config.TargetFolder);
    }

    string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4"); // Passe die Endung nach Bedarf an
    if (filesToProcess.Length == 0)
    {
      Console.WriteLine("[AutoExtraction] Keine Dateien zum Verarbeiten gefunden.");
      return;
    }

    string systemInstruction = "";
    if (File.Exists(_config.SystemInstructionPath))
    {
      systemInstruction = await File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"[INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }
    else
    {
      Console.WriteLine($"[WARNUNG] System Instruction nicht gefunden: {_config.SystemInstructionPath}");
    }

    Console.WriteLine($"[AutoExtraction] {filesToProcess.Length} Datei(en) gefunden. Starte Verarbeitung...");

    // [AI Context] TODO: Future Architecture Note
    // This section will become significantly more complex. We need to integrate the FfmpegToolkit here
    // to automatically cut/split long lecture videos into 10-12 minute chunks before uploading them.
    // The current loop is a simplified placeholder that assumes the files are already prepared.
    foreach (var file in filesToProcess)
    {
      string targetFilePath = Path.Combine(_config.TargetFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
      if (File.Exists(targetFilePath))
      {
        Console.WriteLine($"\n[Übersprungen] {Path.GetFileName(file)} wurde bereits verarbeitet.");
        continue;
      }

      bool success = false;
      while (!success)
      {
        try
        {
          Console.WriteLine($"\n[Verarbeite] {Path.GetFileName(file)}...");

          // 1. Upload Video
          var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{file}\" | {_config.Prompt}");
          if (!uploadSuccess || attachmentParts.Count == 0)
          {
            Console.WriteLine($"\n  [Fehler] Upload fehlgeschlagen für {Path.GetFileName(file)}. Überspringe Datei.");
            break;
          }

          var parts = new List<Part>(attachmentParts);
          parts.Add(new Part { Text = parsedPrompt });
          var contents = new List<Content> { new Content { Role = "user", Parts = parts } };

          // 2. Configure Request
          var requestConfig = new GenerateContentConfig
          {
            Temperature = 0.0f,
            MaxOutputTokens = 65535
          };

          if (!string.IsNullOrWhiteSpace(systemInstruction))
          {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
          }

          if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase))
          {
            requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };
          }

          // 3. API Call
          Console.WriteLine($"  [API] Sende Anfrage an {_config.Model} (kann dauern)...");
          var response = await _client.Models.GenerateContentAsync(_config.Model, contents, requestConfig);

          // 4. Save to Disk
          string outputText = response.Text ?? "";
          string header = $"% ==========================================\n" +
                          $"% AutoExtraction Source: {Path.GetFileName(file)}\n" +
                          $"% Model: {_config.Model}\n" +
                          $"% ==========================================\n\n";

          await File.WriteAllTextAsync(targetFilePath, header + outputText);
          Console.WriteLine($"  [Erfolg] Datei {Path.GetFileName(file)} extrahiert und gespeichert unter: {targetFilePath}");

          success = true;
        }
        catch (Exception ex)
        {
          // [Regel-konformes Verhalten] Wir respektieren Quota-Limits!
          if (ex.Message.Contains("429") || ex.Message.Contains("quota") || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase))
          {
            Console.WriteLine($"\n[Rate Limit erreicht] Googles Free-Tier Limit für Projekt {_config.ActiveApiProfile} schlägt an.");
            Console.WriteLine("  -> Wir tricksen das System nicht aus, sondern warten respektvoll 60 Sekunden...");

            for (int i = 60; i > 0; i -= 10)
            {
              Console.Write($"{i}s... ");
              await Task.Delay(10000);
            }
          }
          else
          {
            Console.WriteLine($"\n[Fehler] Unerwarteter Abbruch bei {Path.GetFileName(file)}: {ex.Message}");
            break; // Bei anderen Fehlern gehen wir zur nächsten Datei über
          }
        }
      }
    }

    Console.WriteLine("\n[AutoExtraction] Alle Dateien im Ordner wurden verarbeitet!");
  }
}

// ==========================================
// 2. Google Cloud Vertex AI (Enterprise Tier)
// ==========================================

public class VertexAutoExtractionConfig
{
  public string ProjectId { get; set; } = "vertex-ai-experiments-494320";
  public string Location { get; set; } = "global";
  public string GcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
  public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";
  public string SystemInstructionPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string Model { get; set; } = "gemini-2.5-pro";
  public string Prompt { get; set; } = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}

public class VertexAutoExtractionSession
{
  private readonly Client _client;
  private readonly VertexAutoExtractionConfig _config;
  private readonly AttachmentHandler _attachmentHandler;

  public VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentHandler attachmentHandler)
  {
    _client = client;
    _config = config;
    _attachmentHandler = attachmentHandler;
  }

  public async Task StartAsync()
  {
    Console.WriteLine("\n[AutoExtraction] Starte Vertex AI Enterprise Extraction Session...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    if (!Directory.Exists(_config.SourceFolder))
    {
      Console.WriteLine($"[Fehler] Quellordner nicht gefunden: {_config.SourceFolder}");
      return;
    }

    if (!Directory.Exists(_config.TargetFolder))
    {
      Directory.CreateDirectory(_config.TargetFolder);
    }

    await CleanupBucketAsync(); // Clean up before starting

    string systemInstruction = "";
    if (File.Exists(_config.SystemInstructionPath))
    {
      systemInstruction = await File.ReadAllTextAsync(_config.SystemInstructionPath);
      Console.WriteLine($"[INFO] System Instruction geladen: {Path.GetFileName(_config.SystemInstructionPath)}");
    }

    string[] filesToProcess = Directory.GetFiles(_config.SourceFolder, "*.mp4");
    if (filesToProcess.Length == 0)
    {
      Console.WriteLine("[AutoExtraction] Keine Dateien zum Verarbeiten gefunden.");
      return;
    }

    Console.WriteLine($"[AutoExtraction] {filesToProcess.Length} Datei(en) gefunden. Starte Verarbeitung...");

    // [AI Context] TODO: Future Architecture Note
    // This section will become significantly more complex. We need to integrate the FfmpegToolkit here
    // to automatically cut/split long lecture videos into 10-12 minute chunks before uploading them.
    // The current loop is a simplified placeholder that assumes the files are already prepared.
    foreach (var file in filesToProcess)
    {
      string targetFilePath = Path.Combine(_config.TargetFolder, Path.GetFileNameWithoutExtension(file) + ".tex");
      if (File.Exists(targetFilePath))
      {
        Console.WriteLine($"\n[Übersprungen] {Path.GetFileName(file)} wurde bereits verarbeitet.");
        continue;
      }

      try
      {
        Console.WriteLine($"\n[Verarbeite] {Path.GetFileName(file)}...");

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{file}\" | {_config.Prompt}");
        if (!uploadSuccess || attachmentParts.Count == 0)
        {
          Console.WriteLine($"\n  [Fehler] Upload fehlgeschlagen. Überspringe.");
          continue;
        }

        var parts = new List<Part>(attachmentParts);
        parts.Add(new Part { Text = parsedPrompt });
        var contents = new List<Content> { new Content { Role = "user", Parts = parts } };

        var requestConfig = new GenerateContentConfig
        {
          Temperature = 0.0f,
          MaxOutputTokens = 65535
        };

        if (!string.IsNullOrWhiteSpace(systemInstruction)) requestConfig.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = systemInstruction } } };
        if (_config.Model.Contains("gemini-2.5", StringComparison.OrdinalIgnoreCase)) requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 };

        Console.WriteLine($"  [API] Sende Anfrage an {_config.Model}...");
        var response = await _client.Models.GenerateContentAsync(_config.Model, contents, requestConfig);

        string outputText = response.Text ?? "";
        string header = $"% ==========================================\n% AutoExtraction Source: {Path.GetFileName(file)}\n% Model: {_config.Model}\n% ==========================================\n\n";

        await File.WriteAllTextAsync(targetFilePath, header + outputText);
        Console.WriteLine($"  [Erfolg] Gespeichert unter: {targetFilePath}");
      }
      catch (Exception ex)
      {
        Console.WriteLine($"\n  [Fehler] Abbruch bei {Path.GetFileName(file)}: {ex.Message}");
      }
      finally
      {
        // ALWAYS clean up GCS after each file to minimize enterprise storage costs!
        await CleanupBucketAsync();
      }
    }
    Console.WriteLine("\n[AutoExtraction] Vertex Batch-Verarbeitung abgeschlossen!");
  }

  private async Task CleanupBucketAsync()
  {
    if (string.IsNullOrWhiteSpace(_config.GcsBucketName)) return;
    try
    {
      var storageClient = await StorageClient.CreateAsync();
      var objects = storageClient.ListObjectsAsync(_config.GcsBucketName);
      int count = 0;
      await foreach (var obj in objects)
      {
        await storageClient.DeleteObjectAsync(_config.GcsBucketName, obj.Name);
        count++;
      }
      if (count > 0) Console.WriteLine($"  [GCS] {count} temporäre Datei(en) gelöscht, um Storage-Kosten zu sparen.");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"  [GCS Warnung] Konnte Bucket nicht bereinigen: {ex.Message}");
    }
  }
}