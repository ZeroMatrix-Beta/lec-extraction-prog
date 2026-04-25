using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Google.GenAI;
using Google.GenAI.Types;
using Config;

namespace AiInteraction;

/// <summary>
/// [AI Context] Core REPL manager specifically for Google Cloud Vertex AI interactions.
/// This completely isolates the enterprise execution context from the developer AI Studio context.
/// </summary>
public class VertexAiChatSession
{
  private readonly string UploadFolderPath;
  private readonly string HistoryFolderPath;
  private string InitialHistoryPrompt = "Hier ist das Material aus meiner History. Bitte lies es sorgfältig und warte dann auf meine nächsten Anweisungen.";
  private readonly string GcsBucketName;
  private readonly string LogFolderPath;
  private readonly string SystemInstructionPath;
  private string? _systemInstructionText;
  private AIConfig AIParams;
  private readonly AttachmentHandler _attachmentHandler;
  private readonly SessionLogger _sessionLogger;
  private readonly IUserInterface _ui;
  private readonly Client _client;

  public VertexAiChatSession(Client client, VertexAiConfig config, IUserInterface ui, SessionLogger logger, AttachmentHandler attachmentHandler)
  {
    _client = client;
    _ui = ui;
    _sessionLogger = logger;
    _attachmentHandler = attachmentHandler;
    UploadFolderPath = config.UploadFolder;
    HistoryFolderPath = config.HistoryFolder;
    LogFolderPath = config.LogFolder;
    GcsBucketName = config.GcsBucketName;
    SystemInstructionPath = config.SystemInstructionPath;

    AIParams = new AIConfig
    {
      Temperature = config.AI.Temperature,
      TopP = config.AI.TopP,
      TopK = config.AI.TopK,
      MaxOutputTokens = config.AI.MaxOutputTokens
    };
  }

  public async Task StartAsync(string selectedModel)
  {
    _ui.WriteLine("\n[System] Initiating Vertex AI Enterprise Session...");

    // ALWAYS clean up the bucket completely before starting a session (crash recovery)
    await ForcePurgeGcsBucketAsync();

    _sessionLogger.InitializeSession();

    _ui.Write($"\n[Setup] System Instruction laden? Pfad: '{SystemInstructionPath}' (j/n): ");
    if (_ui.ReadLine()?.Trim().ToLower() == "j")
    {
      if (!string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath))
      {
        _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
        _ui.WriteLine($"  [INFO] System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
      }
      else
      {
        _ui.WriteLine($"  [WARNUNG] System-Prompt-Datei nicht gefunden: {SystemInstructionPath}");
      }
    }
    else
    {
      _ui.WriteLine("  [INFO] System Instruction wird ignoriert.");
    }

    string? initialInput = GetInitialHistoryCommand();

    await RunChatSessionAsync(selectedModel, initialInput);
  }

  private async Task RunChatSessionAsync(string selectedModel, string? initialInput)
  {
    var history = new List<Content>();
    var initialHistory = new List<Content>(history);

    _ui.WriteLine($"\n--- Vertex Chat gestartet ({selectedModel}) ---");
    _ui.WriteLine("Befehle:");
    _ui.WriteLine("  exit / quit               -> Beendet den Chat");
    _ui.WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
    _ui.WriteLine("  attach datei1, datei2 | Frage  -> Hängt Dateien an und stellt eine Frage dazu.");
    _ui.WriteLine("  set temp [wert]           -> Ändert die Temperatur dynamisch");
    _ui.WriteLine("  set tokens [wert]         -> Ändert das MaxOutputTokens-Limit dynamisch");

    while (true)
    {
      string? input;
      if (initialInput != null)
      {
        input = initialInput;
        _ui.WriteLine($"\nDu: {input}");
        initialInput = null;
      }
      else
      {
        _ui.Write("\nDu: ");
        input = _ui.ReadLine();
      }

      if (string.IsNullOrWhiteSpace(input)) continue;
      if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

      var parts = new List<Part>();
      string promptText = input;

      bool isCommandHandled = await TryHandleBuiltInCommandsAsync(input, history, initialHistory, parts, newPrompt => promptText = newPrompt);

      if (isCommandHandled)
      {
        if (!input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) continue;
        if (parts.Count == 0) continue;
      }

      if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
      else if (parts.Count == 0) continue;

      history.Add(new Content { Role = "user", Parts = parts });

      try
      {
        await StreamGeminiResponseAsync(selectedModel, history, input, promptText);
      }
      catch (Exception ex)
      {
        _ui.WriteLine($"\n[Vertex Error]: {ex.Message}");
        history.RemoveAt(history.Count - 1);
      }
    }

    _ui.WriteLine("\n[INFO] Chat beendet. Räume GCS Bucket komplett auf...");

    // ALWAYS clean up the bucket at the end of the session to save costs.
    await ForcePurgeGcsBucketAsync();
  }

