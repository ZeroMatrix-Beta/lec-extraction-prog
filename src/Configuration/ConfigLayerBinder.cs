using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] The binding step of <see cref="ConfigLoader{T}"/>, one configuration layer at a time.
/// Not generic, so the tests can call it directly and it exists once rather than per config type.
/// [Human] Bindet eine Konfigurationsebene auf ein Konfigurationsobjekt.
/// </summary>
public static class ConfigLayerBinder {
    /// <summary>
    /// [AI Context] Binds one configuration layer onto <paramref name="target"/> so that every key the
    /// layer sets wins, lists included. The binder alone gets lists wrong both ways: it appends a JSON
    /// array to the list already there (C# default [a, b] + JSON [c] = [a, b, c]), and an empty JSON
    /// array binds nothing, so [] cannot clear a default. Hence each list the layer names is emptied
    /// first, and a list it does not name keeps its value - the C# default, or an earlier layer's.
    /// This replaced a step that emptied every list before binding, which threw away all list
    /// defaults: a key missing from the JSON left its list empty instead of at the default.
    /// [Human] Bindet eine Konfigurationsebene: Listen, die in der JSON stehen, ersetzen den
    /// Standardwert; fehlt eine Liste in der JSON, bleibt der Standardwert aus der Klasse.
    /// </summary>
    public static void Bind(IConfiguration layer, object target) {
        ClearCollectionsSetBy(layer, target);
        layer.Bind(target);
    }

    /// <summary>
    /// [AI Context] Empties the arrays and lists on <paramref name="obj"/> that <paramref name="section"/>
    /// has a key for, recursing into nested config objects the section also has a key for. An empty
    /// JSON array still appears as a key (with a null value), so it counts as setting the list.
    /// [Human] Leert die Listen, die diese Konfigurationsebene setzt, damit das Binden sie ersetzt
    /// statt anzuhängen.
    /// </summary>
    private static void ClearCollectionsSetBy(IConfiguration section, object? obj) {
        if (obj == null) return;

        // Configuration keys are case-insensitive, like the binder's property matching.
        var childKeys = new HashSet<string>(section.GetChildren().Select(child => child.Key), StringComparer.OrdinalIgnoreCase);
        if (childKeys.Count == 0) return;

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || !childKeys.Contains(prop.Name)) continue;

            if (prop.PropertyType.IsArray) {
                if (prop.CanWrite) {
                    prop.SetValue(obj, Array.CreateInstance(prop.PropertyType.GetElementType()!, 0));
                }
            }
            else if (typeof(System.Collections.IList).IsAssignableFrom(prop.PropertyType)) {
                (prop.GetValue(obj) as System.Collections.IList)?.Clear();
            }
            else if (prop.PropertyType.IsClass && prop.PropertyType != typeof(string)) {
                ClearCollectionsSetBy(section.GetSection(prop.Name), prop.GetValue(obj));
            }
        }
    }
}
