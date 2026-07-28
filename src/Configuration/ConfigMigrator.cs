using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Idempotently migrates flat legacy JSON configuration properties into their structured, composite sub-objects
/// (Generation, Model, ContextCaching, Endpoint, ApiKey, Sources, Paths) while providing comment remapping for JsonCommentPreserver.
/// [Human] Migriert alte flache JSON-Konfigurationen in die neue strukturierte Form (mit Kommentar-Erhaltung).
/// </summary>
public static class ConfigMigrator {
    /// <summary>
    /// [AI Context] Inspects the raw JObject read from disk and moves legacy top-level keys into nested objects for the target config type.
    /// [Human] Prüft das rohe JSON-Objekt und verschiebt alte flache Schlüssel in die neuen Unter-Objekte des Zieltyps.
    /// </summary>
    public static bool Migrate(JObject root, Type? configType = null) {
        if (root == null) return false;
        bool migratedAny = false;

        bool isAiStudioAuto = configType == typeof(AiStudioAutoExtractionConfig);
        bool isVertexAuto = configType == typeof(VertexAutoExtractionConfig);
        bool isDirectStudio = configType == typeof(DirectAiChatSessionAiStudioConfig);
        bool isDirectVertex = configType == typeof(DirectAiChatSessionVertexConfig);

        bool isExtraction = isAiStudioAuto || isVertexAuto;
        bool isDirectChat = isDirectStudio || isDirectVertex;
        bool isStudio = isAiStudioAuto || isDirectStudio;
        bool isVertex = isVertexAuto || isDirectVertex;

        // 1. Generation Parameters (for AutoExtraction configs where generation params were flat at root)
        if (configType == null || isExtraction) {
            if (root["Generation"] == null) {
                var gen = new JObject();
                migratedAny |= MovePropertyIfExists(root, "Temperature", gen, "Temperature");
                migratedAny |= MovePropertyIfExists(root, "TopP", gen, "TopP");
                migratedAny |= MovePropertyIfExists(root, "TopK", gen, "TopK");
                migratedAny |= MovePropertyIfExists(root, "MaxOutputTokens", gen, "MaxOutputTokens");
                migratedAny |= MovePropertyIfExists(root, "ThinkingBudget", gen, "ThinkingBudget");
                migratedAny |= MovePropertyIfExists(root, "ThinkingLevel", gen, "ThinkingLevel");

                if (gen.Count > 0) {
                    root["Generation"] = gen;
                }
            }
        }

        // 2. Model Selection (for AutoExtraction and DirectChat configs)
        // [AI Context] The target key must match the config class's PROPERTY name (ModelSelection),
        // not the legacy JSON key (Model). Microsoft.Extensions.Configuration binds by property
        // name and silently ignores what it cannot match, and `Model` still exists on the class as
        // a [JsonIgnore] string[] delegating property - so writing the nested object back under
        // "Model" bound nothing, leaving the user's model list at its C# default. Covered by
        // ConfigMigratorTests.LegacyModelArrayAndIndex_SurviveMigrationAndBinding.
        if (configType == null || isExtraction || isDirectChat) {
            if (root["ModelSelection"] == null && root["Model"] is JArray legacyModelArray) {
                var modelObj = new JObject {
                    ["Available"] = legacyModelArray.DeepClone()
                };
                if (root["CurrentModelIndex"] != null) {
                    modelObj["CurrentIndex"] = root["CurrentModelIndex"]!.DeepClone();
                    root.Remove("CurrentModelIndex");
                }
                root.Remove("Model");
                root["ModelSelection"] = modelObj;
                migratedAny = true;
            }
        }

        // 3. Context Caching (for VertexAutoExtractionConfig where caching params were flat at root)
        if (configType == null || isVertexAuto) {
            if (root["ContextCaching"] == null) {
                var cache = new JObject();
                migratedAny |= MovePropertyIfExists(root, "UseContextCaching", cache, "Enabled");
                migratedAny |= MovePropertyIfExists(root, "ContextCachingMinutes", cache, "Minutes");
                migratedAny |= MovePropertyIfExists(root, "ContextCachingIncrementMinutes", cache, "IncrementMinutes");
                migratedAny |= MovePropertyIfExists(root, "ContextCachingMinimumRemainingMinutes", cache, "MinimumRemainingMinutes");

                if (cache.Count > 0) {
                    root["ContextCaching"] = cache;
                }
            }
        }

        // 4. Vertex Endpoint (only for Vertex types)
        if (configType == null || isVertex) {
            if (root["Endpoint"] == null) {
                var endpoint = new JObject();
                migratedAny |= MovePropertyIfExists(root, "ProjectId", endpoint, "ProjectId");
                migratedAny |= MovePropertyIfExists(root, "Location", endpoint, "Location");
                migratedAny |= MovePropertyIfExists(root, "GcsBucketName", endpoint, "GcsBucketName");

                if (endpoint.Count > 0) {
                    root["Endpoint"] = endpoint;
                }
            }
        }

        // 5. API Key Profile (only for AI Studio types)
        if (configType == null || isStudio) {
            if (root["ApiKey"] == null) {
                var apiKey = new JObject();
                migratedAny |= MovePropertyIfExists(root, "ActiveApiProfile", apiKey, "ActiveProfile");
                migratedAny |= MovePropertyIfExists(root, "AiStudioApiKeyEnvNames", apiKey, "EnvNames");

                if (apiKey.Count > 0) {
                    root["ApiKey"] = apiKey;
                }
            }
        }

        // 6. Context Sources (for AutoExtraction and DirectChat configs)
        if (configType == null || isExtraction || isDirectChat) {
            if (root["Sources"] == null) {
                var sources = new JObject();
                migratedAny |= MovePropertyIfExists(root, "SystemInstructionPaths", sources, "SystemInstructionPaths");
                migratedAny |= MovePropertyIfExists(root, "SystemInstructionPath", sources, "SystemInstructionPaths");
                migratedAny |= MovePropertyIfExists(root, "HistoryPreloadPaths", sources, "HistoryPreloadPaths");

                if (sources.Count > 0) {
                    root["Sources"] = sources;
                }
            }
        }

        // 7. Workspace Paths (for AutoExtraction and DirectChat configs)
        if (configType == null || isExtraction || isDirectChat) {
            if (root["Paths"] == null) {
                var paths = new JObject();
                migratedAny |= MovePropertyIfExists(root, "SourceFolder", paths, "SourceFolder");
                migratedAny |= MovePropertyIfExists(root, "PredefinedSourceFolders", paths, "PredefinedSourceFolders");
                migratedAny |= MovePropertyIfExists(root, "TargetFolder", paths, "TargetFolder");
                migratedAny |= MovePropertyIfExists(root, "LogFolder", paths, "LogFolder");
                migratedAny |= MovePropertyIfExists(root, "UploadFolder", paths, "UploadFolder");

                if (paths.Count > 0) {
                    root["Paths"] = paths;
                }
            }
        }

        return migratedAny;
    }

