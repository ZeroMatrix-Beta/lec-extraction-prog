using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using System.Text.Json;
using System.Linq;
using System.Net.Http;


class Program
{
    static async Task Main(string[] args)
    {
        // 1. API Key sicher aus den Umgebungsvariablen laden
        string? apiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
                      ?? System.Environment.GetEnvironmentVariable("GEMINI_API_KEY", EnvironmentVariableTarget.User);

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Fehler: Die Umgebungsvariable GEMINI_API_KEY wurde nicht gefunden.");
            return;
        }

        // 2. Den Client mit einem benutzerdefinierten Timeout erstellen
        // Der Standard-Timeout (100s) ist für große Datei-Uploads/Verarbeitung zu kurz.
        var options = new HttpOptions
        {
            // Timeout auf 20 Minuten erhöhen, um die Verarbeitung großer Dateien zu ermöglichen
            Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
        };
        var client = new Client(apiKey: apiKey, httpOptions: options);

        // 3. Zeigt das interaktive Menü zur Modellauswahl an und gibt die API-ID des gewählten Modells zurück.
        string selectedModel = SelectModel();

        // 4. Liste für die Chat-Historie erstellen
        var history = new List<Content>();

        Console.WriteLine($"\n--- Chat gestartet ({selectedModel}) ---");
        Console.WriteLine("Befehle:");
        Console.WriteLine("  exit / quit               -> Beendet den Chat");
        Console.WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
        Console.WriteLine("  /attach datei1, datei2 | Frage -> Hängt Dateien an und stellt eine Frage dazu.");
        Console.WriteLine("                             (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
        Console.WriteLine("  /modelle                  -> Zeigt alle Modelle mit Audio-Support an");

        // 4. Lädt die System-Regeln (Config) und die LaTeX-Beispiele (History) asynchron vor
        GenerateContentConfig? config = await LoadSystemConfigAsync();
        await PreloadHistoryAsync(history);

        // Den Startzustand (inkl. Beispiele) merken, um ihn bei 'clear' wiederherzustellen
        var initialHistory = new List<Content>(history);

        // Hauptschleife des Chats: Liest kontinuierlich Benutzereingaben, verarbeitet Befehle,
        // sendet Nachrichten an die Gemini-API und gibt die gestreamten Antworten in der Konsole aus.
        while (true)
        {
            Console.Write("\nDu: ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

            if (input.ToLower() == "clear" || input.ToLower() == "reset")
            {
                history.Clear();
                history.AddRange(initialHistory); // Startzustand wiederherstellen
                Console.WriteLine("\n[INFO] Gedächtnis gelöscht! Gemini startet frisch, behält aber deine LaTeX-Beispiele im Kopf.");
                continue;
            }

            var parts = new List<Part>();
            string promptText = input;

            // 5a. Sonderbefehl: Ruft die Google API ab, um alle aktuell verfügbaren Modelle aufzulisten.
            if (input.ToLower() == "/modelle" || input.ToLower() == "modelle")
            {
                await ShowAvailableModelsAsync(apiKey);
                continue;
            }

            // 5b. Datei-Anhang mit kombiniertem Prompt (Z.B.: /attach file1.txt, file2.jpg | Erkläre das Bild)
            if (input.StartsWith("/attach ", StringComparison.OrdinalIgnoreCase))
            {
                var (success, parsedPrompt) = await HandleAttachmentsAsync(input, client, parts);
                if (!success) continue;
                promptText = parsedPrompt;
            }

            // 6. Text-Prompt anhängen und an die Historie übergeben
            if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
            else if (parts.Count == 0) continue;

            history.Add(new Content { Role = "user", Parts = parts });

            try
            {
                Console.Write($"\nGemini: ");
                string fullResponse = "";

                // Streaming aktivieren und config mit System-Prompt übergeben
                var responseStream = client.Models.GenerateContentStreamAsync(model: selectedModel, contents: history, config: config);
                await foreach (var chunk in responseStream)
                {
                    string chunkText = chunk.Text ?? chunk.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "";
                    Console.Write(chunkText);
                    fullResponse += chunkText;
                }
                Console.WriteLine();
                
                // 7. KI-Antwort in die Historie aufnehmen
                history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = fullResponse } } });
                
