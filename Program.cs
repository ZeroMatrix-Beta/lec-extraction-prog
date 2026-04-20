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

        // 2. Den Client erstellen
        var client = new Client(apiKey: apiKey);

        // 3. Modellauswahl beim Start
        Console.WriteLine("=== Gemini Chat-Konfiguration ===");
        Console.WriteLine("Wähle ein Modell:");
        Console.WriteLine("1) gemini-2.5-pro       (Standard, sehr mächtig)");
        Console.WriteLine("2) gemini-2.5-flash     (Schnell, sehr effizient)");
        Console.WriteLine("3) gemini-2.5-flash-lite (Leichtgewicht)");
        Console.WriteLine("4) gemma-3-27b-it       (Open Model, 27B Parameter)");
        Console.WriteLine("5) gemini-1.5-pro       (Verlässliches Fallback für Video/Audio)");
        Console.WriteLine("6) gemini-1.5-flash     (Schnelles Fallback für Video/Audio)");
        Console.Write("Auswahl (1-6) [Standard: 1]: ");
        
        string? choice = Console.ReadLine();
        string selectedModel = choice switch
        {
            "2" => "gemini-2.5-flash",
            "3" => "gemini-2.5-flash-lite",
            "4" => "gemma-3-27b-it",
            "5" => "gemini-1.5-pro",
            "6" => "gemini-1.5-flash",
            _ => "gemini-2.5-pro"
        };

        // 4. Liste für die Chat-Historie erstellen
        var history = new List<Content>();

        Console.WriteLine($"\n--- Chat gestartet ({selectedModel}) ---");
        Console.WriteLine("Befehle:");
        Console.WriteLine("  exit / quit               -> Beendet den Chat");
        Console.WriteLine("  clear / reset             -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
        Console.WriteLine("  /attach datei1, datei2 | Frage -> Hängt Dateien an und stellt eine Frage dazu.");
        Console.WriteLine("                             (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
        Console.WriteLine("  /modelle                  -> Zeigt alle Modelle mit Audio-Support an");

        while (true)
        {
            Console.Write("\nDu: ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) continue;
            if (input.ToLower() == "exit" || input.ToLower() == "quit") break;

            if (input.ToLower() == "clear" || input.ToLower() == "reset")
            {
                history.Clear();
                Console.WriteLine("\n[INFO] Gedächtnis gelöscht! Gemini hat den bisherigen Kontext vergessen und startet frisch.");
                continue;
            }

            var parts = new List<Part>();
            string promptText = input;

            // 5a. Sonderbefehl: Modelle checken
            if (input.ToLower() == "/modelle" || input.ToLower() == "modelle")
            {
                Console.WriteLine("\n[API] Analysiere alle Modelle auf der Suche nach Audio-Support...");
                try
                {
                    using var httpClient = new HttpClient();
                    string json = await httpClient.GetStringAsync($"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}");
                    using var doc = JsonDocument.Parse(json);
                    
                    Console.WriteLine("\nFolgende Modelle unterstützen Audio-Eingaben:");
                    foreach (var element in doc.RootElement.GetProperty("models").EnumerateArray())
                    {
                        string name = element.GetProperty("name").GetString() ?? "";
                        
                        if (element.TryGetProperty("supportedInputModalities", out var modalities))
                        {
                            var mods = modalities.EnumerateArray().Select(m => m.GetString()).ToList();
                            if (mods.Contains("AUDIO"))
                            {
                                Console.WriteLine($"🎵 {name.Replace("models/", "")}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Fehler] Konnte Modelle nicht abrufen: {ex.Message}");
                }
                continue;
            }

            // 5b. Datei-Anhang mit kombiniertem Prompt (Z.B.: /attach file1.txt, file2.jpg | Erkläre das Bild)
            if (input.StartsWith("/attach ", StringComparison.OrdinalIgnoreCase))
            {
                string payload = input.Substring(8).Trim();
                string[] payloadParts = payload.Split('|', 2);
                string filesPart = payloadParts[0];
                promptText = payloadParts.Length > 1 ? payloadParts[1].Trim() : "";

                string[] filePaths = filesPart.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                bool filesLoaded = false;
                var loadedNames = new List<string>();

                foreach (var path in filePaths)
                {
                    // Anführungszeichen entfernen, falls die Dateien per Drag & Drop eingefügt wurden
                    string filePath = path.Trim().Trim('"', '\''); 
                    
                    if (System.IO.File.Exists(filePath))
                    {
                        string ext = Path.GetExtension(filePath).ToLower();
                        
                        // Text-Dateien sauber als Text-Part einlesen
                        if (new[] { ".md", ".txt", ".cs", ".json", ".xml", ".html", ".py", ".js", ".ts", ".css", ".tex" }.Contains(ext))
                        {
                            string fileContent = await System.IO.File.ReadAllTextAsync(filePath);
                            parts.Add(new Part { Text = $"--- DOKUMENT START ({Path.GetFileName(filePath)}) ---\n{fileContent}\n--- DOKUMENT ENDE ---" });
                        }
                        // Bilder/andere Dateien als Binär-Blob anhängen
                        else
                        {
                            byte[] fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
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

                            parts.Add(new Part { InlineData = new Blob { Data = fileBytes, MimeType = mimeType } });
                        }
                        
                        loadedNames.Add(Path.GetFileName(filePath));
                        filesLoaded = true;
                    }
                    else
                    {
                        Console.WriteLine($"[Fehler] Die Datei '{filePath}' wurde nicht gefunden und übersprungen.");
                    }
                }

                // Wenn Dateien geladen wurden, aber keine Frage per '|' definiert wurde
                if (filesLoaded && string.IsNullOrWhiteSpace(promptText))
                {
                    Console.Write($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]\nWas ist deine Frage dazu? ");
                    promptText = Console.ReadLine() ?? "";
                }
                else if (!filesLoaded)
                {
                    continue; // Keine Datei war erfolgreich, starte Schleife neu
                }
                else 
                {
                    Console.WriteLine($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}]");
                }
            }

            // 6. Text-Prompt anhängen und an die Historie übergeben
            if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
            else if (parts.Count == 0) continue;

            history.Add(new Content { Role = "user", Parts = parts });

            try
            {
                // Die dynamische Modellauswahl nutzen!
                var response = await client.Models.GenerateContentAsync(model: selectedModel, contents: history);
                string responseText = response.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "(Keine Antwort)";
                
                Console.WriteLine($"\nGemini: {responseText}");
                
                // 7. KI-Antwort in die Historie aufnehmen
                history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = responseText } } });
                
                // Optional: Verlauf in einer Log-Datei mitprotokollieren
                string logInput = input.StartsWith("/attach") ? $"[Dateien] {promptText}" : input;
                await System.IO.File.AppendAllTextAsync("chat_log.md", $"\n**Du:** {logInput}\n\n**Gemini:** {responseText}\n---\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nHoppla, da gab es einen Fehler: {ex.Message}");
                // Letzte User-Nachricht entfernen, damit der Chat nicht im fehlerhaften Zustand stecken bleibt
                history.RemoveAt(history.Count - 1);
            }
        }
    }
}
