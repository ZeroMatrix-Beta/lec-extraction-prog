﻿using System;
using System.Threading.Tasks;
using AiInteraction;
using AiInteraction.GoogleAIStudio;
using AiInteraction.Vertex;
using FfmpegUtilities;
using GoogleGenAi;
using Google.GenAI;
using AiInteraction.AutoExtraction;

/// <summary>
/// [AI Context] Main application entry point. Orchestrates the execution flow by delegating
/// either to the FFmpeg processing toolkit or the Gemini chat session based on user input.
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird.
/// </summary>
class Program
{
  static async Task Main(string[] args)
  {
    // [AI Context] Bootstrapper. Demonstrates manual Dependency Injection (DI) pattern.
    // Determines the runtime environment (Vertex vs AI Studio) and wires up the respective isolated dependencies.
    Console.WriteLine("==================================================");
    Console.WriteLine("     Welcome to AI Extraction & Processing        ");
    Console.WriteLine("==================================================");
    Console.WriteLine("Please choose your desired operational mode:");
    Console.WriteLine(" 1) Google AI Studio (API Key / Developer endpoints)");
    Console.WriteLine(" 2) Google Cloud Vertex AI (Enterprise / 'vertex-ai-experiments')");
    Console.WriteLine(" 3) FFmpeg Interactive Manager (Local Audio/Video Processing)");
    Console.WriteLine("--------------------------------------------------");
    Console.WriteLine(" 4) Automated Content Retrieval & Processing (Future Enhancement)");
    Console.WriteLine(" 5) LaTeX Refinement & Post-Processing (Dedicated API Key)");
    Console.Write("\nChoice (1-5): ");

    string? mainChoice = Console.ReadLine()?.Trim();

    if (mainChoice == "4")
    {
      Console.WriteLine("\nWelche API soll für die automatisierte Extraktion genutzt werden?");
      Console.WriteLine(" 1) Google AI Studio");
      Console.WriteLine(" 2) Google Cloud Vertex AI");
      Console.Write("Wahl (1-2): ");
      string? extChoice = Console.ReadLine()?.Trim();

      if (extChoice == "2")
      {
        var config = new VertexAutoExtractionConfig();
        Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
        var attachmentHandler = new AttachmentHandler(client, config.SourceFolder, new[] { config.SourceFolder }, isAiStudio: false, config.GcsBucketName);
        var session = new VertexAutoExtractionSession(client, config, attachmentHandler);
        await session.StartAsync();
      }
      else
      {
        var config = new AiStudioAutoExtractionConfig();
        string apiKey = GoogleAiClientBuilder.ResolveApiKey(config.ActiveApiProfile) ?? "no-key";
        Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
        var attachmentHandler = new AttachmentHandler(client, config.SourceFolder, new[] { config.SourceFolder }, isAiStudio: true, "");
        var session = new AiStudioAutoExtractionSession(client, config, attachmentHandler);
        await session.StartAsync();
      }
      return;
    }

    if (mainChoice == "5")
    {
      // Lade den exklusiven Key für das Refinement
      string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-latex-refinement") ?? "no-key";
      Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
      var session = new LatexRefinementSession(client);
      await session.StartAsync();
      return;
    }

    if (mainChoice == "3")
    {
      var ffmpegConfig = new FfmpegSessionConfig();
      var ffmpegMenu = new FfmpegInteractiveSession(ffmpegConfig.SourceFolder, ffmpegConfig.TargetFolder);
      await ffmpegMenu.StartAsync();
      return;
    }

    bool isVertex = mainChoice == "2";

    if (isVertex)
    {
      // Wire up dependencies for Vertex AI
      var config = new VertexAiConfig();
      Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
      var attachmentHandler = new AttachmentHandler(client, config.UploadFolder, config.IncludePaths, isAiStudio: false, config.GcsBucketName);
      var sessionLogger = new SessionLogger(config.LogFolder);

      var chatSession = new VertexAiChatSession(client, config, sessionLogger, attachmentHandler);
      await chatSession.StartAsync();
    }
    else
    {
      // Wire up dependencies for AI Studio
      var config = new GoogleAIStudioConfig();
      string apiKey = GoogleAiClientBuilder.ResolveApiKey(config.ActiveApiProfile) ?? "no-key";
      Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
      var attachmentHandler = new AttachmentHandler(client, config.UploadFolder, config.IncludePaths, isAiStudio: true, config.GcsBucketName);
      var sessionLogger = new SessionLogger(config.LogFolder);

      var chatSession = new GoogleAIStudioChatSession(client, config, sessionLogger, attachmentHandler, isAiStudio: true);
      await chatSession.StartAsync();
    }
  }
}
