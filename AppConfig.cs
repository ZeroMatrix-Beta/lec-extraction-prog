using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DirectChatAiInteraction.AiStudio;


namespace Config;

/// <summary>
/// Generic configuration loader implementing the hierarchy:
/// corresponding .json > appconfig.json > C# static variable > C# app static
/// </summary>
public static class ConfigLoader<T> where T : class, new() {
    public static T Load(string? sectionName = null) {
        sectionName ??= typeof(T).Name;
        var basePath = AppDomain.CurrentDomain.BaseDirectory;

        // Build a single configuration object with a clear hierarchy.
        // The last source added wins for keys at the same path.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true) // 2. Base settings from global file.
            .AddJsonFile($"{typeof(T).Name}.json", optional: true) // 3. Specific file overrides global.
            .Build();

        // 1. Start with a new instance, which will have the C# default values.
        var config = new T();

        // Bind from the "AppConfig:TypeName" section of the combined configuration.
        // This handles values defined within the AppConfig block in appsettings.json.
        configuration.GetSection("AppConfig").GetSection(sectionName).Bind(config);

        ClearCollectionsRecursively(config);

        // Bind from the root of the combined configuration.
        // This allows the specific {TypeName}.json to have settings at the root level,
        // overriding any values that were previously bound.
        configuration.Bind(config);

        return config;
    }

    private static void ClearCollectionsRecursively(object? obj) {
        if (obj == null) return;
        var type = obj.GetType();
        if (type.IsPrimitive || type == typeof(string) || type.IsValueType) return;

        foreach (var prop in type.GetProperties()) {
            if (!prop.CanRead) continue;

            if (prop.PropertyType.IsArray && prop.CanWrite) {
                prop.SetValue(obj, Array.CreateInstance(prop.PropertyType.GetElementType()!, 0));
            }
            else if (typeof(System.Collections.IList).IsAssignableFrom(prop.PropertyType)) {
                if (prop.GetValue(obj) is System.Collections.IList list && list.Count > 0) {
                    list.Clear();
                }
            }
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string)) {
                var val = prop.GetValue(obj);
                if (val != null) {
                    ClearCollectionsRecursively(val);
                }
            }
        }
    }

    /// <summary>
    /// [AI Context] Recursively updates existing JSON properties from `source` to `target` while keeping `JTokenType.Comment` nodes in `target.Children()` intact.
    /// Unlike `JObject.Merge`, which replaces structure blocks and deletes adjacent comment nodes, this updates property values in-place so comments in .json configs survive saving.
    /// [Human] Aktualisiert JSON-Eigenschaften gezielt vor Ort, ohne dass darüber oder darunter liegende Kommentare (`// ...`) beim Speichern gelöscht werden.
    /// </summary>
    private static void UpdatePropertiesPreservingComments(JToken target, JToken source) {
        if (target is JObject targetObj && source is JObject sourceObj) {
            foreach (var sourceProp in sourceObj.Properties()) {
                var targetProp = targetObj.Property(sourceProp.Name);
                if (targetProp != null) {
                    if (targetProp.Value is JObject && sourceProp.Value is JObject) {
                        UpdatePropertiesPreservingComments(targetProp.Value, sourceProp.Value);
                    }
                    else if (targetProp.Value is JArray targetArr && sourceProp.Value is JArray sourceArr) {
                        var newArray = new JArray();
                        int sourceIndex = 0;
                        foreach (var token in targetArr.Children()) {
                            if (token.Type == JTokenType.Comment) {
                                newArray.Add(token.DeepClone());
                            } else if (sourceIndex < sourceArr.Count) {
                                newArray.Add(sourceArr[sourceIndex].DeepClone());
                                sourceIndex++;
                            }
                        }
                        while (sourceIndex < sourceArr.Count) {
                            newArray.Add(sourceArr[sourceIndex].DeepClone());
                            sourceIndex++;
                        }
                        targetProp.Value = newArray;
                    }
                    else if (targetProp.Value is JValue targetVal && sourceProp.Value is JValue sourceVal) {
                        targetVal.Value = sourceVal.Value;
                    }
                    else {
                        targetProp.Value = sourceProp.Value.DeepClone();
                    }
                }
                else {
                    targetObj.Add(sourceProp.Name, sourceProp.Value.DeepClone());
                }
            }
        }
    }

    /// <summary>
    /// [AI Context] Serializes the configuration object to a formatted JSON string while preserving existing JSON comments and formatting from the target file.
    /// [Human] Speichert die Konfiguration in die JSON-Datei und sorgt dafür, dass bestehende Kommentare nicht verloren gehen.
    /// </summary>
    private static string SerializePreservingComments(string filePath, T config) {
        if (File.Exists(filePath)) {
            try {
                string existingJson = File.ReadAllText(filePath);
                var job = JObject.Parse(existingJson, new JsonLoadSettings { CommentHandling = CommentHandling.Load, LineInfoHandling = LineInfoHandling.Load });
                var updatedJob = JObject.FromObject(config);
                UpdatePropertiesPreservingComments(job, updatedJob);
                return job.ToString(Formatting.Indented);
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[AppConfig Warnung] Kommentare in '{Path.GetFileName(filePath)}' konnten beim Speichern nicht erhalten werden. Art der Exception: {ex.GetType().Name}, Fehler: {ex.Message}");
            }
        }
        return JsonConvert.SerializeObject(config, Formatting.Indented);
    }

    public static void Save(T config) {
        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        string fileName = $"{typeof(T).Name}.json";
        string filePath = Path.Combine(basePath, fileName);

        string jsonString = SerializePreservingComments(filePath, config);
        File.WriteAllText(filePath, jsonString);

        try {
            string currentDirFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(currentDirFile), StringComparison.OrdinalIgnoreCase)
                && File.Exists(currentDirFile)) {
                string currentDirJson = SerializePreservingComments(currentDirFile, config);
                File.WriteAllText(currentDirFile, currentDirJson);
            }
        }
        catch (Exception ex) {
            Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
            Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
        }
    }
}


