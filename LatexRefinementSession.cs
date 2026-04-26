using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;

namespace AiInteraction;

public class LatexRefinementConfig
{
  public string GeminiMdPath { get; set; } = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";
  public string Model { get; set; } = "gemini-2.5-pro";
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

    Console.Write("\nBitte gib den Pfad zum Ordner mit den 3 generierten .tex Teilen ein: ");
    string sourceFolder = Console.ReadLine()?.Trim() ?? "";

    if (!Directory.Exists(sourceFolder))
    {
      Console.WriteLine("Ordner nicht gefunden.");
      return;
    }

    var files = Directory.GetFiles(sourceFolder, "*.tex");
    if (files.Length == 0)
    {
      Console.WriteLine("Keine .tex Dateien im Ordner gefunden.");
      return;
    }

    string geminiMdPath = _config.GeminiMdPath;
    string geminiMdContent = System.IO.File.Exists(geminiMdPath) ? await System.IO.File.ReadAllTextAsync(geminiMdPath) : "Keine System-Instruktion gefunden.";

    var parts = new List<Part>();
    parts.Add(new Part
    {
      Text = "Hier sind die generierten Vorlesungsteile (im LaTeX Format), die mit einer alten System-Instruktion generiert wurden.\n" +
                                "Bitte kümmere dich um die Kompilierfähigkeit, füge die Teile logisch zusammen und behebe strukturelle Fehler.\n\n" +
                                "=== ALTE INSTRUKTION ===\n" + geminiMdContent + "\n=== ENDE ALTE INSTRUKTION ==="
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
    try
    {
      var responseStream = _client.Models.GenerateContentStreamAsync(_config.Model, history, requestConfig);
      string fullText = "";

      await foreach (var chunk in responseStream)
      {
        string text = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
        Console.Write(text);
        fullText += text;
      }

      string outPath = Path.Combine(sourceFolder, "refined_output.tex");
      await System.IO.File.WriteAllTextAsync(outPath, fullText);
      Console.WriteLine($"\n\n[Erfolg] Refined LaTeX erfolgreich gespeichert unter: {outPath}");
    }
    catch (Exception ex)
    {
      Console.WriteLine($"\n[Fehler] Beim Refinement ist ein Fehler aufgetreten: {ex.Message}");
    }
  }
}