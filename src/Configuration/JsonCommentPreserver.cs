using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Keeps the hand-written <c>//</c> and <c>/* */</c> comments in the shipped
/// <c>*.json</c> config files alive across the round-trips that <see cref="ConfigLoader{T}"/>
/// performs whenever the running app persists an option change.
///
/// Two distinct mechanisms are needed, because Newtonsoft treats the two comment positions
/// differently:
/// <list type="bullet">
/// <item>Comments inside a JSON <b>array</b> survive parsing as ordinary <see cref="JArray"/>
/// children, so <see cref="UpdatePropertiesPreservingComments"/> only has to avoid discarding
/// them while it replaces the array's real elements.</item>
/// <item>Comments inside a JSON <b>object</b> are dropped by the parser before they ever reach a
/// <see cref="JObject"/>. Those have to be recovered from the original text
/// (<see cref="ExtractOrphanObjectComments"/>) and spliced back into the freshly serialized
/// output (<see cref="ReinsertOrphanObjectComments"/>).</item>
/// </list>
///
/// Extracted (Phase 8) from the generic <c>ConfigLoader&lt;T&gt;</c>, where this logic was
/// private and therefore re-instantiated per closed generic type and unreachable from
/// <see cref="ConfigMigrator"/>. The migrator needs the anchor representation directly, so it
/// can relocate a comment along with the property it annotates when that property moves into a
/// nested section.
/// [Human] Sorgt dafür, dass Kommentare in den JSON-Konfigurationsdateien erhalten bleiben,
/// wenn die App eine Einstellung zurückschreibt.
/// </summary>
internal static partial class JsonCommentPreserver {
    /// <summary>
    /// [AI Context] One <c>// ...</c> or <c>/* ... */</c> comment that was found directly inside a JSON
    /// object (not inside an array) in the original file, together with where it needs to be
    /// re-inserted after re-serialization: either immediately above the named property
    /// (<see cref="BeforePropertyKey"/> set) or immediately above the closing brace of its
    /// containing object (<see cref="BeforePropertyKey"/> null, i.e. a trailing comment).
    /// <see cref="ContainerPath"/> is the chain of property names from the JSON root down to the
    /// object the comment lives in, e.g. <c>["Refinement", "Step1"]</c>.
    /// [Human] Ein Kommentar, der direkt in einem JSON-Objekt stand (nicht in einem Array),
    /// zusammen mit der Stelle, an der er nach dem Neu-Serialisieren wieder eingefügt werden muss.
    /// </summary>
    internal readonly record struct AnchoredComment(IReadOnlyList<string> ContainerPath, string? BeforePropertyKey, IReadOnlyList<string> CommentLines);

    [GeneratedRegex("""^(?<indent>\s*)"(?<key>(?:[^"\\]|\\.)*)"\s*:\s*(?<rest>.*)$""")]
    private static partial Regex PropertyDeclarationLineRegex();

    [GeneratedRegex(@"^\s*[}\]],?\s*$")]
    private static partial Regex ContainerClosingLineRegex();

    [GeneratedRegex(@"^\s*(//.*|/\*.*\*/)\s*$")]
    private static partial Regex CommentOnlyLineRegex();

    /// <summary>
    /// [AI Context] The whole round-trip in one call: takes the original file text and the
    /// freshly serialized object graph, and returns indented JSON that carries the updated values
    /// but the original comments. <paramref name="remapAnchor"/> lets a caller relocate a comment
    /// when its property moved (used by <see cref="ConfigMigrator"/>); return the anchor unchanged
    /// to keep it where it was, or <c>null</c> to drop it.
    /// [Human] Führt den kompletten Vorgang aus: aktualisierte Werte, ursprüngliche Kommentare.
    /// </summary>
    internal static string Merge(string existingJson, JObject updated, Func<AnchoredComment, JObject, AnchoredComment?>? remapAnchor = null) {
        var target = JObject.Parse(existingJson, new JsonLoadSettings { CommentHandling = CommentHandling.Load, LineInfoHandling = LineInfoHandling.Load });
        UpdatePropertiesPreservingComments(target, updated);

        var orphanObjectComments = ExtractOrphanObjectComments(existingJson);
        if (remapAnchor != null) {
            orphanObjectComments = [.. orphanObjectComments.Select(a => remapAnchor(a, updated)).Where(a => a.HasValue).Select(a => a!.Value)];
        }

        string serialized = target.ToString(Formatting.Indented);
        return ReinsertOrphanObjectComments(serialized, orphanObjectComments);
    }

    /// <summary>
    /// [AI Context] Recursively updates existing JSON properties from `source` to `target` while keeping `JTokenType.Comment` nodes in `target.Children()` intact.
    /// Unlike `JObject.Merge`, which replaces structure blocks and deletes adjacent comment nodes, this updates property values in-place so comments in .json configs survive saving.
    /// [Human] Aktualisiert JSON-Eigenschaften gezielt vor Ort, ohne dass darüber oder darunter liegende Kommentare (`// ...`) beim Speichern gelöscht werden.
    /// </summary>
    internal static void UpdatePropertiesPreservingComments(JToken target, JToken source) {
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
    internal static List<AnchoredComment> ExtractOrphanObjectComments(string originalJson) {
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
    internal static string ReinsertOrphanObjectComments(string serializedJson, List<AnchoredComment> anchors) {
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
}
