using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Refinement;
using Spectre.Console;

namespace LectureExtraction.Extraction;

public static class RefinementUiHelper {
    public static async Task StartInteractiveRefinementAsync(LatexRefinementSessionConfig refinementConfig, IAutoExtractionConfig extractionConfig) {
        Ui.Header("Interaktiver LaTeX Refinement Modus");

        refinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();
        if (!AppConfig.IsVertexAiEnabled && refinementConfig.UseVertex) {
            Ui.Warn("Google Cloud Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false)! Wechsle für LaTeX Refinement automatisch auf AI Studio.", "Kostenschutz");
            refinementConfig.UseVertex = false;
            ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
        }

        while (true) {
            string backendDisplay = refinementConfig.UseVertex ? "Vertex AI" : "AI Studio";
            string profileDisplay = refinementConfig.AiStudioActiveApiProfile == 0 ? "Dediziert (API_KEY-latex-refinement)" : $"Profil {refinementConfig.AiStudioActiveApiProfile}";

            string currentModel = refinementConfig.UseVertex
                ? refinementConfig.Step1MergeAndTimestamp.Vertex.CurrentModel
                : refinementConfig.Step1MergeAndTimestamp.AiStudio.CurrentModel;

            Ui.Detail($"Backend:    {backendDisplay}");
            if (!refinementConfig.UseVertex) {
                Ui.Detail($"API-Profil: {profileDisplay}");
            }
            else {
                Ui.Detail($"Project ID: {refinementConfig.VertexProjectId}");
            }
            Ui.Detail($"Modell:     {currentModel}");

            var choices = new[] {
                "1) Refinement fortsetzen (Dateien wählen)",
                $"2) Backend wechseln (Aktuell: {backendDisplay})",
                "3) API Key Profil ändern (Nur für AI Studio)",
                "4) Modell ändern (Für aktuelles Backend)",
                "5) Abbrechen"
            };

            var menuChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold cyan]Optionen:[/]")
                    .AddChoices(choices)
            );

