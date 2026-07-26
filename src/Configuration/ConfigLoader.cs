using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Configuration;

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
