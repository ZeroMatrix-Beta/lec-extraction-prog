using System.Reflection;
using LectureExtraction.Configuration;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Tests;

/// <summary>
/// Guards the configuration contract during the refactor.
///
/// <para><see cref="ConfigLoader{T}"/> binds "{TypeName}.json" onto T with
/// Microsoft.Extensions.Configuration, which <b>silently ignores keys it cannot match</b>.
/// That makes a renamed config property the most dangerous kind of change in this codebase:
/// it produces no build error and no runtime error, the user's saved setting simply stops
/// applying. These tests fail loudly instead.</para>
/// </summary>
public class ConfigBindingTests {
    /// <summary>Config types that <see cref="ConfigLoader{T}"/> loads from a same-named JSON file.</summary>
    public static TheoryData<Type> ConfigTypes => [
        typeof(AiStudioAutoExtractionConfig),
        typeof(VertexAutoExtractionConfig),
        typeof(DirectAiChatSessionAiStudioConfig),
        typeof(DirectAiChatSessionVertexConfig),
        typeof(LatexRefinementSessionConfig),
        typeof(FfmpegSessionConfig),
    ];

    [Theory]
    [MemberData(nameof(ConfigTypes))]
    public void EveryKeyInTheShippedJson_BindsToAPropertyOnTheConfigType(Type configType) {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, $"{configType.Name}.json");
        Assert.True(File.Exists(jsonPath), $"Expected {configType.Name}.json to be copied next to the test assembly.");

        string text = File.ReadAllText(jsonPath);
        if (string.IsNullOrWhiteSpace(text)) return;

