using System;
using System.Threading.Tasks;
using Google.GenAI;

namespace AiInteraction.AutoExtraction;

// ==========================================
// 1. Google AI Studio (Free/Developer Tier)
// ==========================================

public class AiStudioAutoExtractionConfig
{
  public int ActiveApiProfile { get; set; } = 1;
  public string SourceFolder { get; set; } = @"D:\lecture-videos\d-und-a\new";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\d-und-a\extracted";
}

public class AiStudioAutoExtractionSession
{
  private readonly Client _client;
  private readonly AiStudioAutoExtractionConfig _config;

  public AiStudioAutoExtractionSession(Client client, AiStudioAutoExtractionConfig config)
  {
    _client = client;
    _config = config;
  }

  public async Task StartAsync()
  {
    Console.WriteLine("\n[AutoExtraction] Starte AI Studio Extraction Session (Dummy)...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    while (true)
    {
      Console.Write("\nAutoExtract-AIStudio> ");
      string? input = Console.ReadLine();
      if (string.IsNullOrWhiteSpace(input)) continue;
      if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

      Console.WriteLine($"[Dummy] Verarbeite Input: {input}");
    }
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
}

public class VertexAutoExtractionSession
{
  private readonly Client _client;
  private readonly VertexAutoExtractionConfig _config;

  public VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config)
  {
    _client = client;
    _config = config;
  }

  public async Task StartAsync()
  {
    Console.WriteLine("\n[AutoExtraction] Starte Vertex AI Extraction Session (Dummy)...");
    Console.WriteLine($"[AutoExtraction] Quelle (Source): {_config.SourceFolder}");
    Console.WriteLine($"[AutoExtraction] Ziel (Target): {_config.TargetFolder}");

    while (true)
    {
      Console.Write("\nAutoExtract-Vertex> ");
      string? input = Console.ReadLine();
      if (string.IsNullOrWhiteSpace(input)) continue;
      if (input.Equals("exit", StringComparison.OrdinalIgnoreCase) || input.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;

      Console.WriteLine($"[Dummy] Verarbeite Input: {input}");
    }
  }
}