                // Optional: Verlauf in einer Log-Datei mitprotokollieren
                string logInput = input.StartsWith("/attach") ? $"[Dateien] {promptText}" : input;
                await System.IO.File.AppendAllTextAsync("chat_log.md", $"\n**Du:** {logInput}\n\n**Gemini:** {fullResponse}\n---\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nHoppla, da gab es einen Fehler: {ex.Message}");
                // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
                history.RemoveAt(history.Count - 1);
            }
        }
    }

    // --- Ausgelagerte Methoden ---

    /// <summary>
    /// Präsentiert dem Nutzer ein aufgeräumtes Konsolen-Menü zur Auswahl des gewünschten Gemini-Modells.
    /// Fallback ist standardmäßig das schnelle und effiziente 'gemini-2.5-flash'.
    /// </summary>
    static string SelectModel()
    {
        Console.WriteLine("=== Gemini Chat-Konfiguration ===");
        Console.WriteLine("Wähle ein Modell:");
        Console.WriteLine("1) gemini-2.5-flash     (Schnell, sehr effizient, 1M+ Tokens)");
        Console.WriteLine("2) gemini-2.5-flash-lite (Leichtgewicht)");
        Console.WriteLine("3) gemma-3-27b-it       (Open Model, 27B Parameter)");
        Console.WriteLine("4) gemini-1.5-flash     (Schnelles Fallback für Video/Audio, 1M+ Tokens)");
        Console.WriteLine("5) gemini-robotics-er-1.5-preview (Free Tier, Multimodal)");
        Console.WriteLine("6) gemini-robotics-er-1.6-preview (Neues Robotics Modell)");
        Console.Write("Auswahl (1-6) [Standard: 1]: ");
        
        string? choice = Console.ReadLine();
        return choice switch
        {
            "2" => "gemini-2.5-flash-lite",
            "3" => "gemma-3-27b-it",
            "4" => "gemini-1.5-flash",
            "5" => "gemini-robotics-er-1.5-preview",
            "6" => "gemini-robotics-er-1.6-preview",
            _ => "gemini-2.5-flash"
        };
    }

    /// <summary>
    /// Verbindet sich direkt mit der Google REST-API, um dynamisch eine Liste aller unterstützten Modelle 
    /// abzurufen und formatiert in der Konsole darzustellen (inklusive Token-Limits).
    /// </summary>
    /// <param name="apiKey">Der API-Schlüssel für die Authentifizierung.</param>
    static async Task ShowAvailableModelsAsync(string apiKey)
    {
        Console.WriteLine("\n[API] Rufe alle aktuell verfügbaren Modelle von Google ab...");
        try
        {
            using var httpClient = new HttpClient();
            string json = await httpClient.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
            using var doc = JsonDocument.Parse(json);
            
            Console.WriteLine("\nVerfügbare Gemini-Modelle und ihre unterstützten Eingabe-Formate:");
            foreach (var element in doc.RootElement.GetProperty("models").EnumerateArray())
            {
                string name = element.GetProperty("name").GetString()?.Replace("models/", "") ?? "";
                
                // Nur relevante Modelle anzeigen, um die Liste übersichtlich zu halten
                if (!name.Contains("gemini") && !name.Contains("gemma")) continue;

                if (element.TryGetProperty("supportedGenerationMethods", out var methods))
                {
                    var methodList = methods.EnumerateArray().Select(m => m.GetString()).ToList();
                    if (methodList.Contains("generateContent"))
                    {
                        string displayName = element.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                        int inputTokens = element.TryGetProperty("inputTokenLimit", out var limit) ? limit.GetInt32() : 0;
                        string tokenStr = inputTokens > 0 ? $"[{inputTokens:N0} Tokens]" : "";
                        Console.WriteLine($"🔹 {name,-30} {tokenStr,-16} -> {displayName}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fehler] Konnte Modelle nicht abrufen: {ex.Message}");
        }
    }

    /// <summary>
    /// Lädt die System-Anweisungen (z.B. gemini.md) asynchron von der Festplatte,
    /// um die grundlegenden Regeln und das Verhalten (Persona) der KI zu definieren.
    /// </summary>
    /// <returns>Die Konfiguration mit dem System-Prompt oder null, falls die Datei nicht existiert.</returns>
    static async Task<GenerateContentConfig?> LoadSystemConfigAsync()
    {
        if (System.IO.File.Exists("gemini.md"))
        {
            string sysPrompt = await System.IO.File.ReadAllTextAsync("gemini.md");
            Console.WriteLine("  [INFO] System-Prompt 'gemini.md' erfolgreich geladen! (LaTeX-Modus aktiv)");
            return new GenerateContentConfig
            {
                SystemInstruction = new Content { Role = "system", Parts = new List<Part> { new Part { Text = sysPrompt } } }
            };
        }
        return null;
    }

    /// <summary>
    /// Lädt vordefinierte Beispiel-Dateien (example1.tex, example2.tex) asynchron in den 
    /// Chat-Verlauf und simuliert eine Bestätigung der KI, um Token und API-Requests zu sparen.
    /// </summary>
    /// <param name="history">Die Liste mit dem aktuellen Chat-Verlauf, die befüllt wird.</param>
    static async Task PreloadHistoryAsync(List<Content> history)
    {
        if (System.IO.File.Exists("example1.tex") && System.IO.File.Exists("example2.tex"))
        {
            string ex1 = await System.IO.File.ReadAllTextAsync("example1.tex");
            string ex2 = await System.IO.File.ReadAllTextAsync("example2.tex");
            var exampleParts = new List<Part> {
                new Part { Text = $"Hier sind zwei Beispiele für das gewünschte LaTeX-Format:\n\n--- DOKUMENT START (example1.tex) ---\n{ex1}\n--- DOKUMENT ENDE ---" },
                new Part { Text = $"--- DOKUMENT START (example2.tex) ---\n{ex2}\n--- DOKUMENT ENDE ---" },
                new Part { Text = "Bitte lies diese sorgfältig, bevor ich dir weitere Anweisungen gebe." }
            };
            history.Add(new Content { Role = "user", Parts = exampleParts });
            history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = "Ich habe die Protocol-Beispiele verstanden und werde mich bei der Transkription strikt an diese Vorlagen und semantischen Umgebungen halten." } } });
            Console.WriteLine("  [INFO] Referenzdateien 'example1.tex' und 'example2.tex' automatisch in das Gedächtnis geladen!");
        }
    }

    /// <summary>
    /// Parst den /attach-Befehl, lädt lokale Textdateien in den Speicher und 
    /// streamt Medien-Dateien (Bilder, Videos, Audio) asynchron an die Google API.
    /// Wartet bei großen Medien-Dateien automatisch auf die serverseitige Verarbeitung.
    /// </summary>
    /// <param name="input">Die komplette Benutzereingabe inklusive /attach.</param>
    /// <param name="client">Der verbundene Gemini API-Client.</param>
    /// <param name="parts">Die Liste der Nachrichten-Teile, an die die Dateien angehängt werden.</param>
    /// <returns>Ein Tupel mit Erfolg-Status und dem extrahierten Text-Prompt.</returns>
    static async Task<(bool success, string promptText)> HandleAttachmentsAsync(string input, Client client, List<Part> parts)
    {
        string payload = input.Substring(8).Trim();
        string[] payloadParts = payload.Split('|', 2);
        string filesPart = payloadParts[0];
        string promptText = payloadParts.Length > 1 ? payloadParts[1].Trim() : "";

        string[] filePaths = filesPart.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        bool filesLoaded = false;
        var loadedNames = new List<string>();

        foreach (var path in filePaths)
        {
            string filePath = path.Trim().Trim('"', '\''); 
            
            if (System.IO.File.Exists(filePath))
            {
                string ext = Path.GetExtension(filePath).ToLower();
                
                if (new[] { ".md", ".txt", ".cs", ".json", ".xml", ".html", ".py", ".js", ".ts", ".css", ".tex" }.Contains(ext))
                {
                    string fileContent = await System.IO.File.ReadAllTextAsync(filePath);
                    parts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(filePath)}) ---\n{fileContent}\n--- DOKUMENT ENDE ---" });
                }
                else
                {
                    string? mimeType = ext switch {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        ".pdf" => "application/pdf",
                        ".mp3" => "audio/mpeg",
                        ".wav" => "audio/wav",
                        ".mp4" => "video/mp4",
                        _ => null
                    };
                    
                    if (mimeType == null)
                    {
                        Console.WriteLine($"[Fehler] Der Dateityp '{ext}' von '{Path.GetFileName(filePath)}' wird nicht unterstützt.");
                        continue;
                    }

                    Console.WriteLine($"  [API] Lade '{Path.GetFileName(filePath)}' hoch (dies kann je nach Dateigröße einen Moment dauern)...");
                    var uploadedFile = await client.Files.UploadAsync(filePath);

                    if (uploadedFile.State?.ToString().Equals("Processing", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Console.Write("  [API] Warte auf Verarbeitung durch Google");
                        while (uploadedFile.State?.ToString().Equals("Processing", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            Console.Write(".");
                            await Task.Delay(3000);
                            uploadedFile = await client.Files.GetAsync(uploadedFile.Name!);
                        }
                        Console.WriteLine();
                    }

                    if (uploadedFile.State?.ToString().Equals("Failed", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Console.WriteLine($"  [Fehler] Die Datei '{Path.GetFileName(filePath)}' konnte von Google nicht verarbeitet werden.");
                        continue;
                    }

                    parts.Add(new Part { FileData = new FileData { FileUri = uploadedFile.Uri, MimeType = mimeType } });
                }
                
                loadedNames.Add(Path.GetFileName(filePath));
                filesLoaded = true;
            }
            else
            {
                Console.WriteLine($"[Fehler] Die Datei '{filePath}' wurde nicht gefunden und übersprungen.");
            }
        }

        if (filesLoaded && string.IsNullOrWhiteSpace(promptText))
        {
            Console.WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
            Console.WriteLine("  [INFO] Kein Prompt angegeben. Sende automatischen Start-Befehl an Gemini...");
            promptText = "Hier ist das Material. Bitte starte mit der Transkription exakt nach den Regeln des System-Protocols.";
        }
        else if (!filesLoaded)
        {
            return (false, promptText);
        }
        else 
        {
            Console.WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
        }

        return (true, promptText);
    }
}
