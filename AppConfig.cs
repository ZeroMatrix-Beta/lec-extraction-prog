using System;
using System.IO;

namespace Config;

/// <summary>
/// [AI Context] Centralized 'Single Point of Truth' for all hardcoded paths and default parameters.
/// [Human] Hier kannst du alle Festplatten-Pfade, Cloud-Buckets und Standard-Prompts an einem einzigen Ort ändern.
/// </summary>
public static class AppConfig
{
  // --- Basis-Verzeichnisse (Directories) ---
  public static readonly string BaseLectureFolder = @"D:\lecture-videos";
  public static readonly string UploadFolder = @"D:\gemini-upload-folder";
  public static readonly string LogFolder = @"D:\gemini-logs";
  public static readonly string[] HistoryPreloadPaths = new[] {
    @"C:\Users\miche\latex\directors-cut-analysis2\gemini-chat-history",
    @"D:\ETH HS 2025\Analysis I HS 2025\Analysis_I_Skript_I_25-12-22.pdf"
  };

  // --- Dynamisch zusammengesetzte Pfade ---
  public static readonly string AutoExtractionSourceFolder = Path.Combine(BaseLectureFolder, "analysis2");
  public static readonly string AutoExtractionTargetFolder = Path.Combine(BaseLectureFolder, @"analysis2\destination2");

  public static readonly string VertexAutoExtractionSourceFolder = Path.Combine(BaseLectureFolder, @"d-und-a\new");
  public static readonly string VertexAutoExtractionTargetFolder = Path.Combine(BaseLectureFolder, @"d-und-a\extracted");

  public static readonly string LatexRefinementSourceFolder = Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement");
  public static readonly string LatexRefinementTargetFolder = Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement\refined");

  public static readonly string FfmpegSourceFolder = Path.Combine(BaseLectureFolder, "d-und-a");
  public static readonly string FfmpegTargetFolder = Path.Combine(BaseLectureFolder, @"d-und-a\new");

  // --- Dateien (Files) ---
  public static readonly string SystemInstructionPath = @"C:\Users\miche\latex\directors-cut-analysis2\gemini.md";

  // --- Cloud & API ---
  public static readonly string VertexProjectId = "vertex-ai-experiments-494320";
  public static readonly string VertexLocation = "global";
  public static readonly string VertexGcsBucketName = "vertex-ai-experiments-upload-bucket-us";

  // --- Standard KI-Parameter ---
  public static readonly string DefaultModel = "gemini-3-flash-preview";
  public static readonly string RefinementModel = "gemini-2.5-pro";
  public static readonly float DefaultTemperature = 0.1f;
  public static readonly float DefaultTopP = 0.9f;
  public static readonly int DefaultTopK = 10;
  public static readonly int DefaultMaxOutputTokens = 65535;
  public static readonly string AutoExtractionPrompt = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.";
}