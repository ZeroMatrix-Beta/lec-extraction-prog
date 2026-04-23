﻿﻿﻿using System;
using System.Threading.Tasks;

class Program
{
  // === Zentrale Pfad-Konfiguration ===
  
  // Ordner für den FFmpeg-Prozessor (Videodateien -> MP3)
  public static string FfmpegSourceFolder = @"";
  public static string FfmpegTargetFolder = @"";

  // Ordner für die Gemini-ChatSession (Uploads und History)
  public static string ChatUploadFolder = @"D:\lecture videos";
  public static string ChatHistoryFolder = @"";
  // Absoluter Pfad für den designierten Log-Ordner, in dem die folder-x Ordner erstellt werden
  public static string ChatLogFolder = @"D:\gemini-logs";

  static async Task Main(string[] args)
  {
    string selectedOption = SelectModel();

    if (selectedOption == "ffmpeg")
    {
      var ffmpegProcessor = new FfmpegProcessor(FfmpegSourceFolder, FfmpegTargetFolder);
      await ffmpegProcessor.StartInteractiveAsync();
    }
    else
    {
      var chatSession = new ChatSession(ChatUploadFolder, ChatHistoryFolder, ChatLogFolder);
      await chatSession.StartAsync(selectedOption);
    }
  }

  private static string SelectModel()
  {
    Console.WriteLine("=== Start-Konfiguration ===");
    Console.WriteLine("Wähle ein Modell:");
    Console.WriteLine("1) gemini-2.5-flash     (Schnell, sehr effizient, 1M+ Tokens)");
    Console.WriteLine("2) gemini-2.5-flash-lite (Leichtgewicht)");
    Console.WriteLine("3) gemma-3-27b-it       (Open Model, 27B Parameter)");
    Console.WriteLine("4) gemini-1.5-flash     (Schnelles Fallback für Video/Audio, 1M+ Tokens)");
    Console.WriteLine("5) gemini-robotics-er-1.5-preview (Free Tier, Multimodal)");
    Console.WriteLine("6) gemini-robotics-er-1.6-preview (Neues Robotics Modell)");
    Console.WriteLine("7) gemini-2.5-pro       (Neuestes Pro Modell)");
    Console.WriteLine("8) --- FFmpeg Manager (Lokale Video/Audio-Verarbeitung) ---");
    Console.Write("Auswahl (1-8) [Standard: 1]: ");

    string? choice = Console.ReadLine()?.Trim();
    return choice switch
    {
      "2" => "gemini-2.5-flash-lite",
      "3" => "gemma-3-27b-it",
      "4" => "gemini-1.5-flash",
      "5" => "gemini-robotics-er-1.5-preview",
      "6" => "gemini-robotics-er-1.6-preview",
      "7" => "gemini-2.5-pro",
      "8" => "ffmpeg",
      _ => "gemini-2.5-flash"
    };
  }
}
