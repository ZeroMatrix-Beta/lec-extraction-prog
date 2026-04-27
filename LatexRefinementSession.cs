using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using DocumentUtilities;

namespace AiInteraction;

public class LatexRefinementConfig
{
  public string GeminiMdPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string Model { get; set; } = "gemini-2.5-pro";
  public string TargetFolder { get; set; } = @"D:\lecture-videos\analysis2\destination\tex-refinement\refined";
  public string SourceFolder { get; set; } = @"D:\lecture-videos\analysis2\destination\tex-refinement";
}

/// <summary>
/// [AI Context] A dedicated session for refining, formatting, and correcting raw LaTeX files.
/// Uses its own isolated API Key (API_KEY-latex-refinement) to separate quotas from extraction workloads.
/// </summary>
public class LatexRefinementSession
{
  private readonly Client _client;
  private readonly LatexRefinementConfig _config;

  public LatexRefinementSession(Client client, LatexRefinementConfig config)
  {
    _client = client;
    _config = config;
  }

  public async Task StartAsync()
  {
    Console.WriteLine("\n==================================================");
    Console.WriteLine("   Starte LaTeX Refinement & Post-Processing");
    Console.WriteLine("==================================================");

    string sourceFolder = _config.SourceFolder;
    Console.WriteLine($"\nSuche nach .tex Dateien in: {sourceFolder}");

    if (!Directory.Exists(sourceFolder))
    {
      Console.WriteLine("Ordner nicht gefunden. Bitte prüfe den SourceFolder in der Konfiguration.");
      return;
    }

    var files = Directory.GetFiles(sourceFolder, "*.tex");
    if (files.Length == 0)
    {
      Console.WriteLine("Keine .tex Dateien im Ordner gefunden.");
      return;
    }

    Console.WriteLine("\nFolgende Dateien wurden gefunden:");
    foreach (var file in files)
    {
      Console.WriteLine($"  - {Path.GetFileName(file)}");
    }

    Console.Write("\nMöchtest du diese Dateien an Gemini schicken und zusammenfügen lassen? (j/n): ");
    string? confirm = Console.ReadLine()?.Trim().ToLower();
    if (confirm != "j" && confirm != "y")
    {
      Console.WriteLine("Vorgang abgebrochen.");
      return;
    }

    string geminiMdPath = _config.GeminiMdPath;
    string geminiMdContent = System.IO.File.Exists(geminiMdPath) ? await System.IO.File.ReadAllTextAsync(geminiMdPath) : "Keine System-Instruktion gefunden.";

    var parts = new List<Part>();
    parts.Add(new Part
    {
      Text = "Here are the generated lecture parts (in LaTeX format) that were transcribed from overlapping video segments.\n" +
             "CRITICAL: The original video segments were cut with a **3-minute overlap**. This means the end of one part and the beginning of the next part contain duplicate transcribed content.\n" +
             "Your primary task is to PERFECTLY MERGE these overlapping parts into a single, cohesive, and continuous document! Identify the overlapping regions, eliminate all duplicate sentences and equations, and stitch the narrative seamlessly together.\n" +
             "Ensure the final output is fully compilable, fix any structural LaTeX errors, and guarantee no overlapping artifacts remain.\n\n" +
             "=== SYSTEM INSTRUCTION USED FOR EXTRACTION ===\n" + geminiMdContent + "\n=== END SYSTEM INSTRUCTION ==="
    });

    foreach (var file in files)
    {
      string content = await System.IO.File.ReadAllTextAsync(file);
      parts.Add(new Part { Text = $"\n\n--- TEIL: {Path.GetFileName(file)} ---\n{content}\n--- ENDE TEIL ---" });
    }

    var history = new List<Content> { new Content { Role = "user", Parts = parts } };

    var requestConfig = new GenerateContentConfig
    {
      Temperature = 0.0f,
      MaxOutputTokens = 65535,
      ThinkingConfig = new ThinkingConfig { ThinkingBudget = 4096 }
    };

    Console.WriteLine($"\nSende Refinement-Anfrage an Gemini ({_config.Model})...");
    int maxRetries = 5;
    int backoff = 15;

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
      try
      {
        if (attempt > 1) Console.WriteLine($"\n[API] Sende Anfrage (Versuch {attempt}/{maxRetries})...");
        var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, history, requestConfig);
        string fullText = "";

        await foreach (var chunk in responseStream)
        {
          string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
          Console.Write(text);
          fullText += text;
        }

        string targetFolder = string.IsNullOrWhiteSpace(_config.TargetFolder) ? sourceFolder : _config.TargetFolder;
        if (!Directory.Exists(targetFolder))
        {
          Directory.CreateDirectory(targetFolder);
        }

        string outPath = Path.Combine(targetFolder, "refined_output.tex");
        await System.IO.File.WriteAllTextAsync(outPath, fullText);
        Console.WriteLine($"\n\n[Erfolg] Refined LaTeX erfolgreich gespeichert unter: {outPath}");

        // Automatische PDF Kompilierung im Hintergrund
        var latexToolkit = new LatexToolkit();
        await latexToolkit.CompilePdfAsync(outPath);
        break;
      }
      catch (Exception ex)
      {
        bool isOverloaded = ex.Message.Contains("429") || ex.Message.Contains("503") || ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase);
        if (attempt < maxRetries && isOverloaded)
        {
          Console.WriteLine($"\n[Rate Limit / Überlastung] Versuch {attempt}/{maxRetries} fehlgeschlagen. Warte {backoff} Sekunden... ({ex.Message})");
          await Task.Delay(backoff * 1000);
          backoff *= 2;
        }
        else
        {
          Console.WriteLine($"\n[Fehler] Beim Refinement ist ein Fehler aufgetreten: {ex.Message}");
          break;
        }
      }
    }
  }
}