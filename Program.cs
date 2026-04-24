﻿using System;
using System.Threading.Tasks;
using FfmpegUtilities;

/// <summary>
/// [AI Context] Main application entry point. Orchestrates the execution flow by delegating
/// either to the FFmpeg processing toolkit or the Gemini chat session based on user input.
/// [Human] Die Hauptklasse, die beim Start des Programms als erstes aufgerufen wird.
/// </summary>
class Program
{
  static async Task Main(string[] args)
  {
    // [Human] Start-Menü anzeigen und Benutzerauswahl abwarten.
    string selectedOption = SelectModel();

    // [AI Context] Routes execution. Side-effect: Blocks main thread until the respective subsystem completes.
    if (selectedOption == "aistudio_ffmpeg")
    {
      var ffmpegMenu = new FfmpegInteractiveMenu(AppConfig.FfmpegSourceFolder, AppConfig.FfmpegTargetFolder);
      await ffmpegMenu.StartAsync();
    }
    else
    {
      var chatSession = new ChatSession(AppConfig.Session);
      await chatSession.StartAsync(selectedOption);
    }
  }

  /// <summary>
  /// [AI Context] Interactive console menu for initial model selection. Returns the specific model ID string.
  /// [Human] Das Startmenü in der Konsole. Wenn du neue Modelle hinzufügst, musst du sie exakt hier eintragen.
  /// </summary>
  private static string SelectModel()
  {
    Console.WriteLine("=== Start-Konfiguration ===");
    Console.WriteLine("Wähle ein Modell:");
    Console.WriteLine(" 1) gemini-3.1-flash-lite-preview || Input:  $0.25 (text / image / video), $0.50 (audio)");
    Console.WriteLine("                                  || Output: $1.50 (<== Claimed to be the most cost-efficient, optimized)");
    Console.WriteLine(" 2) gemini-3-flash-preview        || Input:  $0.50 (text / image / video), $1.00 (audio)");
    Console.WriteLine("                                  || Output: $3.0");
    Console.WriteLine(" 3) gemini-3.1-pro-preview        || Input:  $2.00, prompts <= 200k tokens, $4.00, prompts > 200k tokens");
    Console.WriteLine("                                  || Output: $12.00, prompts <= 200k tokens, $18.00, prompts > 200k");
    Console.WriteLine(" 4) gemini-2.5-flash              || Input:  $0.30  (text / image / video) $1.00 (audio). ");
    Console.WriteLine("                                  || Output: $2.50");
    Console.WriteLine(" 5) gemini-2.5-flash-lite         || Input:  $0.10  (text / image / video). ");
    Console.WriteLine("                                  || Output: $0.40");
    Console.WriteLine(" 6) gemini-2.5-pro                || Input:  $1.25, prompts <= 200k tokens, $2.50, prompts > 200k tokens.");
    Console.WriteLine("                                  || Output: $10.00, prompts <= 200k tokens $15.00, prompts > 200k");
    Console.WriteLine(" 7) gemini-2.0-flash              || Input:  $0.10  (text / image / video). $0.70 (audio) (shut down June 1, 2026)");
    Console.WriteLine("                                  || Output: $0.40");
    Console.WriteLine(" 8) gemini-2.0-flash-lite         || Input:  $0.075 (shut down June 1, 2026)");
    Console.WriteLine("                                  || Output: $0.30");
    Console.WriteLine(" 9) gemma-3-27b-it                || (Open Model, 27B Parameter)");
    Console.WriteLine("10) gemini-1.5-flash              || (Schnelles Fallback für Video/Audio)");
    Console.WriteLine("11) gemini-robotics-er-1.5-preview|| (Free Tier, Multimodal)");
    Console.WriteLine("12) gemini-robotics-er-1.6-preview|| (Neues Robotics Modell)");
    Console.WriteLine("13) --- AI Studio FFmpeg Manager (Lokale Video/Audio-Verarbeitung) ---");
    Console.Write("Auswahl (1-13) [Standard: 4]: ");

    // [Human] Liest die Eingabe. Wenn der Nutzer nur "Enter" drückt, greift der Fallback "_" ganz unten.
    string? choice = Console.ReadLine()?.Trim();
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
      "13" => "aistudio_ffmpeg",
      _ => "gemini-2.5-flash"
    };
  }
}
