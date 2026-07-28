using System;
using System.IO;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Interactive confirm-or-change prompts for the persisted configuration values
/// (source folder, model, API key profile), each with an optional callback to persist the change.
/// </summary>
public static class ConfigurationPrompts {
    /// <summary>
    /// <summary>
    /// [AI Context] Interactive prompt verifying or updating the configured source directory.
    /// Displays predefined folders if configured, launches the folder explorer, or allows direct path input.
    /// </summary>
    public static string PromptForSourceFolder(string currentFolder, Action<string>? onFolderChanged = null, string[]? predefinedFolders = null) {
        Console.WriteLine($"\n==================================================");
        Console.WriteLine($" 📁 Quellordner-Auswahl");
        Console.WriteLine($"==================================================");
        Console.WriteLine($" Aktueller Quellordner: {currentFolder}");

        if (predefinedFolders != null && predefinedFolders.Length > 0) {
            Console.WriteLine("\nVordefinierte Quellordner:");
            for (int i = 0; i < predefinedFolders.Length; i++) {
                Console.WriteLine($"  {i + 1}) {predefinedFolders[i]}");
            }
            Console.WriteLine($"  {predefinedFolders.Length + 1}) 🔍 Interaktiver Ordner-Explorer");
            Console.WriteLine($"  {predefinedFolders.Length + 2}) ✍️ Pfad manuell eingeben");
            Console.WriteLine($"  [ENTER]          => Bisherigen Ordner '{currentFolder}' verwenden");

            Console.Write($"\nAuswahl (1-{predefinedFolders.Length + 2}) [Standard: ENTER]: ");
            string choice = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(choice)) {
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                return currentFolder;
            }

            if (int.TryParse(choice, out int choiceNum)) {
                if (choiceNum >= 1 && choiceNum <= predefinedFolders.Length) {
                    currentFolder = predefinedFolders[choiceNum - 1];
                    Console.WriteLine($"\n  🎯 Quellordner ausgewählt: {currentFolder}");
                    DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                    onFolderChanged?.Invoke(currentFolder);
                    return currentFolder;
                }
                else if (choiceNum == predefinedFolders.Length + 1) {
                    currentFolder = FileSelectionPrompt.NavigateDirectory(currentFolder);
                    Console.WriteLine($"\n  🎯 Quellordner ausgewählt: {currentFolder}");
                    DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                    onFolderChanged?.Invoke(currentFolder);
                    return currentFolder;
                }
                else if (choiceNum == predefinedFolders.Length + 2) {
                    // Proceed to manual input
                }
                else {
                    Console.WriteLine("  [INFO] Ungültige Auswahl. Behalte bisherigen Ordner bei.");
                    DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                    return currentFolder;
                }
            }
            else {
                Console.WriteLine("  [INFO] Ungültige Auswahl. Behalte bisherigen Ordner bei.");
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                return currentFolder;
            }
        }
        else {
            Console.WriteLine($"  1) Bisherigen Ordner '{currentFolder}' verwenden");
            Console.WriteLine("  2) 🔍 Interaktiver Ordner-Explorer");
            Console.WriteLine("  3) ✍️ Pfad manuell eingeben");
            Console.Write("\nAuswahl (1-3) [Standard: 1]: ");
            string choice = Console.ReadLine()?.Trim() ?? "";

            if (choice == "2") {
                currentFolder = FileSelectionPrompt.NavigateDirectory(currentFolder);
                Console.WriteLine($"\n  🎯 Quellordner ausgewählt: {currentFolder}");
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                onFolderChanged?.Invoke(currentFolder);
                return currentFolder;
            }
            else if (choice == "3") {
                // Proceed to manual input
            }
            else {
                DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);
                return currentFolder;
            }
        }

        Console.Write("\nBitte neuen Pfad für den Quellordner eingeben: ");
        string? newPath = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(newPath)) {
            newPath = newPath.Trim('\"', '\'');

            if (!Directory.Exists(newPath)) {
                Console.Write($"Der Ordner '{newPath}' existiert nicht. Möchten Sie ihn erstellen? (j/n, Standard: j): ");
                string? createChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (createChoice != "n" && createChoice != "nein") {
                    try {
                        Directory.CreateDirectory(newPath);
                        Console.WriteLine($"  [OK] Ordner erstellt: {newPath}");
                    }
                    catch (Exception ex) {
                        Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
                        Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
                        return currentFolder;
                    }
                }
                else {
                    Console.WriteLine("  [INFO] Behalte bisherigen Ordner bei.");
                    return currentFolder;
                }
            }

            currentFolder = newPath;
            Console.WriteLine($"\n  🎯 Neuer Quellordner ausgewählt: {currentFolder}");
            DirectoryTreeRenderer.DisplayDirectoryPreview(currentFolder);

            if (onFolderChanged != null) {
                Console.Write("Möchten Sie diese Änderung permanent in der Konfiguration speichern? (j/n, Standard: j): ");
                string? saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (saveChoice != "n" && saveChoice != "nein" && saveChoice != "no") {
                    onFolderChanged.Invoke(currentFolder);
                    Console.WriteLine("  💾 [INFO] Der neue Pfad wurde in der Konfiguration (JSON) gespeichert.");
                } else {
                    Console.WriteLine("  [INFO] Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
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
        Console.WriteLine($"\n==================================================");
        Console.WriteLine($" 🤖 Voreingestelltes Modell ({apiType}): {currentModel}");
        Console.WriteLine($"==================================================");

        Console.Write("\nMöchten Sie dieses voreingestellte Modell verwenden? (j/n, Standard: j): ");
        string? choice = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (choice == "n" || choice == "nein" || choice == "no") {
            Console.WriteLine("\nVerfügbare Modelle:");
            for (int i = 0; i < availableModels.Length; i++) {
                Console.WriteLine($" {i + 1}) {availableModels[i]}");
            }

            Console.Write($"\nBitte Modell auswählen (1-{availableModels.Length}) [Standard: {currentModel}]: ");
            string? modelChoice = Console.ReadLine()?.Trim();

            if (modelChoice == "exit" || modelChoice == "quit") return "__EXIT__";

            string newModel;
            if (int.TryParse(modelChoice, out int index) && index >= 1 && index <= availableModels.Length) {
                newModel = availableModels[index - 1];
            }
            else {
                newModel = currentModel;
            }

            Console.WriteLine($"\n  🎯 Neues Modell ausgewählt: {newModel}");

            if (onModelChanged != null) {
                Console.Write("Möchten Sie diese Änderung permanent in der Konfiguration speichern? (j/n, Standard: j): ");
                string? saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (saveChoice != "n" && saveChoice != "nein" && saveChoice != "no") {
                    onModelChanged.Invoke(newModel);
                    Console.WriteLine("  💾 [INFO] Das neue Modell wurde in der Konfiguration (JSON) gespeichert.");
                } else {
                    Console.WriteLine("  [INFO] Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
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

        Console.WriteLine($"\n==================================================");
        Console.WriteLine($" 🔑 API-Key Profil ({sessionName})");
        Console.WriteLine($"==================================================");
        Console.WriteLine($" Aktuelles Profil: {profileLabel} ({currentEnvName}) {keyStatus}");

        Console.Write("\nMöchten Sie dieses API-Key Profil verwenden? (j/n, Standard: j): ");
        string? choice = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (choice == "n" || choice == "nein" || choice == "no") {
            Console.WriteLine("\nVerfügbare API-Key Profile:");
            for (int i = 0; i < envNames.Length; i++) {
                string name = envNames[i];
                string label = i == 0 ? "Dedizierter Key" : $"Profil {i}";
                bool exists = CheckEnvKey(name);
                string status = exists ? "✅ [OK]" : "⚠️ [FEHLT IN ENV]";
                Console.WriteLine($"  {i}) {label} ({name}) {status}");
            }

            Console.Write($"\nBitte Profil auswählen (0-{envNames.Length - 1}) [Standard: {currentProfile}]: ");
            string? profileChoice = Console.ReadLine()?.Trim();

            if (profileChoice == "exit" || profileChoice == "quit") return currentProfile;

            int newProfile = currentProfile;
            if (int.TryParse(profileChoice, out int parsed) && parsed >= 0 && parsed < envNames.Length) {
                newProfile = parsed;
            }

            string newEnvName = envNames[newProfile];
            string newLabel = newProfile == 0 ? "Dedizierter Key (0)" : $"Profil {newProfile}";
            Console.WriteLine($"\n  🎯 Neues API-Key Profil ausgewählt: {newLabel} ({newEnvName})");

            if (onProfileChanged != null) {
                Console.Write("Möchten Sie diese Änderung permanent in der Konfiguration speichern? (j/n, Standard: j): ");
                string? saveChoice = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (saveChoice != "n" && saveChoice != "nein" && saveChoice != "no") {
                    onProfileChanged.Invoke(newProfile);
                    Console.WriteLine("  💾 [INFO] Das neue API-Key Profil wurde in der Konfiguration (JSON) gespeichert.");
                } else {
                    Console.WriteLine("  [INFO] Die Änderung ist nur vorübergehend (wird nicht in JSON gespeichert).");
                }
            }

            return newProfile;
        }

        return currentProfile;
    }
}
