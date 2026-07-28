using System;
using System.IO;
using LectureExtraction.GoogleAi;
using Spectre.Console;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Interactive confirm-or-change prompts for the persisted configuration values
/// (source folder, model, API key profile), each with an optional callback to persist the change.
/// </summary>
public static class ConfigurationPrompts {
    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured source directory.
    /// Displays predefined folders if configured, launches the folder explorer, or allows direct path input.
    /// </summary>
    public static string PromptForSourceFolder(string currentFolder, Action<string>? onFolderChanged = null, string[]? predefinedFolders = null) {
        Ui.Step("Quellordner-Auswahl");
        Ui.Detail($"Aktueller Quellordner: {currentFolder}");

        if (predefinedFolders != null && predefinedFolders.Length > 0) {
            var choices = new System.Collections.Generic.List<string>();
            for (int i = 0; i < predefinedFolders.Length; i++) {
                choices.Add($"{i + 1}) {predefinedFolders[i]}");
            }
            string optExplorer = $"{predefinedFolders.Length + 1}) 🔍 Interaktiver Ordner-Explorer";
            string optManual = $"{predefinedFolders.Length + 2}) ✍️ Pfad manuell eingeben";
            string optKeep = $"[Standard] Bisherigen Ordner '{currentFolder}' verwenden";
            choices.Add(optExplorer);
            choices.Add(optManual);
            choices.Add(optKeep);

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Vordefinierte Quellordner:[/]")
                    .AddChoices(choices));

            if (selection == optKeep) {
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                return currentFolder;
            }

            for (int i = 0; i < predefinedFolders.Length; i++) {
                if (selection.StartsWith($"{i + 1})")) {
                    currentFolder = predefinedFolders[i];
                    Ui.Info($"Quellordner ausgewählt: {currentFolder}");
                    DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                    onFolderChanged?.Invoke(currentFolder);
                    return currentFolder;
                }
            }

            if (selection == optExplorer) {
                currentFolder = FileSelectionPrompt.NavigateDirectory(currentFolder);
                Ui.Info($"Quellordner ausgewählt: {currentFolder}");
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                onFolderChanged?.Invoke(currentFolder);
                return currentFolder;
            }
        }
        else {
            string optKeep = $"1) Bisherigen Ordner '{currentFolder}' verwenden";
            string optExplorer = "2) 🔍 Interaktiver Ordner-Explorer";
            string optManual = "3) ✍️ Pfad manuell eingeben";

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Quellordner-Optionen:[/]")
                    .AddChoices(optKeep, optExplorer, optManual));

