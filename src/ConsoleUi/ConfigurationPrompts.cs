using System;
using System.Collections.Generic;
using System.IO;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Interactive confirm-or-change prompts for the persisted configuration values
/// (source folder, model, API key profile), each with an optional callback to persist the change.
///
/// <para>All three return a <see cref="PromptResult{T}"/>: they sit in the middle of multi-step
/// setup flows, so "the user wants to go back a step" has to be distinguishable from "the user
/// kept the current value". Collapsing the two - which is what returning a bare string did - makes
/// a back option impossible to add without changing every caller again.</para>
/// </summary>
public static class ConfigurationPrompts {
    private enum SourceFolderAction { Use, Explorer, Manual }

    private sealed record SourceFolderChoice(SourceFolderAction Action, string Path);

    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured source directory.
    /// Displays predefined folders if configured, launches the folder explorer, or allows direct path input.
    /// </summary>
    public static PromptResult<string> PromptForSourceFolder(string currentFolder, Action<string>? onFolderChanged = null, string[]? predefinedFolders = null, bool allowBack = true) {
        Ui.Step("Quellordner-Auswahl");
        Ui.Detail($"Aktueller Quellordner: {currentFolder}");

        var choices = new List<(string Label, SourceFolderChoice Value)>();
        string title;

        if (predefinedFolders != null && predefinedFolders.Length > 0) {
            title = "Vordefinierte Quellordner:";
            for (int i = 0; i < predefinedFolders.Length; i++) {
                choices.Add(($"{i + 1}) {predefinedFolders[i]}", new SourceFolderChoice(SourceFolderAction.Use, predefinedFolders[i])));
            }
            choices.Add(($"{predefinedFolders.Length + 1}) 🔍 Interaktiver Ordner-Explorer", new SourceFolderChoice(SourceFolderAction.Explorer, currentFolder)));
            choices.Add(($"{predefinedFolders.Length + 2}) ✍️ Pfad manuell eingeben", new SourceFolderChoice(SourceFolderAction.Manual, currentFolder)));
            choices.Add(($"[Standard] Bisherigen Ordner '{currentFolder}' verwenden", new SourceFolderChoice(SourceFolderAction.Use, currentFolder)));
        }
        else {
            title = "Quellordner-Optionen:";
            choices.Add(($"1) Bisherigen Ordner '{currentFolder}' verwenden", new SourceFolderChoice(SourceFolderAction.Use, currentFolder)));
            choices.Add(("2) 🔍 Interaktiver Ordner-Explorer", new SourceFolderChoice(SourceFolderAction.Explorer, currentFolder)));
            choices.Add(("3) ✍️ Pfad manuell eingeben", new SourceFolderChoice(SourceFolderAction.Manual, currentFolder)));
        }

        var selection = Ui.Select(title, choices, allowBack);
        if (!selection.IsValue || selection.Value == null) {
            return new PromptResult<string>(selection.Outcome, null);
        }

        switch (selection.Value.Action) {
            case SourceFolderAction.Use:
                if (selection.Value.Path != currentFolder) {
                    currentFolder = selection.Value.Path;
                    Ui.Info($"Quellordner ausgewählt: {currentFolder}");
                    onFolderChanged?.Invoke(currentFolder);
                }
                return PromptResult.FromValue(currentFolder);

            case SourceFolderAction.Explorer:
                currentFolder = FileSelectionPrompt.NavigateDirectory(currentFolder);
                Ui.Info($"Quellordner ausgewählt: {currentFolder}");
                onFolderChanged?.Invoke(currentFolder);
                return PromptResult.FromValue(currentFolder);

            default:
                return PromptResult.FromValue(PromptForManualPath(currentFolder, onFolderChanged));
        }
    }

