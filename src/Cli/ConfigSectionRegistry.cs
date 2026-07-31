using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;
using LectureExtraction.Configuration;

namespace LectureExtraction.Cli;

/// <summary>
/// Reaches the <c>ConfigLoader&lt;T&gt;</c> family by name. The loader is a static generic, so a
/// command that is handed the string "AiStudioAutoExtractionConfig" cannot call it directly; this
/// closes the generic by reflection. Section names are deliberately the type names, because
/// <c>ConfigLoader</c> derives the JSON file name from the type name too - so the section a user
/// types is the file they see on disk.
/// </summary>
public static class ConfigSectionRegistry {
    private static readonly Dictionary<string, Type> Sections = new(StringComparer.OrdinalIgnoreCase) {
        [nameof(AiStudioAutoExtractionConfig)] = typeof(AiStudioAutoExtractionConfig),
        [nameof(VertexAutoExtractionConfig)] = typeof(VertexAutoExtractionConfig),
        [nameof(LatexRefinementSessionConfig)] = typeof(LatexRefinementSessionConfig),
        [nameof(FfmpegSessionConfig)] = typeof(FfmpegSessionConfig),
        [nameof(DirectAiChatSessionAiStudioConfig)] = typeof(DirectAiChatSessionAiStudioConfig),
        [nameof(DirectAiChatSessionVertexConfig)] = typeof(DirectAiChatSessionVertexConfig),
        [nameof(SessionLoggerConfig)] = typeof(SessionLoggerConfig)
    };

    public static IReadOnlyCollection<string> Names => [.. Sections.Keys.OrderBy(name => name)];

    public static bool TryResolve(string name, out Type sectionType) => Sections.TryGetValue(name, out sectionType!);

    /// <summary>Loads one section through the ordinary <c>ConfigLoader</c> hierarchy.</summary>
    public static object Load(Type sectionType) {
        var loaderType = typeof(ConfigLoader<>).MakeGenericType(sectionType);
        var load = loaderType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"ConfigLoader<{sectionType.Name}>.Load not found.");

        return load.Invoke(null, [null])
            ?? throw new InvalidOperationException($"ConfigLoader<{sectionType.Name}>.Load returned null.");
    }

    public static object Load(string sectionName) =>
        TryResolve(sectionName, out var type)
            ? Load(type)
            : throw new ArgumentException($"Unknown config section '{sectionName}'.", nameof(sectionName));

    /// <summary>Persists one section through the ordinary <c>ConfigLoader</c> save path.</summary>
    public static void Save(Type sectionType, object config) {
        var loaderType = typeof(ConfigLoader<>).MakeGenericType(sectionType);
        var save = loaderType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"ConfigLoader<{sectionType.Name}>.Save not found.");

        save.Invoke(null, [config]);
    }

    /// <summary>
    /// Sets a dotted path from its text form. Only scalars are supported: an array or a nested
    /// object has no unambiguous single-value spelling on a command line, and guessing one (comma
    /// splitting, JSON fragments) would make <c>config set</c> the wrong tool for editing a list.
    /// </summary>
    public static bool TryWritePath(object root, string dottedPath, string rawValue, out string? error) {
        error = null;

        string[] segments = dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        object? current = root;

        for (int i = 0; i < segments.Length - 1; i++) {
            var step = current?.GetType().GetProperty(segments[i], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (step == null) {
                error = $"'{segments[i]}' is not a property of {current?.GetType().Name}.";
                return false;
            }
            current = step.GetValue(current);
        }

        string leafName = segments[^1];
        var leaf = current?.GetType().GetProperty(leafName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (leaf == null) {
            error = $"'{leafName}' is not a property of {current?.GetType().Name}.";
            return false;
        }

        if (!leaf.CanWrite) {
            error = $"'{leafName}' is read-only.";
            return false;
        }

        var target = Nullable.GetUnderlyingType(leaf.PropertyType) ?? leaf.PropertyType;
        if (target.IsArray || (!target.IsPrimitive && !target.IsEnum && target != typeof(string) && target != typeof(decimal))) {
            error = $"'{leafName}' is a {target.Name}; only scalar values can be set from the command line.";
            return false;
        }

        try {
            object converted = target.IsEnum
                ? Enum.Parse(target, rawValue, ignoreCase: true)
                : Convert.ChangeType(rawValue, target, CultureInfo.InvariantCulture);
            leaf.SetValue(current, converted);
        }
        catch (Exception ex) {
            error = $"cannot convert '{rawValue}' to {target.Name} ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Walks a dotted property path such as <c>Paths.SourceFolder</c>. The flat delegating
    /// properties (<c>SourceFolder</c>) are <c>[JsonIgnore]</c> but still ordinary properties, so
    /// both the nested path and its flat alias resolve - whichever the caller happens to know.
    /// </summary>
    public static bool TryReadPath(object root, string dottedPath, out object? value, out string? error) {
        value = null;
        error = null;
        object? current = root;

        foreach (string segment in dottedPath.Split('.', StringSplitOptions.RemoveEmptyEntries)) {
            if (current == null) {
                error = $"'{segment}' cannot be read because its parent is null.";
                return false;
            }

            var property = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null) {
                error = $"'{segment}' is not a property of {current.GetType().Name}.";
                return false;
            }

            current = property.GetValue(current);
        }

        value = current;
        return true;
    }
}
