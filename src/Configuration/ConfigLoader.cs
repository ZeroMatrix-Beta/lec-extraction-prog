using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Configuration;

/// <summary>
/// Generic configuration loader implementing the hierarchy:
/// corresponding .json > appconfig.json > C# static variable > C# app static
/// </summary>
public static partial class ConfigLoader<T> where T : class, new() {
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

                // Comments that live inside a JSON *array* survive the parse/merge above as
                // ordinary JArray children (see UpdatePropertiesPreservingComments). Comments
                // that live directly inside a JSON *object* do not - Newtonsoft's JObject parser
                // discards them before they ever reach `job`. Recover those from the original
                // text and splice them back into the freshly serialized output.
                var orphanObjectComments = ExtractOrphanObjectComments(existingJson);
                string serialized = job.ToString(Formatting.Indented);
                return ReinsertOrphanObjectComments(serialized, orphanObjectComments);
            }
            catch (Exception ex) {
                Console.WriteLine($"\n[AppConfig Warnung] Kommentare in '{Path.GetFileName(filePath)}' konnten beim Speichern nicht erhalten werden. Art der Exception: {ex.GetType().Name}, Fehler: {ex.Message}");
            }
        }
        return JsonConvert.SerializeObject(config, Formatting.Indented);
    }

    /// <summary>
    /// [AI Context] One `// ...` or `/* ... */` comment that was found directly inside a JSON
    /// object (not inside an array) in the original file, together with where it needs to be
    /// re-inserted after re-serialization: either immediately above the named property
    /// (<see cref="BeforePropertyKey"/> set) or immediately above the closing brace of its
    /// containing object (<see cref="BeforePropertyKey"/> null, i.e. a trailing comment).
    /// <see cref="ContainerPath"/> is the chain of property names from the JSON root down to the
    /// object the comment lives in, e.g. `["Refinement", "Step1"]`.
    /// [Human] Ein Kommentar, der direkt in einem JSON-Objekt stand (nicht in einem Array),
    /// zusammen mit der Stelle, an der er nach dem Neu-Serialisieren wieder eingefügt werden muss.
    /// </summary>
    private readonly record struct AnchoredComment(IReadOnlyList<string> ContainerPath, string? BeforePropertyKey, IReadOnlyList<string> CommentLines);

    [GeneratedRegex("""^(?<indent>\s*)"(?<key>(?:[^"\\]|\\.)*)"\s*:\s*(?<rest>.*)$""")]
    private static partial Regex PropertyDeclarationLineRegex();

    [GeneratedRegex(@"^\s*[}\]],?\s*$")]
    private static partial Regex ContainerClosingLineRegex();

    [GeneratedRegex(@"^\s*(//.*|/\*.*\*/)\s*$")]
    private static partial Regex CommentOnlyLineRegex();

    /// <summary>
    /// [AI Context] Scans the original JSON text line by line, tracking the current object/array
    /// nesting path with a simple stack. Comment-only lines are buffered; once buffered comments
    /// are followed by a property declaration or a closing brace, they are anchored to that
    /// property (or to "end of this container"). Comments encountered while inside an array
    /// ancestor are skipped entirely - those already round-trip via the JArray comment children
    /// that <see cref="UpdatePropertiesPreservingComments"/> preserves.
    /// [Human] Liest den ursprünglichen JSON-Text zeilenweise, merkt sich den aktuellen Pfad
    /// (Stack aus Property-Namen) und ordnet gepufferte Kommentare der jeweils nächsten
    /// Eigenschaft (oder dem Ende des umschließenden Objekts) zu. Kommentare innerhalb eines
    /// Arrays werden hier ignoriert, da sie bereits über die JArray-Kindelemente erhalten bleiben.
    /// </summary>
    private static List<AnchoredComment> ExtractOrphanObjectComments(string originalJson) {
        var anchors = new List<AnchoredComment>();
        var pathStack = new Stack<string>();
        var suppressedStack = new Stack<bool>();
        bool suppressed = false;
        var pendingComments = new List<string>();

        foreach (string rawLine in originalJson.Split('\n')) {
            string line = rawLine.TrimEnd('\r');

            if (CommentOnlyLineRegex().IsMatch(line)) {
                if (!suppressed) pendingComments.Add(line.Trim());
                continue;
            }

            if (ContainerClosingLineRegex().IsMatch(line)) {
                if (!suppressed && pendingComments.Count > 0) {
                    anchors.Add(new AnchoredComment(pathStack.Reverse().ToList(), null, [.. pendingComments]));
                }
                pendingComments.Clear();
                if (pathStack.Count > 0) pathStack.Pop();
                if (suppressedStack.Count > 0) suppressed = suppressedStack.Pop();
                continue;
            }

            var propertyMatch = PropertyDeclarationLineRegex().Match(line);
            if (propertyMatch.Success) {
                string key = propertyMatch.Groups["key"].Value;
                string rest = propertyMatch.Groups["rest"].Value.TrimEnd();

                if (!suppressed && pendingComments.Count > 0) {
                    anchors.Add(new AnchoredComment(pathStack.Reverse().ToList(), key, [.. pendingComments]));
                }
                pendingComments.Clear();

                if (rest is "{" or "[") {
                    pathStack.Push(key);
                    suppressedStack.Push(suppressed);
                    if (rest == "[") suppressed = true;
                }
                continue;
            }

            // Anything else (an array element, the root's opening brace, a blank line) cannot
            // anchor a comment reliably, so whatever was pending is orphaned and dropped.
            pendingComments.Clear();
        }

        return anchors;
    }

    /// <summary>
    /// [AI Context] Mirror-image of <see cref="ExtractOrphanObjectComments"/>: walks the freshly
    /// serialized JSON text with the same path-tracking logic and inserts each anchored comment's
    /// lines, re-indented to match, immediately above the line it was anchored to.
    /// [Human] Fügt die zuvor extrahierten Kommentare an der passenden Stelle im neu
    /// serialisierten JSON-Text wieder ein, mit an die Zieleinrückung angepasstem Whitespace.
    /// </summary>
    private static string ReinsertOrphanObjectComments(string serializedJson, List<AnchoredComment> anchors) {
        if (anchors.Count == 0) return serializedJson;

        var lines = serializedJson.Split('\n').ToList();
        var pathStack = new Stack<string>();

        for (int i = 0; i < lines.Count; i++) {
            string line = lines[i].TrimEnd('\r');

            if (ContainerClosingLineRegex().IsMatch(line)) {
                var currentPath = pathStack.Reverse().ToList();
                int matchIndex = anchors.FindIndex(a => a.BeforePropertyKey is null && PathsEqual(a.ContainerPath, currentPath));
                if (matchIndex >= 0) {
                    i += InsertCommentLines(lines, i, line, anchors[matchIndex].CommentLines);
                    anchors.RemoveAt(matchIndex);
                }
                if (pathStack.Count > 0) pathStack.Pop();
                continue;
            }

            var propertyMatch = PropertyDeclarationLineRegex().Match(line);
            if (propertyMatch.Success) {
                string key = propertyMatch.Groups["key"].Value;
                string rest = propertyMatch.Groups["rest"].Value.TrimEnd();
                var currentPath = pathStack.Reverse().ToList();

                int matchIndex = anchors.FindIndex(a => a.BeforePropertyKey == key && PathsEqual(a.ContainerPath, currentPath));
                if (matchIndex >= 0) {
                    i += InsertCommentLines(lines, i, line, anchors[matchIndex].CommentLines);
                    anchors.RemoveAt(matchIndex);
                }

                if (rest is "{" or "[") pathStack.Push(key);
            }
        }

        return string.Join('\n', lines);
    }

    private static int InsertCommentLines(List<string> lines, int index, string anchorLine, IReadOnlyList<string> commentLines) {
        string indent = anchorLine[..(anchorLine.Length - anchorLine.TrimStart().Length)];
        lines.InsertRange(index, commentLines.Select(c => indent + c));
        return commentLines.Count;
    }

    private static bool PathsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b) => a.SequenceEqual(b);

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