  private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText)
  {
    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase) || input.Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
      history.Clear();
      history.AddRange(initialHistory);
      _ui.WriteLine("\n[INFO] Gedächtnis gelöscht! Vertex Modell startet frisch.");
      return true;
    }

    if (input.StartsWith("set temp ", StringComparison.OrdinalIgnoreCase))
    {
      string tempValueStr = input.Substring(9).Trim();
      if (float.TryParse(tempValueStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float newTemp) && newTemp >= 0.0f && newTemp <= 2.0f)
      {
        AIParams.Temperature = newTemp;
        _ui.WriteLine($"[INFO] Temperatur auf {AIParams.Temperature:F1} gesetzt.");
      }
      return true;
    }

    if (input.StartsWith("set tokens ", StringComparison.OrdinalIgnoreCase))
    {
      string tokenValueStr = input.Substring(11).Trim();
      if (int.TryParse(tokenValueStr, out int newTokens) && newTokens >= 1)
      {
        AIParams.MaxOutputTokens = newTokens;
        _ui.WriteLine($"[INFO] MaxOutputTokens auf {AIParams.MaxOutputTokens} gesetzt.");
      }
      return true;
    }

    if (input.StartsWith("attach ", StringComparison.OrdinalIgnoreCase))
    {
      var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(input);

      if (!success) return true;

      parts.AddRange(attachmentParts);
      updatePromptText(parsedPrompt);
      return true;
    }

    return false;
  }

  private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText)
  {
    _ui.Write($"\n[Vertex] {selectedModel}: ");
    string fullResponse = "";

    var config = new GenerateContentConfig
    {
      Temperature = AIParams.Temperature,
      TopP = AIParams.TopP,
      TopK = AIParams.TopK,
      MaxOutputTokens = AIParams.MaxOutputTokens
    };

    if (!string.IsNullOrWhiteSpace(_systemInstructionText))
    {
      config.SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = _systemInstructionText } } };
    }

    var responseStream = _client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
    await foreach (var chunk in responseStream)
    {
      string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
      _ui.Write(chunkText);
      fullResponse += chunkText;
    }
    _ui.WriteLine();

    history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
    await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse);
  }

  private string? GetInitialHistoryCommand()
  {
    _ui.Write($"\n[Setup] History laden? Ordner: '{HistoryFolderPath}' (j/n): ");
    bool loadHistory = _ui.ReadLine()?.Trim().ToLower() == "j";

    if (!loadHistory) return null;
    if (string.IsNullOrWhiteSpace(HistoryFolderPath) || !Directory.Exists(HistoryFolderPath)) return null;

    string[] historyFiles = Directory.GetFiles(HistoryFolderPath);
    if (historyFiles.Length == 0) return null;

    string fileList = string.Join(", ", historyFiles.Select(p => $"\"{p}\""));
    return $"attach {fileList} | {InitialHistoryPrompt}";
  }

  /// <summary>
  /// Deep cleans the assigned Vertex AI Bucket. Crucial for managing storage costs and cleaning up crashed sessions.
  /// </summary>
  private async Task ForcePurgeGcsBucketAsync()
  {
    if (string.IsNullOrWhiteSpace(GcsBucketName)) return;

    try
    {
      // StorageClient utilizes Application Default Credentials
      var storageClient = await StorageClient.CreateAsync();
      _ui.WriteLine($"  [GCS] Verifying Bucket '{GcsBucketName}' and purging ALL files...");

      var objects = storageClient.ListObjectsAsync(GcsBucketName);
      int count = 0;

      await foreach (var obj in objects)
      {
        await storageClient.DeleteObjectAsync(GcsBucketName, obj.Name);
        count++;
      }

      if (count > 0)
      {
        _ui.WriteLine($"  [GCS] Successfully deleted {count} file(s) to secure billing.");
      }
      else
      {
        _ui.WriteLine($"  [GCS] Bucket is already empty.");
      }
    }
    catch (Exception ex)
    {
      _ui.WriteLine($"  [GCS ERROR] Failed to access or purge bucket '{GcsBucketName}': {ex.Message}");
    }
  }
}