        var root = JObject.Parse(text, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });

        AssertKeysBind(root, configType, configType.Name);
    }

    /// <summary>
    /// Walks the JSON object graph alongside the CLR type graph and asserts every JSON key
    /// has a settable counterpart. Recurses into nested objects (e.g. the refinement steps).
    /// </summary>
    private static void AssertKeysBind(JObject json, Type type, string path) {
        foreach (var property in json.Properties()) {
            PropertyInfo? clrProperty = type.GetProperty(
                property.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            Assert.True(
                clrProperty is not null,
                $"'{path}.{property.Name}' exists in the JSON but has no public property on {type.Name}. " +
                "A renamed or removed config property silently drops the user's saved setting.");

            Assert.True(
                clrProperty!.CanWrite,
                $"'{path}.{property.Name}' maps to a read-only property on {type.Name}, so it can never be bound.");

            // Recurse into nested configuration objects, but not into collections or scalars.
            if (property.Value is JObject nested
                && clrProperty.PropertyType.IsClass
                && clrProperty.PropertyType != typeof(string)
                && !clrProperty.PropertyType.IsArray
                && clrProperty.PropertyType.Namespace?.StartsWith("System") != true) {
                AssertKeysBind(nested, clrProperty.PropertyType, $"{path}.{property.Name}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ConfigTypes))]
    public void ConfigLoader_LoadsEveryShippedConfig_WithoutThrowing(Type configType) {
        // ConfigLoader<T> is generic and static, so it has to be invoked reflectively here.
        object? loaded = typeof(ConfigLoader<>)
            .MakeGenericType(configType)
            .GetMethod("Load", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [null]);

        Assert.NotNull(loaded);
        Assert.IsType(configType, loaded);
    }

    [Fact]
    public void AppSettings_AppConfigSection_BindsToAppConfigOptions() {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(jsonPath), "Expected appsettings.json next to the test assembly.");

        var root = JObject.Parse(File.ReadAllText(jsonPath), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
        var appConfigSection = root["AppConfig"] as JObject;
        Assert.NotNull(appConfigSection);

        // Only the scalar settings are checked: the trailing per-session keys in this section
        // are placeholders bound by ConfigLoader<T> against their own types, not AppConfigOptions.
        foreach (var property in appConfigSection!.Properties()) {
            if (property.Value.Type is JTokenType.Object or JTokenType.Null) continue;

            PropertyInfo? clrProperty = typeof(AppConfigOptions).GetProperty(
                property.Name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            Assert.True(
                clrProperty is not null,
                $"'AppConfig.{property.Name}' exists in appsettings.json but has no property on AppConfigOptions.");
        }
    }

    [Fact]
    public void ConfigMigrator_MigratesLegacyFlatKeys_ToNestedSections() {
        var legacyJson = new JObject {
            ["Temperature"] = 0.42f,
            ["TopP"] = 0.95f,
            ["Model"] = new JArray("gemini-3.6-flash", "gemini-3.5-flash"),
            ["CurrentModelIndex"] = 1,
            ["UseContextCaching"] = true,
            ["ContextCachingMinutes"] = 30,
            ["ProjectId"] = "my-project",
            ["ActiveApiProfile"] = 2,
            ["SourceFolder"] = @"C:\lectures"
        };

        bool migrated = ConfigMigrator.Migrate(legacyJson);

        Assert.True(migrated);
        Assert.NotNull(legacyJson["Generation"]);
        Assert.Equal(0.42f, legacyJson["Generation"]!["Temperature"]!.Value<float>());

        // The nested section is named after the config class's PROPERTY (ModelSelection), not the
        // legacy JSON key (Model) - the binder matches by property name, and `Model` survives on
        // the class only as a [JsonIgnore] delegating string[]. Asserting "Model" here previously
        // passed while the migrated value bound to nothing.
        Assert.NotNull(legacyJson["ModelSelection"]);
        Assert.Equal(1, legacyJson["ModelSelection"]!["CurrentIndex"]!.Value<int>());
        Assert.Equal(2, (legacyJson["ModelSelection"]!["Available"] as JArray)!.Count);

        Assert.NotNull(legacyJson["ContextCaching"]);
        Assert.True(legacyJson["ContextCaching"]!["Enabled"]!.Value<bool>());

        Assert.NotNull(legacyJson["Endpoint"]);
        Assert.Equal("my-project", legacyJson["Endpoint"]!["ProjectId"]!.Value<string>());

        Assert.NotNull(legacyJson["ApiKey"]);
        Assert.Equal(2, legacyJson["ApiKey"]!["ActiveProfile"]!.Value<int>());

        Assert.NotNull(legacyJson["Paths"]);
        Assert.Equal(@"C:\lectures", legacyJson["Paths"]!["SourceFolder"]!.Value<string>());
    }

    [Fact]
    public void ClearCollectionsRecursively_ClearsInitializedCollections() {
        var dummy = new AiStudioAutoExtractionConfig {
            Model = ["a", "b"]
        };
        Assert.True(dummy.Model.Length > 0);

        typeof(ConfigLoader<AiStudioAutoExtractionConfig>)
            .GetMethod("ClearCollectionsRecursively", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [dummy]);

        Assert.Empty(dummy.Model);
    }

    [Fact]
    public void ModelSelection_SelectOrAdd_AppendsNewModel_WithoutOverwritingExisting() {
        var selection = new ModelSelection {
            Available = ["gemini-3.6-flash", "gemini-3.5-flash"],
            CurrentIndex = 0
        };

        // 1. Setting an existing model selects its index without mutating array
        selection.Current = "gemini-3.5-flash";
        Assert.Equal(1, selection.CurrentIndex);
        Assert.Equal(2, selection.Available.Length);
        Assert.Equal("gemini-3.6-flash", selection.Available[0]);
        Assert.Equal("gemini-3.5-flash", selection.Available[1]);

        // 2. Setting a new freetext model appends it and updates CurrentIndex
        selection.Current = "gemma-2-9b";
        Assert.Equal(2, selection.CurrentIndex);
        Assert.Equal(3, selection.Available.Length);
        Assert.Equal("gemini-3.6-flash", selection.Available[0]);
        Assert.Equal("gemini-3.5-flash", selection.Available[1]);
        Assert.Equal("gemma-2-9b", selection.Available[2]);
        Assert.Equal("gemma-2-9b", selection.Current);
    }
}


