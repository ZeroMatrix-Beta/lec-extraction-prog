using System.Linq;
using LectureExtraction.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Reproduces and pins the unbounded config growth reported on 2026-07-29: the predefined source
/// folder list rendered 4 unique folders twelve times, and grew again within the same run.
///
/// <para><b>The loop.</b> Every migration block is guarded by <c>if (root[Section] == null)</c>, so
/// once a nested section exists the block is skipped and a legacy flat key beside it is never
/// removed. <c>Microsoft.Extensions.Configuration</c> then binds that flat key onto the same storage
/// as the nested one — it ignores <c>[JsonIgnore]</c> and binds the delegating compatibility
/// properties by name — and it <b>appends</b> to arrays instead of replacing them. So each
/// <c>Load</c> produced nested + flat, and each <c>Save</c> wrote the longer list back to disk.
/// Four entries per launch, without bound.</para>
///
/// <para>Per this plan's own rule, these assert on what the program ends up believing after a full
/// migrate-and-bind round trip, not on the shape of the emitted JSON.</para>
/// </summary>
public class ConfigLegacyKeyCleanupTests {
    private static JObject ConfigWithBothFlatAndNestedFolders() => JObject.Parse("""
    {
      "PredefinedSourceFolders": [ "D:\\a", "D:\\b" ],
      "SourceFolder": "D:\\a",
      "Paths": {
        "SourceFolder": "D:\\b",
        "PredefinedSourceFolders": [ "D:\\a", "D:\\b" ]
      }
    }
    """);

    [Fact]
    public void A_legacy_flat_key_is_removed_once_its_nested_section_exists() {
        var root = ConfigWithBothFlatAndNestedFolders();

        bool changed = ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig));

        Assert.True(changed);
        Assert.Null(root.Property("PredefinedSourceFolders"));
        Assert.Null(root.Property("SourceFolder"));

        // The nested section keeps the values - the flat key was the duplicate, not the survivor.
        var nested = (JArray)root["Paths"]!["PredefinedSourceFolders"]!;
        Assert.Equal(["D:\\a", "D:\\b"], nested.Select(t => t.Value<string>()));
    }

    /// <summary>
    /// The defect's signature: run the migrator repeatedly, as launching the app repeatedly does,
    /// and the list must not grow. Before the fix this test would have produced 2, 4, 6, 8 entries.
    /// </summary>
    [Fact]
    public void Repeated_migration_does_not_grow_the_list() {
        var root = ConfigWithBothFlatAndNestedFolders();

        for (int launch = 0; launch < 5; launch++) {
            ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig));
        }

        var nested = (JArray)root["Paths"]!["PredefinedSourceFolders"]!;
        Assert.Equal(2, nested.Count);
    }

    [Fact]
    public void An_already_duplicated_list_is_repaired_keeping_the_users_order() {
        var root = JObject.Parse("""
        {
          "Paths": {
            "PredefinedSourceFolders": [ "D:\\a", "D:\\b", "D:\\c", "D:\\a", "D:\\b", "D:\\c" ]
          }
        }
        """);

        Assert.True(ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig)));

        var nested = (JArray)root["Paths"]!["PredefinedSourceFolders"]!;
        Assert.Equal(["D:\\a", "D:\\b", "D:\\c"], nested.Select(t => t.Value<string>()));
    }

    [Fact]
    public void Duplicated_api_key_env_names_are_repaired_too() {
        var root = JObject.Parse("""
        {
          "ApiKey": {
            "ActiveProfile": 3,
            "EnvNames": [ "KEY-0", "KEY-1", "KEY-0", "KEY-1" ]
          }
        }
        """);

        Assert.True(ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig)));

        var names = (JArray)root["ApiKey"]!["EnvNames"]!;
        Assert.Equal(["KEY-0", "KEY-1"], names.Select(t => t.Value<string>()));
        Assert.Equal(3, root["ApiKey"]!["ActiveProfile"]!.Value<int>());
    }

    /// <summary>
    /// The safety property that makes the cleanup non-destructive: when the nested section does NOT
    /// exist, the flat key still holds the user's only copy and must be migrated, never dropped.
    /// </summary>
    [Fact]
    public void A_flat_key_without_a_nested_section_is_migrated_not_deleted() {
        var root = JObject.Parse("""
        { "PredefinedSourceFolders": [ "D:\\only-copy" ] }
        """);

        Assert.True(ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig)));

        var nested = (JArray)root["Paths"]!["PredefinedSourceFolders"]!;
        Assert.Equal(["D:\\only-copy"], nested.Select(t => t.Value<string>()));
    }

    /// <summary>
    /// SystemInstructionPaths is an ordered document assembly, not a set. Repeats there could be
    /// deliberate, so the repair deliberately leaves it alone rather than silently rewriting it.
    /// </summary>
    [Fact]
    public void Ordered_instruction_paths_are_left_untouched_by_the_deduplication() {
        var root = JObject.Parse("""
        {
          "Sources": {
            "SystemInstructionPaths": [ "a.md", "b.md", "a.md" ]
          }
        }
        """);

        ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig));

        var paths = (JArray)root["Sources"]!["SystemInstructionPaths"]!;
        Assert.Equal(3, paths.Count);
    }

    [Fact]
    public void A_clean_config_is_reported_as_unchanged_so_the_file_is_not_rewritten() {
        var root = JObject.Parse("""
        {
          "Paths": { "SourceFolder": "D:\\a", "PredefinedSourceFolders": [ "D:\\a" ] },
          "ApiKey": { "EnvNames": [ "KEY-0" ] }
        }
        """);

        Assert.False(ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig)));
    }
}
