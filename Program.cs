﻿using System;
using System.Threading.Tasks;
using DirectChatAiInteraction;
using DirectChatAiInteraction.AiStudio;
using DirectChatAiInteraction.Vertex;
using FfmpegUtilities;
using GoogleGenAi;
using Google.GenAI;
using AutoExtraction;
using Config;
using Infrastructure;

/// <summary>
/// [AI Context] Main application entry point. Orchestrates the execution flow by delegating
/// either to the FFmpeg processing toolkit or the Gemini chat session based on user input.
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird.
/// </summary>
class Program {
  static async Task Main(string[] args) {
    try {
      // [AI Context] Bootstrapper. Demonstrates manual Dependency Injection (DI) pattern.
      // Determines the runtime environment (Vertex vs AI Studio) and wires up the respective isolated dependencies.
      while (true) {
        Console.WriteLine("\n==================================================");
        Console.WriteLine("     Welcome to AI Extraction & Processing        ");
        Console.WriteLine("==================================================");
        Console.WriteLine("Please choose your desired operational mode:");
        Console.WriteLine(" 1) Google AI Studio (API Key / Developer endpoints)");
        Console.WriteLine(" 2) Google Cloud Vertex AI (Enterprise / 'vertex-ai-experiments')");
        Console.WriteLine(" 3) FFmpeg Interactive Manager (Local Audio/Video Processing)");
        Console.WriteLine("--------------------------------------------------");
        Console.WriteLine(" 4) Automated Content Retrieval & Processing (Future Enhancement)");
        Console.WriteLine(" 5) LaTeX Refinement & Post-Processing (Dedicated API Key)");
        Console.Write("\nChoice (1-5) or 'exit': ");

        string? mainChoice = Console.ReadLine()?.Trim().ToLower();

        // [AI Context] Handle null (EOF) as an exit signal to prevent infinite loops in non-interactive terminals.
        if (mainChoice == null || mainChoice == "exit" || mainChoice == "quit") {
          break;
        }

        if (mainChoice == "4") {
          Console.WriteLine("\nWelche API soll für die automatisierte Extraktion genutzt werden?");
          Console.WriteLine(" 1) Google AI Studio");
          Console.WriteLine(" 2) Google Cloud Vertex AI");
          Console.Write("Wahl (1-2) oder 'exit': ");
          string? extChoice = Console.ReadLine()?.Trim().ToLower();

          if (extChoice == "exit" || extChoice == "quit") continue;

          if (extChoice == "2") {
            var config = ConfigLoader<VertexAutoExtractionConfig>.Load();
            Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
            var attachmentHandler = new AttachmentHandler(client, config.SourceFolder, new[] { config.SourceFolder }, false, config.GcsBucketName);
            var loggerConfig = new SessionLoggerConfig { LogFolderPath = config.LogFolder };
            var sessionLogger = new SessionLogger(loggerConfig);
            var session = new VertexAutoExtractionSession(client, config, attachmentHandler, sessionLogger);
            await session.StartAsync();
          }
          else {
            var config = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
            string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-automated-content-extraction") ?? "no-key";
            Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
            var attachmentHandler = new AttachmentHandler(client, config.SourceFolder, new[] { config.SourceFolder }, true, "");
            var loggerConfig = new SessionLoggerConfig { LogFolderPath = config.LogFolder };
            var sessionLogger = new SessionLogger(loggerConfig);
            var session = new AiStudioAutoExtractionSession(client, config, attachmentHandler, sessionLogger);
            await session.StartAsync();
          }
          continue;
        }

        if (mainChoice == "5") {
          // Lade den exklusiven Key für das Refinement
          string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName("API_KEY-latex-refinement") ?? "no-key";
          Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
          var config = ConfigLoader<LatexRefinementConfig>.Load();
          var session = new LatexRefinementSession(client, config);
          await session.StartAsync();
          continue;
        }

        if (mainChoice == "3") {
          var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
          var ffmpegMenu = new FfmpegInteractiveSession(ffmpegConfig);
          await ffmpegMenu.StartAsync();
          continue;
        }

        bool isVertex = mainChoice == "2";

        if (isVertex) {
          // Wire up dependencies for Vertex AI
          var config = ConfigLoader<VertexAiConfig>.Load();
          Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
          var attachmentHandler = new AttachmentHandler(client, config.UploadFolder, config.IncludePaths, false, config.GcsBucketName);
          var loggerConfig = new SessionLoggerConfig { LogFolderPath = config.LogFolder };
          var sessionLogger = new SessionLogger(loggerConfig);

          var chatSession = new VertexAiChatSession(client, config, sessionLogger, attachmentHandler);
          await chatSession.StartAsync();
        }
        else if (mainChoice == "1") {
          // Wire up dependencies for AI Studio
          var config = ConfigLoader<AiStudioChatSessionConfig>.Load();
          string apiKey = GoogleAiClientBuilder.ResolveApiKey(config.ActiveApiProfile) ?? "no-key";
          Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
          var attachmentHandler = new AttachmentHandler(client, config.UploadFolder, config.IncludePaths, true, config.GcsBucketName);
          var loggerConfig = new SessionLoggerConfig { LogFolderPath = config.LogFolder };
          var sessionLogger = new SessionLogger(loggerConfig);

          var chatSession = new AiStudioChatSession(client, config, sessionLogger, attachmentHandler, isAiStudio: true);
          await chatSession.StartAsync();
        }
        else {
          Console.WriteLine("Invalid choice.");
        }
      }
    }
    catch (OperationCanceledException) {
      Console.WriteLine("\n[System] Execution cancelled by user. Exiting cleanly.");
    }
    catch (Exception ex) {
      Console.WriteLine($"\n[FATAL ERROR] The application encountered an unhandled exception and must close.");
      Console.WriteLine($"Type: {ex.GetType().Name}");
      Console.WriteLine($"Message: {ex.Message}");
      Console.WriteLine($"Stack Trace:\n{ex.StackTrace}");

      // Keep the console open so the user can actually read the fatal error
      Console.WriteLine("\nPress any key to exit...");
      if (!Console.IsInputRedirected) Console.ReadKey(true);
    }
    finally {
      Console.WriteLine("\n[System] Session ended.");
    }
  }
}
