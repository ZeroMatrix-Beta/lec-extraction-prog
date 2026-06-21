using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Config;
using DirectChatAiInteraction;
using Infrastructure;
using Google.GenAI;

namespace AutoExtraction {
    public static class RefinementUiHelper {
        public static async Task StartInteractiveRefinementAsync(LatexRefinementSessionConfig refinementConfig, IAutoExtractionConfig extractionConfig) {
            Console.WriteLine("\n=== Interaktiver LaTeX Refinement Modus ===");

            // Hot-reload the config from disk so manual edits to the .json file are picked up immediately
            refinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();

            while (true) {
                string backendDisplay = refinementConfig.UseVertex ? "Vertex AI" : "AI Studio";
                string profileDisplay = refinementConfig.AiStudioActiveApiProfile == 0 ? "Dediziert (API_KEY-latex-refinement)" : $"Profil {refinementConfig.AiStudioActiveApiProfile}";
                
                string currentModel = refinementConfig.UseVertex 
                    ? refinementConfig.Step1MergeAndTimestamp.Vertex.Model 
                    : refinementConfig.Step1MergeAndTimestamp.AiStudio.Model;

                Console.WriteLine($"\n[Refinement Config]");
                Console.WriteLine($"Backend:    {backendDisplay}");
                if (!refinementConfig.UseVertex) {
                    Console.WriteLine($"API-Profil: {profileDisplay}");
                } else {
                    Console.WriteLine($"Project ID: {refinementConfig.VertexProjectId}");
                }
                Console.WriteLine($"Modell:     {currentModel}");
                Console.WriteLine("\nOptionen:");
                Console.WriteLine(" 1) Refinement fortsetzen (Dateien wählen)");
                Console.WriteLine($" 2) Backend wechseln (Aktuell: {backendDisplay})");
                Console.WriteLine(" 3) API Key Profil ändern (Nur für AI Studio)");
                Console.WriteLine(" 4) Modell ändern (Für aktuelles Backend)");
                Console.WriteLine(" 5) Abbrechen");
                Console.Write("Auswahl (1-5, Standard: 1): ");
                
                string menuChoice = Console.ReadLine()?.Trim() ?? "1";
                if (string.IsNullOrEmpty(menuChoice)) menuChoice = "1";

                if (menuChoice == "2") {
                    refinementConfig.UseVertex = !refinementConfig.UseVertex;
                    ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                    Console.WriteLine($"  [INFO] Backend gewechselt auf: {(refinementConfig.UseVertex ? "Vertex AI" : "AI Studio")}");
                    continue;
                } else if (menuChoice == "3") {
                    if (refinementConfig.UseVertex) {
                        Console.WriteLine("API Profile sind nur für AI Studio relevant.");
                        continue;
                    }
                    Console.Write("Neues API-Key Profil (0-3, 0 für Dediziert): ");
                    if (int.TryParse(Console.ReadLine(), out int newProfile) && newProfile >= 0 && newProfile <= 3) {
                        refinementConfig.AiStudioActiveApiProfile = newProfile;
                        ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                    } else {
                        Console.WriteLine("Ungültige Eingabe.");
                    }
                    continue;
                } else if (menuChoice == "4") {
                    Console.WriteLine("\nWähle ein Modell:");
                    Console.WriteLine(" 1) gemini-3.5-flash");
                    Console.WriteLine(" 2) gemini-3.1-flash-lite-preview");
                    Console.WriteLine(" 3) gemini-2.5-flash");
                    Console.WriteLine(" 4) gemini-2.5-pro");
                    Console.WriteLine(" 5) gemini-2.0-flash-exp");
                    Console.Write("Wahl (1-5): ");
                    string mChoice = Console.ReadLine()?.Trim() ?? "";
                    string newModel = mChoice switch {
                        "1" => "gemini-3.5-flash",
                        "2" => "gemini-3.1-flash-lite-preview",
                        "3" => "gemini-2.5-flash",
                        "4" => "gemini-2.5-pro",
                        "5" => "gemini-2.0-flash-exp",
                        _ => ""
                    };
                    if (!string.IsNullOrEmpty(newModel)) {
                        if (refinementConfig.UseVertex) {
                            refinementConfig.Step1MergeAndTimestamp.Vertex.Model = newModel;
                            refinementConfig.Step2SpeechRefinement.Vertex.Model = newModel;
                            refinementConfig.Step3LastRefinement.Vertex.Model = newModel;
                        } else {
                            refinementConfig.Step1MergeAndTimestamp.AiStudio.Model = newModel;
                            refinementConfig.Step2SpeechRefinement.AiStudio.Model = newModel;
                            refinementConfig.Step3LastRefinement.AiStudio.Model = newModel;
                        }
                        ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                    }
                    continue;
                } else if (menuChoice == "5") {
                    return;
                }

                break; // proceed with option 1
            }

            var uiConfig = ConfigLoader<RefinementUiHelperConfig>.Load();
            
            Console.WriteLine($"\nVerzeichnis auswählen:");
            Console.WriteLine($" 1) Voreingestellter Pfad: {uiConfig.PredefinedPath}");
            Console.WriteLine($" 2) Eigenen Pfad eingeben");
            Console.Write("Auswahl (1-2, Standard: 1): ");
            string pathChoice = Console.ReadLine()?.Trim() ?? "1";

            string searchFolder;
            if (pathChoice == "2") {
                Console.Write($"\nVerzeichnis eingeben (Standard: {extractionConfig.TargetFolder}): ");
                string folderInput = Console.ReadLine()?.Trim() ?? "";
                searchFolder = string.IsNullOrEmpty(folderInput) ? extractionConfig.TargetFolder : folderInput;
            } else {
                searchFolder = uiConfig.PredefinedPath;
            }

            if (!Directory.Exists(searchFolder)) {
                Console.WriteLine($"[FEHLER] Verzeichnis {searchFolder} nicht gefunden.");
                return;
            }

            Console.WriteLine("\nWelchen Schritt möchtest du ausführen?");
            Console.WriteLine(" 1) Offset Correction / Merge (Schritt 1)");
            Console.WriteLine(" 2) Speech Refinement (Schritt 2)");
            Console.WriteLine(" 3) Last Refinement (Schritt 3)");
            Console.WriteLine(" 4) Komplette Pipeline (Alle 3 Schritte)");
            Console.Write("Auswahl (1-4, Standard: 4): ");
            string stepChoice = Console.ReadLine()?.Trim() ?? "4";

            refinementConfig.Step1MergeAndTimestamp.Enabled = false;
            refinementConfig.Step2SpeechRefinement.Enabled = false;
            refinementConfig.Step3LastRefinement.Enabled = false;

            if (stepChoice == "1") refinementConfig.Step1MergeAndTimestamp.Enabled = true;
            else if (stepChoice == "2") refinementConfig.Step2SpeechRefinement.Enabled = true;
            else if (stepChoice == "3") refinementConfig.Step3LastRefinement.Enabled = true;
            else {
                refinementConfig.Step1MergeAndTimestamp.Enabled = true;
                refinementConfig.Step2SpeechRefinement.Enabled = true;
                refinementConfig.Step3LastRefinement.Enabled = true;
            }

            var texFiles = Directory.GetFiles(searchFolder, "*.tex", SearchOption.AllDirectories).ToArray();
                
            if (texFiles.Length == 0) {
                Console.WriteLine($"Keine passenden .tex Dateien in {searchFolder} oder den Unterordnern gefunden.");
                return;
            }

            texFiles = texFiles.OrderBy(f => Path.GetDirectoryName(f)).ThenBy(f => Path.GetFileName(f)).ToArray();

            Console.WriteLine("\nVerfügbare .tex Dateien:");
            string? lastDir = null;
            for (int i = 0; i < texFiles.Length; i++) {
                string currentDir = Path.GetDirectoryName(texFiles[i]) ?? "";
                if (lastDir != null && currentDir != lastDir) {
                    Console.WriteLine("==========");
                }
                lastDir = currentDir;

                string relativePath = Path.GetRelativePath(searchFolder, texFiles[i]);
                Console.WriteLine($"{i + 1}) {relativePath}");
            }
            Console.Write("\nWähle die Datei für das Refinement (Nummer): ");
            if (!int.TryParse(Console.ReadLine(), out int fileIndex) || fileIndex < 1 || fileIndex > texFiles.Length) {
                Console.WriteLine("Ungültige Auswahl.");
                return;
            }

            string selectedTex = texFiles[fileIndex - 1];
            string selectedDir = Path.GetDirectoryName(selectedTex) ?? searchFolder;

            var audioFiles = Directory.GetFiles(selectedDir, "*.aac");
            string? selectedAudio = null;
            if (stepChoice != "3") {
                if (audioFiles.Length > 0) {
                    Console.WriteLine("\nVerfügbare Audio-Dateien:");
                    for (int i = 0; i < audioFiles.Length; i++) {
                        Console.WriteLine($"{i + 1}) {Path.GetFileName(audioFiles[i])}");
                    }
                    Console.Write("\nWähle die Audio-Datei (Nummer, oder Enter für Überspringen): ");
                    string audioInput = Console.ReadLine()?.Trim() ?? "";
                    if (int.TryParse(audioInput, out int audioIdx) && audioIdx >= 1 && audioIdx <= audioFiles.Length) {
                        selectedAudio = audioFiles[audioIdx - 1];
                    }
                } else {
                    Console.WriteLine("\nKeine Audio-Dateien in diesem Ordner gefunden. (Audio ist null)");
                }
            } else {
                Console.WriteLine("\n[INFO] Überspringe Audio-Auswahl für 'Last Refinement' (Schritt 3 benötigt kein Audio).");
            }

            Console.WriteLine($"\n[INFO] Starte Refinement für: {Path.GetFileName(selectedTex)}");
            
            Client refinementClient;
            if (refinementConfig.UseVertex) {
                refinementClient = GoogleGenAi.GoogleAiClientBuilder.BuildVertexClient(
                    refinementConfig.VertexProjectId, 
                    refinementConfig.VertexLocation
                );
            } else {
                string refinementApiKey;
                if (refinementConfig.AiStudioActiveApiProfile == 0) {
                    refinementApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKeyByName(refinementConfig.AiStudioApiKeyEnvName) ?? "no-key";
                } else {
                    refinementApiKey = GoogleGenAi.GoogleAiClientBuilder.ResolveApiKey(refinementConfig.AiStudioActiveApiProfile) ?? "no-key";
                }
                refinementClient = GoogleGenAi.GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);
            }

            var refinementSession = new LatexRefinementSession(
                refinementClient,
                refinementConfig,
                selectedTex,
                extractionConfig, // using AiStudioAutoExtractionConfig here for the target folder etc. 
                selectedAudio
            );

            await refinementSession.StartAsync();
        }
    }
}