    private static bool MovePropertyIfExists(JObject source, string sourceKey, JObject target, string targetKey) {
        var prop = source.Property(sourceKey);
        if (prop != null) {
            target[targetKey] = prop.Value.DeepClone();
            source.Remove(sourceKey);
            return true;
        }
        return false;
    }

    /// <summary>
    /// [AI Context] Remaps AnchoredComment positions when migrating property locations for JsonCommentPreserver.Merge.
    /// [Human] Bezieht alte Kommentaranker auf die neuen verschachtelten JSON-Pfade.
    /// </summary>
    internal static JsonCommentPreserver.AnchoredComment? RemapAnchor(JsonCommentPreserver.AnchoredComment anchor, JObject updated) {
        if (anchor.ContainerPath.Count != 0 || anchor.BeforePropertyKey == null) return anchor;

        string key = anchor.BeforePropertyKey;

        if (key is "Temperature" or "TopP" or "TopK" or "MaxOutputTokens" or "ThinkingBudget" or "ThinkingLevel") {
            if (updated["Generation"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "Generation" }, key, anchor.CommentLines);
        }

        if (key is "Model") {
            if (updated["ModelSelection"] is JObject)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ModelSelection" }, "Available", anchor.CommentLines);
        }

        if (key is "CurrentModelIndex") {
            if (updated["ModelSelection"] is JObject)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ModelSelection" }, "CurrentIndex", anchor.CommentLines);
        }

        if (key is "UseContextCaching") {
            if (updated["ContextCaching"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ContextCaching" }, "Enabled", anchor.CommentLines);
        }

        if (key is "ContextCachingMinutes") {
            if (updated["ContextCaching"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ContextCaching" }, "Minutes", anchor.CommentLines);
        }

        if (key is "ContextCachingIncrementMinutes") {
            if (updated["ContextCaching"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ContextCaching" }, "IncrementMinutes", anchor.CommentLines);
        }

        if (key is "ContextCachingMinimumRemainingMinutes") {
            if (updated["ContextCaching"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ContextCaching" }, "MinimumRemainingMinutes", anchor.CommentLines);
        }

        if (key is "ProjectId" or "Location" or "GcsBucketName") {
            if (updated["Endpoint"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "Endpoint" }, key, anchor.CommentLines);
        }

        if (key is "ActiveApiProfile") {
            if (updated["ApiKey"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ApiKey" }, "ActiveProfile", anchor.CommentLines);
        }

        if (key is "AiStudioApiKeyEnvNames") {
            if (updated["ApiKey"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "ApiKey" }, "EnvNames", anchor.CommentLines);
        }

        if (key is "SystemInstructionPaths" or "SystemInstructionPath" or "HistoryPreloadPaths") {
            if (updated["Sources"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "Sources" }, key == "SystemInstructionPath" ? "SystemInstructionPaths" : key, anchor.CommentLines);
        }

        if (key is "SourceFolder" or "PredefinedSourceFolders" or "TargetFolder" or "LogFolder" or "UploadFolder") {
            if (updated["Paths"] != null)
                return new JsonCommentPreserver.AnchoredComment(new[] { "Paths" }, key, anchor.CommentLines);
        }

        return anchor;
    }
}
