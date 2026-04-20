using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        // 1. API Key sicher aus den Umgebungsvariablen laden
        string? apiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        
        // Fallback für Windows: Prüfen, ob die Variable auf Benutzerebene existiert 
        // (hilft, wenn das Terminal/die IDE nach dem Setzen der Variable noch nicht neu gestartet wurde)
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY", System.EnvironmentVariableTarget.User);
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Fehler: Die Umgebungsvariable GEMINI_API_KEY wurde nicht gefunden.");
            return;
        }

        // 2. Den Client erstellen
        var client = new Client(apiKey: apiKey);

        // 3. Liste für die Chat-Historie erstellen
        var history = new List<Content>();

        Console.WriteLine("\n--- Chat gestartet (gemini-2.5-pro) ---");
        Console.WriteLine("Tipp: Schreibe 'exit', um den Chat zu beenden.");
        Console.WriteLine("Tipp: Schreibe 'SENDE <Dateipfad>', um Dateien anzuhängen (mehrere trennen mit ';' oder ',').");
        Console.WriteLine("Tipp: Schreibe 'modelle', um zu prüfen, welche Modelle Audio unterstützen.");
        Console.WriteLine("Tipp: Schreibe 'clear', um das Gedächtnis (den Kontext) der KI zu löschen.");

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

            // 4a. Sonderbefehl: Modelle checken
            if (input.ToLower() == "modelle")
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
                    Console.WriteLine("\n(Tipp: Um Audio zu senden, musst du 'gemini-2.5-pro' unten im Code durch eines dieser Modelle ersetzen!)\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Fehler] Konnte Modelle nicht abrufen: {ex.Message}");
                }
                continue;
            }

            // 4. Prüfen, ob eine Datei gesendet werden soll (Escape-Befehl)
            if (input.StartsWith("SENDE ", StringComparison.OrdinalIgnoreCase))
            {
                string[] filePaths = input.Substring(6).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                bool filesLoaded = false;
                var loadedNames = new List<string>();

                foreach (var path in filePaths)
                {
                    string filePath = path.Trim();
                    if (System.IO.File.Exists(filePath))
                    {
                        string ext = Path.GetExtension(filePath).ToLower();
                        
                        // Text-Dateien sauber als Text-Part einlesen
                        if (ext == ".md" || ext == ".txt" || ext == ".cs" || ext == ".json" || ext == ".xml")
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

                if (filesLoaded)
                {
                    Console.Write($"[{loadedNames.Count} Datei(en) angehängt: {string.Join(", ", loadedNames)}] Was ist deine Frage dazu? ");
                    promptText = Console.ReadLine() ?? "";
                }
                else
                {
                    continue; // Keine Datei war erfolgreich, starte Schleife neu
                }
            }

            // 5. Text-Prompt anhängen und an die Historie übergeben
            if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
            else if (parts.Count == 0) continue;

            history.Add(new Content { Role = "user", Parts = parts });

            try
            {
                var response = await client.Models.GenerateContentAsync(model: "gemini-2.5-pro", contents: history);
                string responseText = response.Candidates?[0]?.Content?.Parts?[0]?.Text ?? "(Keine Antwort)";
                
                Console.WriteLine($"\nGemini: {responseText}");
                
                // 6. KI-Antwort in die Historie aufnehmen
                history.Add(new Content { Role = "model", Parts = new List<Part> { new Part { Text = responseText } } });
                
                // Optional: Verlauf in einer Log-Datei mitprotokollieren
                await System.IO.File.AppendAllTextAsync("chat_log.md", $"\n**Du:** {(input.StartsWith("SENDE") ? $"[Datei: {input.Substring(6).Trim()}] {promptText}" : input)}\n\n**Gemini:** {responseText}\n---\n");
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
