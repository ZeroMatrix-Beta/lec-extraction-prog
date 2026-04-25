﻿using System;
using System.Threading.Tasks;
using AiInteraction;
using Config;
using FfmpegUtilities;
using GoogleGenAi;
using Google.GenAI;

/// <summary>
/// [AI Context] Main application entry point. Orchestrates the execution flow by delegating
/// either to the FFmpeg processing toolkit or the Gemini chat session based on user input.
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird.
/// </summary>
class Program
{
  static async Task Main(string[] args)
  {
    IUserInterface ui = new ConsoleUserInterface();

    ui.WriteLine("==================================================");
    ui.WriteLine("     Welcome to AI Extraction & Processing        ");
    ui.WriteLine("==================================================");
    ui.WriteLine("Please choose your desired operational mode:");
    ui.WriteLine(" 1) Google AI Studio (API Key / Developer endpoints)");
    ui.WriteLine(" 2) Google Cloud Vertex AI (Enterprise / 'vertex-ai-experiments')");
    ui.WriteLine(" 3) FFmpeg Interactive Manager (Local Audio/Video Processing)");
    ui.Write("\nChoice (1-3): ");

    string? mainChoice = ui.ReadLine()?.Trim();

    if (mainChoice == "3")
    {
      var ffmpegMenu = new FfmpegInteractiveMenu(AppConfig.FfmpegSourceFolder, AppConfig.FfmpegTargetFolder);
      await ffmpegMenu.StartAsync();
      return;
    }

    bool isVertex = mainChoice == "2";
    string selectedModel = SelectModel(ui, isVertex);

    if (isVertex)
    {
      // Wire up dependencies for Vertex AI
      Client client = GoogleAiClientBuilder.BuildVertexClient(AppConfig.VertexSession.ProjectId, AppConfig.VertexSession.Location);
      var attachmentHandler = new AttachmentHandler(client, AppConfig.VertexSession.UploadFolder, AppConfig.VertexSession.IncludePaths, isAiStudio: false, AppConfig.VertexSession.GcsBucketName, ui);
      var sessionLogger = new SessionLogger(AppConfig.VertexSession.LogFolder);

      var chatSession = new VertexAiChatSession(client, AppConfig.VertexSession, ui, sessionLogger, attachmentHandler);
      await chatSession.StartAsync(selectedModel);
    }
    else
    {
      // Wire up dependencies for AI Studio
      string apiKey = GoogleAiClientBuilder.ResolveApiKey(AppConfig.Session.ActiveApiProfile) ?? "no-key";
      Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
      var attachmentHandler = new AttachmentHandler(client, AppConfig.Session.UploadFolder, AppConfig.Session.IncludePaths, isAiStudio: true, AppConfig.Session.GcsBucketName, ui);
      var sessionLogger = new SessionLogger(AppConfig.Session.LogFolder);

      var chatSession = new AiChatSession(client, AppConfig.Session, ui, sessionLogger, attachmentHandler, isAiStudio: true);
      await chatSession.StartAsync(selectedModel);
    }
  }

  /// <summary>
  /// [AI Context] Interactive console menu for initial model selection. Returns the specific model ID string.
  /// [Human] Das Startmenü in der Konsole. Wenn du neue Modelle hinzufügst, musst du sie exakt hier eintragen.
  /// </summary>
  private static string SelectModel(IUserInterface ui, bool isVertex)
  {
    ui.WriteLine($"\n=== Model Selection ({(isVertex ? "Vertex AI" : "AI Studio")}) ===");
    ui.WriteLine("Wähle ein Modell:");
    ui.WriteLine(" 1) gemini-3.1-flash-lite-preview || Input:  $0.25 (text / image / video), $0.50 (audio)");
    ui.WriteLine("                                  || Output: $1.50 (<== Claimed to be the most cost-efficient, optimized)");
    ui.WriteLine(" 2) gemini-3-flash-preview        || Input:  $0.50 (text / image / video), $1.00 (audio)");
    ui.WriteLine("                                  || Output: $3.0");
    ui.WriteLine(" 3) gemini-3.1-pro-preview        || Input:  $2.00, prompts <= 200k tokens, $4.00, prompts > 200k tokens");
    ui.WriteLine("                                  || Output: $12.00, prompts <= 200k tokens, $18.00, prompts > 200k");
    ui.WriteLine(" 4) gemini-2.5-flash              || Input:  $0.30  (text / image / video) $1.00 (audio). ");
    ui.WriteLine("                                  || Output: $2.50");
    ui.WriteLine(" 5) gemini-2.5-flash-lite         || Input:  $0.10  (text / image / video). ");
    ui.WriteLine("                                  || Output: $0.40");
    ui.WriteLine(" 6) gemini-2.5-pro                || Input:  $1.25, prompts <= 200k tokens, $2.50, prompts > 200k tokens.");
    ui.WriteLine("                                  || Output: $10.00, prompts <= 200k tokens $15.00, prompts > 200k");
    ui.WriteLine(" 7) gemini-2.0-flash              || Input:  $0.10  (text / image / video). $0.70 (audio) (shut down June 1, 2026)");
    ui.WriteLine("                                  || Output: $0.40");
    ui.WriteLine(" 8) gemini-2.0-flash-lite         || Input:  $0.075 (shut down June 1, 2026)");
    ui.WriteLine("                                  || Output: $0.30");
    ui.WriteLine(" 9) gemma-3-27b-it                || (Open Model, 27B Parameter)");
    ui.WriteLine("10) gemini-1.5-flash              || (Schnelles Fallback für Video/Audio)");
    ui.WriteLine("11) gemini-robotics-er-1.5-preview|| (Free Tier, Multimodal)");
    ui.WriteLine("12) gemini-robotics-er-1.6-preview|| (Neues Robotics Modell)");
    ui.Write("Auswahl (1-12) [Standard: 4]: ");

    // [Human] Liest die Eingabe. Wenn der Nutzer nur "Enter" drückt, greift der Fallback "_" ganz unten.
    string? choice = ui.ReadLine()?.Trim();
    return choice switch
    {
      "1" => "gemini-3.1-flash-lite-preview",
      "2" => "gemini-3-flash-preview",
      "3" => "gemini-3.1-pro-preview",
      "4" => "gemini-2.5-flash",
      "5" => "gemini-2.5-flash-lite",
      "6" => "gemini-2.5-pro",
      "7" => "gemini-2.0-flash",
      "8" => "gemini-2.0-flash-lite",
      "9" => "gemma-3-27b-it",
      "10" => "gemini-1.5-flash",
      "11" => "gemini-robotics-er-1.5-preview",
      "12" => "gemini-robotics-er-1.6-preview",
      _ => "gemini-2.5-flash"
    };
  }
}