    /// <summary>
    /// [AI Context] The free-text branch of <see cref="PromptForSourceFolder"/>: takes a typed path,
    /// offers to create it when missing, and asks whether the change should be persisted. Split out
    /// so the choice dispatch above stays readable.
    /// [Human] Fragt einen Pfad ab, bietet an ihn zu erstellen und fragt, ob er gespeichert werden soll.
    /// </summary>
    private static string PromptForManualPath(string currentFolder, Action<string>? onFolderChanged) {
        string newPath = Ui.Ask("Bitte neuen Pfad für den Quellordner eingeben:").Trim();
        if (string.IsNullOrWhiteSpace(newPath)) {
            return currentFolder;
        }

        newPath = newPath.Trim('\"', '\'');

        if (!Directory.Exists(newPath)) {
            if (Ui.Confirm($"Der Ordner '{newPath}' existiert nicht. Möchten Sie ihn erstellen?", true)) {
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

        if (onFolderChanged != null) {
            if (Ui.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", true)) {
                onFolderChanged.Invoke(currentFolder);
                Ui.Info("Der neue Pfad wurde in der Konfiguration (JSON) gespeichert.");
            }
            else {
                Ui.Info("Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
            }
        }

        return currentFolder;
    }

    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured model.
    /// Allows persisting the new model to configuration JSON.
    /// </summary>
    public static PromptResult<string> ConfirmOrChangeModel(string currentModel, string apiType, string[] availableModels, Action<string>? onModelChanged = null, bool allowBack = true) {
        Ui.Step($"Voreingestelltes Modell ({apiType}): {currentModel}");

        while (true) {
            var keepCurrent = Ui.ConfirmOrBack($"Möchten Sie dieses voreingestellte Modell ({currentModel}) verwenden?", true);
            if (keepCurrent.IsBack && allowBack) return PromptResult.Back<string>();
            if (!keepCurrent.IsValue) return PromptResult.FromValue(currentModel);
            if (keepCurrent.Value) return PromptResult.FromValue(currentModel);

            var choices = new List<(string Label, string Value)>();
            for (int i = 0; i < availableModels.Length; i++) {
                choices.Add(($"{i + 1}) {availableModels[i]}", availableModels[i]));
            }

            // Back here returns to the confirm question above rather than out of the prompt - the
            // user who lands in this list by answering "Nein" needs a way to take that back.
            var selection = Ui.Select("Verfügbare Modelle:", choices);
            if (!selection.IsValue || selection.Value == null) continue;

            string newModel = selection.Value;
            Ui.Info($"Neues Modell ausgewählt: {newModel}");

            if (onModelChanged != null) {
                if (Ui.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", true)) {
                    onModelChanged.Invoke(newModel);
                    Ui.Info("Das neue Modell wurde in der Konfiguration (JSON) gespeichert.");
                }
                else {
                    Ui.Info("Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                }
            }

            return PromptResult.FromValue(newModel);
        }
    }

    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured active API Key profile (0-3).
    /// Checks environment variable resolution and allows persisting changes to configuration JSON.
    /// [Human] Interaktiver Dialog zum Bestätigen oder Wechseln des aktiven API-Key Profils (0 für dediziert, 1-3 für Testprojekte).
    /// </summary>
    public static PromptResult<int> ConfirmOrChangeApiKeyProfile(int currentProfile, string sessionName, Action<int>? onProfileChanged = null, string[]? envNames = null, bool allowBack = true) {
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

        while (true) {
            var keepCurrent = Ui.ConfirmOrBack("Möchten Sie dieses API-Key Profil verwenden?", true);
            if (keepCurrent.IsBack && allowBack) return PromptResult.Back<int>();
            if (!keepCurrent.IsValue) return PromptResult.FromValue(currentProfile);
            if (keepCurrent.Value) return PromptResult.FromValue(currentProfile);

            var choices = new List<(string Label, int Value)>();
            for (int i = 0; i < envNames.Length; i++) {
                string name = envNames[i];
                string label = i == 0 ? "Dedizierter Key" : $"Profil {i}";
                string status = CheckEnvKey(name) ? "✅ [OK]" : "⚠️ [FEHLT IN ENV]";
                choices.Add(($"{i}) {label} ({name}) {status}", i));
            }

            var selection = Ui.Select("Verfügbare API-Key Profile:", choices);
            if (!selection.IsValue) continue;

            int newProfile = selection.Value;
            string newEnvName = envNames[newProfile];
            string newLabel = newProfile == 0 ? "Dedizierter Key (0)" : $"Profil {newProfile}";
            Ui.Info($"Neues API-Key Profil ausgewählt: {newLabel} ({newEnvName})");

            if (onProfileChanged != null) {
                if (Ui.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", true)) {
                    onProfileChanged.Invoke(newProfile);
                    Ui.Info("Das neue API-Key Profil wurde in der Konfiguration (JSON) gespeichert.");
                }
                else {
                    Ui.Info("Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                }
            }

            return PromptResult.FromValue(newProfile);
        }
    }
}
