using System;

namespace Config;

/// <summary>
/// [AI Context] Centralized configuration dependency. Injected into sessions to ensure predictability and avoid hardcoded magic strings.
/// [Human] Zentrale Anlaufstelle für alle Pfade und Einstellungen. Wenn du Ordner auf deiner Festplatte verschiebst, musst du das nur HIER ändern!
/// Zentrale Konfigurationsklasse für die gesamte Anwendung.
/// Hier werden alle Pfade, API-Einstellungen und KI-Parameter zentral verwaltet.
/// </summary>
public static class AppConfig
{
  // ====================================================================
  // 1. FFmpeg Pfad-Konfiguration
  // ====================================================================

  /// <summary>Ordner, in dem nach Videodateien gesucht wird, die konvertiert werden sollen.</summary>
  public static readonly string FfmpegSourceFolder = @"D:\lecture-videos\d-und-a/";

  /// <summary>Zielordner für die verarbeiteten Videos und extrahierten MP3s.</summary>
  public static readonly string FfmpegTargetFolder = @"D:\lecture-videos\d-und-a/new";

  // ====================================================================
  // 2. Chat Session & API Konfiguration
  // ====================================================================

  // [AI Context] Holds the global state definition for DirectAIInteraction instantiation.
  public static readonly ChatConfig Session = new ChatConfig
  {
    // [Human] Wenn auf 'true' gesetzt, nutzt das Programm Google Cloud IAM (Vertex AI). 'false' nutzt den normalen Google AI Studio API-Key (Pay-as-you-go oder Free).
    // [AI Context] Toggles connection mode: true = Vertex AI (Enterprise endpoints), false = Google AI Studio (Developer API).
    UseVertexAI = false, // Setze auf 'true' für Google Cloud Vertex AI, 'false' für Google AI Studio

    ActiveApiProfile = 1, // Wählt den API-Key: 1 = Projekt 1, 2 = Projekt 2, 3 = Projekt 3
    UploadFolder = @"D:\gemin-upload-folder",
    HistoryFolder = @"D:\gemini-chat-history",
    LogFolder = @"D:\gemini-logs",
    GcsBucketName = "biran-linalg-source-material",

    // Ordner und Dateien, die der Chat-Bot zusätzlich zum UploadFolder und Arbeitsverzeichnis durchsuchen soll.
    IncludePaths = new[] {
      @"c:\Users\miche\programming\lec-extraction-prog\gemini.md",
      FfmpegSourceFolder,
      FfmpegTargetFolder
    },

    // ====================================================================
    // 3. KI (Modell) Parameter Tuning
    // ====================================================================
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
      TopK = 10, // Der perfekte Sweetspot für strikten LaTeX-Code und natürlichen Text.

      // MaxOutputTokens setzt eine harte Obergrenze für die Länge der generierten Antwort.
      // WICHTIG: Dieser Wert ändert NICHT das Verhalten oder die Ausführlichkeit der KI. 
      // Wenn das Limit erreicht wird, bricht die Antwort einfach mitten im Satz ab (Truncation).
      // - 65536 (~64k): Das Maximum für neuere Modelle (Gemini 2.5). Erlaubt gigantische LaTeX-Skripte am Stück!
      MaxOutputTokens = 65536,

      // [AI Context] "Thinking" params introduced for the latest Gemini 2.5 and 3.x models.
      // - ThinkingBudget: Integer (z.B. 1024, 4096) - Wird strikt nur von der Gemini 2.5 Serie verlangt.
      ThinkingBudget = 1024,

      // - ThinkingLevel: String (MINIMAL, LOW, MEDIUM, HIGH) - Wird strikt nur von der Gemini 3.x Serie verlangt.
      // [Human] Kontrolliert, wie lange das 3.1 Modell intern "nachdenkt", bevor es antwortet.
      ThinkingLevel = "LOW"
    }
  };
}

// ====================================================================
// Konfigurations-Datenklassen
// ====================================================================

/// <summary>
/// [AI Context] DTO for Session configuration properties.
/// </summary>
public class ChatConfig
{
  public bool UseVertexAI { get; set; } = false;
  public int ActiveApiProfile { get; set; } = 1;
  public string UploadFolder { get; set; } = "";
  public string HistoryFolder { get; set; } = "";
  public string LogFolder { get; set; } = "";
  public string GcsBucketName { get; set; } = "";
  public string[] IncludePaths { get; set; } = Array.Empty<string>();
  public AIConfig AI { get; set; } = new AIConfig();
}

/// <summary>
/// [AI Context] DTO for generation parameters. Directly impacts deterministic vs creative output distributions.
/// </summary>
public class AIConfig
{
  public float Temperature { get; set; } = 0.0f;
  public float TopP { get; set; } = 0.95f;
  public int TopK { get; set; } = 40;
  public int MaxOutputTokens { get; set; } = 65536;
  public int? ThinkingBudget { get; set; } = null;
  public string? ThinkingLevel { get; set; } = null;
}