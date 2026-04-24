﻿using System;
using System.Threading.Tasks;
using FfmpegUtilities;

class Program
{
  // === Zentrale Pfad-Konfiguration ===

  // Ordner für den FFmpeg-Prozessor (Videodateien -> MP3)
  public static string FfmpegSourceFolder = @"D:\lecture-videos\d-und-a/";
  //public static string FfmpegSourceFolder = @"D:\ffmpeg-tool\source";
  public static string FfmpegTargetFolder = @"D:\lecture-videos\d-und-a/new";
  //public static string FfmpegTargetFolder = @"D:\ffmpeg-tool\target";

  // Konfigurationsobjekt für die Chat-Session
  public static readonly ChatConfig SessionConfig = new ChatConfig
  {
    UseVertexAI = false, // Setze auf 'true' für Google Cloud Vertex AI, 'false' für Google AI Studio (Free-Tier API Keys)
    UploadFolder = @"D:\gemin-upload-folder",
    HistoryFolder = @"D:\gemini-chat-history",
    LogFolder = @"D:\gemini-logs",
    GcsBucketName = "biran-linalg-source-material",
    IncludePaths = new[] {
      @"c:\Users\miche\programming\lec-extraction-prog\gemini.md"
    },
    AI = new AIConfig
    {
      // Temperature steuert die Zufälligkeit bzw. Kreativität der Antworten (Range: 0.0 - 2.0 für Gemini).
      // - 0.0: Maximal deterministisch und präzise. Die KI wählt immer die wahrscheinlichsten Wörter. Ideal für strikten Code, Mathe und Transkripte.
      // - 0.7 bis 1.0: Ausgewogen. Standard-Wert für natürliche Chats, Zusammenfassungen und normale Textgenerierung.
      // - 1.5 bis 2.0: Sehr kreativ bis chaotisch. Erlaubt unerwartete Wortkombinationen, erhöht aber das Risiko von Halluzinationen.
      Temperature = 0.1f,

      // TopP (Nucleus Sampling) steuert die Auswahl dynamisch basierend auf der kumulativen Wahrscheinlichkeit (Range: 0.0 - 1.0).
      // Die KI wählt die kleinste Menge an Wörtern aus, deren summierte Wahrscheinlichkeit P erreicht.
      // - TopP = 0.95 bis 1.0: Standardwert. Erlaubt eine große Vielfalt und Kreativität (gut für Chats).
      // - TopP = 0.8 bis 0.9: Schneidet den "long tail" der unwahrscheinlichen Wörter ab. Erhöht den Fokus und die Kohärenz für Fachtexte.
      // - TopP = 0.1 bis 0.7: Sehr restriktiv. Zwingt die KI, fast ausschließlich die absolut wahrscheinlichsten Standard-Wörter zu nutzen.
      // - TopP = 0.0: Theoretisches Minimum. Maximal deterministisch (quasi identisch mit TopK = 1). Erlaubt keinerlei Abweichungen.
      TopP = 0.9f,

      // TopK steuert das Vokabular der KI. Bei jedem Schritt werden nur die K wahrscheinlichsten nächsten Wörter in Betracht gezogen.
      // - TopK = 40: Standardwert für normale Chats und kreatives Schreiben.
      // - TopK = 10 bis 20: Guter Mittelweg für strukturierte, faktenbasierte Texte.
      // - TopK = 1 (Greedy Decoding): Wählt immer exakt das 1 wahrscheinlichste Wort. Maximal deterministisch und perfekt, um Halluzinationen in LaTeX zu verhindern.
      TopK = 10,              // Geändert auf 10: Der perfekte Sweetspot für strikten LaTeX-Code und natürlichen Text.

      // MaxOutputTokens setzt eine harte Obergrenze für die Länge der generierten Antwort.
      // WICHTIG: Dieser Wert ändert NICHT das Verhalten oder die Ausführlichkeit der KI. 
      // Wenn das Limit erreicht wird, bricht die Antwort einfach mitten im Satz ab (Truncation).
      // Um kurze Antworten zu erzwingen, nutze stattdessen Prompts (z.B. "Antworte in einem Satz").
      // - 65536 (~64k): Das Maximum für neuere Modelle (Gemini 2.5). Erlaubt gigantische LaTeX-Skripte am Stück!
      MaxOutputTokens = 65536
    }
  };

  static async Task Main(string[] args)
  {
    string selectedOption = SelectModel();

    if (selectedOption == "aistudio_ffmpeg")
    {
      var ffmpegMenu = new FfmpegInteractiveMenu(FfmpegSourceFolder, FfmpegTargetFolder);
      await ffmpegMenu.StartAsync();
    }
    else
    {
      var chatSession = new ChatSession(SessionConfig);
      await chatSession.StartAsync(selectedOption);
    }
  }

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

public class ChatConfig
{
  public bool UseVertexAI { get; set; } = false;
  public string UploadFolder { get; set; } = "";
  public string HistoryFolder { get; set; } = "";
  public string LogFolder { get; set; } = "";
  public string GcsBucketName { get; set; } = "";
  public string[] IncludePaths { get; set; } = Array.Empty<string>();
  public AIConfig AI { get; set; } = new AIConfig();
}

public class AIConfig
{
  public float Temperature { get; set; } = 0.0f;
  public float TopP { get; set; } = 0.95f;
  public int TopK { get; set; } = 40;
  public int MaxOutputTokens { get; set; } = 65536;
}