            if (menuChoice.StartsWith("2")) {
                if (!AppConfig.IsVertexAiEnabled && !refinementConfig.UseVertex) {
                    Ui.Warn("Google Cloud Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false). Wechsel auf Vertex nicht möglich.", "Kostenschutz");
                    continue;
                }
                refinementConfig.UseVertex = !refinementConfig.UseVertex;
                ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                Ui.Info($"Backend gewechselt auf: {(refinementConfig.UseVertex ? "Vertex AI" : "AI Studio")}");
                continue;
            }
            else if (menuChoice.StartsWith("3")) {
                if (refinementConfig.UseVertex) {
                    Ui.Warn("API Profile sind nur für AI Studio relevant.");
                    continue;
                }
                refinementConfig.AiStudioActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                    refinementConfig.AiStudioActiveApiProfile,
                    "LaTeX Refinement Session",
                    newProfile => {
                        refinementConfig.AiStudioActiveApiProfile = newProfile;
                        ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                    },
                    refinementConfig.AiStudioApiKeyEnvNames
                );
                continue;
            }
            else if (menuChoice.StartsWith("4")) {
                var models = new[] { "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview" };
                string newModel = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold cyan]Wähle ein Modell:[/]")
                        .AddChoices(models)
                );

                if (!string.IsNullOrEmpty(newModel)) {
                    if (refinementConfig.UseVertex) {
                        refinementConfig.Step1MergeAndTimestamp.Vertex.CurrentModel = newModel;
                        refinementConfig.Step2SpeechRefinement.Vertex.CurrentModel = newModel;
                        refinementConfig.Step3LastRefinement.Vertex.CurrentModel = newModel;
                    }
                    else {
                        refinementConfig.Step1MergeAndTimestamp.AiStudio.CurrentModel = newModel;
                        refinementConfig.Step2SpeechRefinement.AiStudio.CurrentModel = newModel;
                        refinementConfig.Step3LastRefinement.AiStudio.CurrentModel = newModel;
                    }
                    ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                    Ui.Success($"Modell auf '{newModel}' aktualisiert.");
                }
                continue;
            }
            else if (menuChoice.StartsWith("5")) {
                return;
            }

            break;
        }

        var uiConfig = ConfigLoader<RefinementUiHelperConfig>.Load();

        string searchFolder = ConfigurationPrompts.PromptForSourceFolder(uiConfig.PredefinedPath, newFolder => {
            uiConfig.PredefinedPath = newFolder;
            ConfigLoader<RefinementUiHelperConfig>.Save(uiConfig);
        });

        if (!Directory.Exists(searchFolder)) {
            Ui.Error($"Verzeichnis {searchFolder} nicht gefunden.");
            return;
        }

        var stepOptions = new[] {
            "4) Komplette Pipeline (Alle 3 Schritte)",
            "1) Offset Correction / Merge (Schritt 1)",
            "2) Speech Refinement (Schritt 2)",
            "3) Last Refinement (Schritt 3)"
        };

        string selectedStepChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold cyan]Welchen Schritt möchtest du ausführen?[/]")
                .AddChoices(stepOptions)
        );

        string stepChoice = selectedStepChoice[..1];

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
            Ui.Warn($"Keine passenden .tex Dateien in {searchFolder} oder den Unterordnern gefunden.");
            return;
        }

        texFiles = [.. texFiles.OrderBy(f => Path.GetDirectoryName(f)).ThenBy(f => Path.GetFileName(f))];

        var fileChoices = texFiles.Select(f => Path.GetRelativePath(searchFolder, f)).ToArray();
        string selectedRelativeTex = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold cyan]Wähle die .tex Datei für das Refinement:[/]")
                .PageSize(15)
                .AddChoices(fileChoices)
        );

        string selectedTex = Path.Combine(searchFolder, selectedRelativeTex);
        Ui.Success($"Ausgewählte Datei: {selectedRelativeTex}");
        string selectedDir = Path.GetDirectoryName(selectedTex) ?? searchFolder;

        var audioFiles = Directory.GetFiles(selectedDir, "*.aac");
        string? selectedAudio = null;
        if (stepChoice != "3") {
            if (audioFiles.Length > 0) {
                var audioChoices = new List<string> { "(Ohne Audio fortfahren)" };
                audioChoices.AddRange(audioFiles.Select(f => Path.GetFileName(f)));

                string audioChoice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold cyan]Wähle die Audio-Datei:[/]")
                        .AddChoices(audioChoices)
                );

                if (audioChoice != "(Ohne Audio fortfahren)") {
                    selectedAudio = audioFiles.FirstOrDefault(f => Path.GetFileName(f) == audioChoice);
                    Ui.Success($"Ausgewählte Audio-Datei: {audioChoice}");
                }
            }
            else {
                Ui.Info("Keine Audio-Dateien in diesem Ordner gefunden.");
            }
        }
        else {
            Ui.Info("Überspringe Audio-Auswahl für 'Last Refinement' (Schritt 3 benötigt kein Audio).");
        }

        if (stepChoice == "1" || stepChoice == "2" || stepChoice == "3") {
            bool runToEnd = Ui.Confirm("Möchtest du ab diesem Schritt die restliche Pipeline bis zum Ende (inkl. Schritt 4: PDF-Kompilierung) ausführen?", false);
            if (runToEnd) {
                if (stepChoice == "1") {
                    refinementConfig.Step2SpeechRefinement.Enabled = true;
                    refinementConfig.Step3LastRefinement.Enabled = true;
                }
                else if (stepChoice == "2") {
                    refinementConfig.Step3LastRefinement.Enabled = true;
                }
                if (refinementConfig.PdfCompilation != null) {
                    refinementConfig.PdfCompilation.Enabled = true;
                }
                Ui.Info("Pipeline wird ab dem gewählten Schritt bis zum Ende (inkl. Schritt 4: PDF-Kompilierung) ausgeführt.");
            }
            else {
                if (refinementConfig.PdfCompilation != null) {
                    refinementConfig.PdfCompilation.Enabled = false;
                }
                Ui.Info($"Es wird ausschließlich Schritt {stepChoice} ausgeführt (ohne nachfolgende PDF-Kompilierung).");
            }
        }
        else {
            if (refinementConfig.PdfCompilation != null) {
                refinementConfig.PdfCompilation.Enabled = true;
            }
        }

        Ui.Info($"Starte Refinement für: {Path.GetFileName(selectedTex)}");

        Client refinementClient;
        if (refinementConfig.UseVertex && AppConfig.IsVertexAiEnabled) {
            refinementClient = GoogleAiClientBuilder.BuildVertexClient(
                refinementConfig.VertexProjectId,
                refinementConfig.VertexLocation
            );
        }
        else {
            string? extractedRefinementEnvName = (refinementConfig.AiStudioApiKeyEnvNames != null && refinementConfig.AiStudioApiKeyEnvNames.Length > refinementConfig.AiStudioActiveApiProfile)
                ? refinementConfig.AiStudioApiKeyEnvNames[refinementConfig.AiStudioActiveApiProfile]
                : null;
            string envName = !string.IsNullOrEmpty(extractedRefinementEnvName)
                ? extractedRefinementEnvName
                : "API_KEY-latex-refinement";
            string refinementApiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
            refinementClient = GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);
        }

        var refinementSession = new LatexRefinementSession(
            refinementClient,
            refinementConfig,
            selectedTex,
            extractionConfig,
            selectedAudio
        );

        await refinementSession.StartAsync();
    }
}