            if (selection == optExplorer) {
                currentFolder = FileSelectionPrompt.NavigateDirectory(currentFolder);
                Ui.Info($"Quellordner ausgewählt: {currentFolder}");
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                onFolderChanged?.Invoke(currentFolder);
                return currentFolder;
            }
            else if (selection == optKeep) {
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                return currentFolder;
            }
        }

        string newPath = AnsiConsole.Ask<string>("Bitte neuen Pfad für den Quellordner eingeben:").Trim();
        if (!string.IsNullOrWhiteSpace(newPath)) {
            newPath = newPath.Trim('\"', '\'');

            if (!Directory.Exists(newPath)) {
                if (AnsiConsole.Confirm($"Der Ordner '{newPath}' existiert nicht. Möchten Sie ihn erstellen?", defaultValue: true)) {
                    try {
                        Directory.CreateDirectory(newPath);
                        Ui.Success($"Ordner erstellt: {newPath}");
                    }
                    catch (Exception ex) {
                        Ui.Error($"Unerwarteter Fehler beim Erstellen des Ordners: {ex.Message}");
                        return currentFolder;
                    }
                }
                else {
                    Ui.Info("Behalte bisherigen Ordner bei.");
                    return currentFolder;
                }
            }

            currentFolder = newPath;
            Ui.Info($"Neuer Quellordner ausgewählt: {currentFolder}");
            DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);

            if (onFolderChanged != null) {
                if (AnsiConsole.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", defaultValue: true)) {
                    onFolderChanged.Invoke(currentFolder);
                    Ui.Info("Der neue Pfad wurde in der Konfiguration (JSON) gespeichert.");
                } else {
                    Ui.Info("Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                }
            }
        }

        return currentFolder;
    }

    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured model.
    /// Allows persisting the new model to configuration JSON.
    /// </summary>
    public static string ConfirmOrChangeModel(string currentModel, string apiType, string[] availableModels, Action<string>? onModelChanged = null) {
        Ui.Step($"Voreingestelltes Modell ({apiType}): {currentModel}");

        if (!AnsiConsole.Confirm($"Möchten Sie dieses voreingestellte Modell ({currentModel}) verwenden?", defaultValue: true)) {
            var choices = new System.Collections.Generic.List<string>();
            for (int i = 0; i < availableModels.Length; i++) {
                choices.Add($"{i + 1}) {availableModels[i]}");
            }
            string optCancel = "Abbrechen / Bisheriges Modell beibehalten";
            choices.Add(optCancel);

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Verfügbare Modelle:[/]")
                    .AddChoices(choices));

            if (selection == optCancel) return currentModel;

            string newModel = currentModel;
            for (int i = 0; i < availableModels.Length; i++) {
                if (selection.StartsWith($"{i + 1})")) {
                    newModel = availableModels[i];
                    break;
                }
            }

            Ui.Info($"Neues Modell ausgewählt: {newModel}");

            if (onModelChanged != null) {
                if (AnsiConsole.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", defaultValue: true)) {
                    onModelChanged.Invoke(newModel);
                    Ui.Info("Das neue Modell wurde in der Konfiguration (JSON) gespeichert.");
                } else {
                    Ui.Info("Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                }
            }

            return newModel;
        }

        return currentModel;
    }

    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured active API Key profile (0-3).
    /// Checks environment variable resolution and allows persisting changes to configuration JSON.
    /// [Human] Interaktiver Dialog zum Bestätigen oder Wechseln des aktiven API-Key Profils (0 für dediziert, 1-3 für Testprojekte).
    /// </summary>
    public static int ConfirmOrChangeApiKeyProfile(int currentProfile, string sessionName, Action<int>? onProfileChanged = null, string[]? envNames = null) {
        envNames ??= [
            "API_KEY-automated-content-extraction",
            "API_KEY-ai-studio-test-project-1",
            "API_KEY-ai-studio-test-project-2",
            "API_KEY-ai-studio-test-project-3"
        ];

        static bool CheckEnvKey(string name) {
            string? key = Environment.GetEnvironmentVariable(name)
                       ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                       ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            return !string.IsNullOrEmpty(key);
        }

        string currentEnvName = ApiKeyProfileResolver.Resolve(currentProfile, envNames);
        string profileLabel = currentProfile == 0 ? "Dedizierter Key (0)" : $"Profil {currentProfile}";
        bool hasCurrentKey = CheckEnvKey(currentEnvName);
        string keyStatus = hasCurrentKey ? "✅ [VORHANDEN]" : "⚠️ [NICHT GEFUNDEN IN ENV]";

        Ui.Step($"API-Key Profil ({sessionName})");
        Ui.Detail($"Aktuelles Profil: {profileLabel} ({currentEnvName}) {keyStatus}");

        if (!AnsiConsole.Confirm("Möchten Sie dieses API-Key Profil verwenden?", defaultValue: true)) {
            var choices = new System.Collections.Generic.List<string>();
            for (int i = 0; i < envNames.Length; i++) {
                string name = envNames[i];
                string label = i == 0 ? "Dedizierter Key" : $"Profil {i}";
                bool exists = CheckEnvKey(name);
                string status = exists ? "✅ [OK]" : "⚠️ [FEHLT IN ENV]";
                choices.Add($"{i}) {label} ({name}) {status}");
            }
            string optCancel = "Abbrechen / Bisheriges Profil beibehalten";
            choices.Add(optCancel);

            var selection = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Verfügbare API-Key Profile:[/]")
                    .AddChoices(choices));

            if (selection == optCancel) return currentProfile;

            int newProfile = currentProfile;
            for (int i = 0; i < envNames.Length; i++) {
                if (selection.StartsWith($"{i})")) {
                    newProfile = i;
                    break;
                }
            }

            string newEnvName = envNames[newProfile];
            string newLabel = newProfile == 0 ? "Dedizierter Key (0)" : $"Profil {newProfile}";
            Ui.Info($"Neues API-Key Profil ausgewählt: {newLabel} ({newEnvName})");

            if (onProfileChanged != null) {
                if (AnsiConsole.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", defaultValue: true)) {
                    onProfileChanged.Invoke(newProfile);
                    Ui.Info("Das neue API-Key Profil wurde in der Konfiguration (JSON) gespeichert.");
                } else {
                    Ui.Info("Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                }
            }

            return newProfile;
        }

        return currentProfile;
    }
}