// [AI Context] The DTO that directly maps to the structure of the appsettings.json file.
// We provide default fallback values here just in case the JSON file is missing or malformed.
public class AppConfigOptions {
    public string BaseLectureFolder { get; set; } = @"D:\lecture-videos";
    public string UploadFolder { get; set; } = @"D:\gemini-upload-folder";
    public string LogFolder { get; set; } = @"D:\gemini-logs";
    public string[] HistoryPreloadPaths { get; set; } = [];
    public string SystemInstructionPath { get; set; } = @"";
    public string VertexProjectId { get; set; } = "vertex-ai-experiments-494320";
    public string VertexLocation { get; set; } = "global";
    public string VertexGcsBucketName { get; set; } = "vertex-ai-experiments-upload-bucket-us";
    public string DefaultModel { get; set; } = "gemini-3.5-flash"; // This is for other sessions
    public string RefinementModel { get; set; } = "gemini-3.5-flash"; // This is for LatexRefinement
    public float DefaultTemperature { get; set; } = 0.35f;
    public float DefaultTopP { get; set; } = 0.90f;
    public int DefaultTopK { get; set; } = 40;
    public int DefaultMaxOutputTokens { get; set; } = 65535;
    public int? DefaultThinkingBudget { get; set; } = 24576;
    public string? DefaultThinkingLevel { get; set; } = "HIGH";
}

/// <summary>
/// [AI Context] Centralized 'Single Point of Truth' for all hardcoded paths and default parameters.
/// Uses the Microsoft.Extensions.Configuration binder to dynamically load values from appsettings.json.
/// </summary>
public static class AppConfig {
    private static readonly AppConfigOptions _options;

    static AppConfig() {
        // [AI Context] Automatically looks for appsettings.json in the compiled output directory.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        _options = new AppConfigOptions();
        configuration.GetSection("AppConfig").Bind(_options);
    }

    // --- Basis-Verzeichnisse (Directories) ---
    public static string BaseLectureFolder => _options.BaseLectureFolder;
    public static string UploadFolder => _options.UploadFolder;
    public static string LogFolder => _options.LogFolder;
    public static string[] HistoryPreloadPaths => _options.HistoryPreloadPaths;

    // --- Dynamisch zusammengesetzte Pfade ---
    public static string AutoExtractionSourceFolder => Path.Combine(BaseLectureFolder, "analysis2");
    public static string AutoExtractionTargetFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination2");

    public static string VertexAutoExtractionSourceFolder => Path.Combine(BaseLectureFolder, @"d-und-a\new");
    public static string VertexAutoExtractionTargetFolder => Path.Combine(BaseLectureFolder, @"d-und-a\extracted");

    public static string LatexRefinementSourceFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement");
    public static string LatexRefinementTargetFolder => Path.Combine(BaseLectureFolder, @"analysis2\destination\tex-refinement\refined");

    public static string FfmpegSourceFolder => Path.Combine(BaseLectureFolder, "d-und-a");
    public static string FfmpegTargetFolder => Path.Combine(BaseLectureFolder, @"d-und-a\new");

    // --- Dateien (Files) ---
    public static string SystemInstructionPath => _options.SystemInstructionPath;

    // --- Cloud & API ---
    public static string VertexProjectId => _options.VertexProjectId;
    public static string VertexLocation => _options.VertexLocation;
    public static string VertexGcsBucketName => _options.VertexGcsBucketName;

    // --- Standard KI-Parameter ---
    public static string DefaultModel => _options.DefaultModel;
    public static string RefinementModel => _options.RefinementModel;
    public static float DefaultTemperature => _options.DefaultTemperature;
    public static float DefaultTopP => _options.DefaultTopP;
    public static int DefaultTopK => _options.DefaultTopK;
    public static int DefaultMaxOutputTokens => _options.DefaultMaxOutputTokens;
    public static int? DefaultThinkingBudget => _options.DefaultThinkingBudget;
    public static string? DefaultThinkingLevel => _options.DefaultThinkingLevel;
}