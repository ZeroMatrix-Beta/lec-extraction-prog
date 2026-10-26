using LectureExtraction.Configuration;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Tests;

/// <summary>
/// Guards how a JSON layer binds onto a config object's lists. The binder alone appends a JSON array
/// to the list already there and binds nothing for an empty one; the loader used to compensate by
/// emptying every list first, which threw away every list default a JSON file did not repeat.
/// </summary>
public class ConfigLayerBinderTests {
    public sealed class Sample {
        public string[] Array { get; set; } = ["default"];
        public List<string> List { get; set; } = ["default"];
        public Nested Inner { get; set; } = new();
    }

    public sealed class Nested {
        public string[] Values { get; set; } = ["default"];
        public string[] Others { get; set; } = ["default"];
    }

    private static IConfiguration Json(string json) =>
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();

    [Fact]
    public void A_list_the_json_sets_replaces_the_default_instead_of_appending() {
        var sample = new Sample();

        ConfigLayerBinder.Bind(Json("""{ "Array": ["a", "b"], "List": ["c"] }"""), sample);

        Assert.Equal(["a", "b"], sample.Array);
        Assert.Equal(["c"], sample.List);
    }

    [Fact]
    public void A_list_the_json_leaves_out_keeps_the_default() {
        var sample = new Sample();

        ConfigLayerBinder.Bind(Json("""{ "Array": ["a"] }"""), sample);

        Assert.Equal(["default"], sample.List);
        Assert.Equal(["default"], sample.Inner.Values);
    }

    [Fact]
    public void An_empty_json_list_clears_the_default() {
        var sample = new Sample();

        ConfigLayerBinder.Bind(Json("""{ "Array": [], "List": [] }"""), sample);

        Assert.Empty(sample.Array);
        Assert.Empty(sample.List);
    }

    [Fact]
    public void Nested_lists_are_replaced_only_where_the_json_names_them() {
        var sample = new Sample();

        ConfigLayerBinder.Bind(Json("""{ "Inner": { "Values": ["x"] } }"""), sample);

        Assert.Equal(["x"], sample.Inner.Values);
        Assert.Equal(["default"], sample.Inner.Others);
    }

    [Fact]
    public void A_later_layer_keeps_the_lists_an_earlier_layer_set_and_it_leaves_out() {
        var sample = new Sample();

        ConfigLayerBinder.Bind(Json("""{ "Array": ["global"], "List": ["global"] }"""), sample);
        ConfigLayerBinder.Bind(Json("""{ "List": ["session"] }"""), sample);

        Assert.Equal(["global"], sample.Array);
        Assert.Equal(["session"], sample.List);
    }

    [Fact]
    public void The_reference_slimming_defaults_survive_a_json_that_sets_only_the_preceding_part() {
        // The class documents OlderParts' default as the 2026-10-16 behaviour; it used to be emptied
        // on load whenever the JSON did not repeat it.
        var config = new AiStudioAutoExtractionConfig();

        ConfigLayerBinder.Bind(Json("""{ "ReferenceSlimming": { "PrecedingPart": { "KeepCommentPrefixes": ["% Lecturer(s):"] } } }"""), config);

        Assert.Equal(["tikzpicture"], config.ReferenceSlimming.OlderParts.RemoveEnvironments);
        Assert.Equal(["speech"], config.ReferenceSlimming.OlderParts.EmptyEnvironments);
        Assert.Equal(["% Lecturer(s):"], config.ReferenceSlimming.PrecedingPart.KeepCommentPrefixes);
    }

    [Fact]
    public void A_legacy_flat_model_list_replaces_the_default_through_its_delegating_property() {
        var config = new VertexAutoExtractionConfig();

        ConfigLayerBinder.Bind(Json("""{ "Model": ["gemini-x"] }"""), config);

        Assert.Equal(["gemini-x"], config.ModelSelection.Available);
    }

    [Fact]
    public void The_shipped_model_list_is_loaded_as_written() {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, $"{nameof(AiStudioAutoExtractionConfig)}.json"));
        var expected = JObject.Parse(json, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore })["ModelSelection"]!["Available"]!
            .Select(token => token.Value<string>()!).ToArray();
        var config = new AiStudioAutoExtractionConfig();

        ConfigLayerBinder.Bind(new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, $"{nameof(AiStudioAutoExtractionConfig)}.json")).Build(), config);

        Assert.Equal(expected, config.ModelSelection.Available);
    }
}

/// <summary>
/// Loads every config type through <see cref="ConfigLoader{T}"/> from a folder with no JSON at all:
/// the result must be exactly the class defaults, lists included. Joins the console collection
/// because <see cref="ConfigStore"/> is a process-wide global.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ConfigLoaderDefaultsTests : IDisposable {
    private readonly string _emptyDirectory = Path.Combine(Path.GetTempPath(), "lecx-empty-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigLoaderDefaultsTests() => Directory.CreateDirectory(_emptyDirectory);

    public void Dispose() {
        ConfigStore.Reset();
        try {
            Directory.Delete(_emptyDirectory, recursive: true);
        }
        catch (Exception ex) {
            // A leftover temp folder must not fail the suite, but it should be visible.
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Theory]
    [MemberData(nameof(ConfigBindingTests.ConfigTypes), MemberType = typeof(ConfigBindingTests))]
    public void Loading_without_a_json_file_gives_the_class_defaults(Type configType) {
        ConfigStore.DirectoryOverride = _emptyDirectory;

        object loaded = typeof(ConfigLoader<>).MakeGenericType(configType)
            .GetMethod("Load")!
            .Invoke(null, [null])!;
        object defaults = Activator.CreateInstance(configType)!;

        Assert.Equal(JObject.FromObject(defaults).ToString(), JObject.FromObject(loaded).ToString());
    }

    [Fact]
    public void A_fresh_install_has_a_model_to_run() {
        ConfigStore.DirectoryOverride = _emptyDirectory;

        var config = ConfigLoader<AiStudioAutoExtractionConfig>.Load();

        Assert.Equal(ModelCatalog.Default, config.ModelSelection.Available);
        Assert.Equal(ModelCatalog.Default[0], config.CurrentModel);
    }
}
