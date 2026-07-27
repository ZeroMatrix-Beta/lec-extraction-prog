using System.Reflection;

namespace LectureExtraction.Tests;

/// <summary>
/// Guards <c>ConfigLoader&lt;T&gt;</c>'s comment-preserving save path: users are meant to be
/// able to annotate the shipped <c>*.json</c> config files with <c>//</c> or <c>/* */</c>
/// comments and have those comments survive every time the app rewrites the file after an
/// option changes - whether the comment sits between two properties, inside an array, or right
/// before the closing brace.
/// </summary>
public class ConfigCommentPreservationTests {
    private sealed class SampleConfig {
        public int Temperature { get; set; }
        public string SourceFolder { get; set; } = "";
        public List<string> Models { get; set; } = [];
        public NestedSampleConfig Nested { get; set; } = new();
    }

    private sealed class NestedSampleConfig {
        public int Retries { get; set; }
    }

    /// <summary>Invokes the private, file-based `SerializePreservingComments(string, T)` via reflection against a scratch file, mirroring exactly what `ConfigLoader&lt;T&gt;.Save` does.</summary>
    private static string SerializePreservingComments(string existingFileContent, SampleConfig updatedConfig) {
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        try {
            File.WriteAllText(tempFile, existingFileContent);
            MethodInfo method = typeof(LectureExtraction.Configuration.ConfigLoader<SampleConfig>)
                .GetMethod("SerializePreservingComments", BindingFlags.NonPublic | BindingFlags.Static)!;
            return (string)method.Invoke(null, [tempFile, updatedConfig])!;
        }
        finally {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void StandaloneCommentBeforeAProperty_SurvivesAValueChange() {
        string original = """
            {
              // this stays fast for local runs, don't crank it up
              "Temperature": 36,
              "SourceFolder": "D:\\old",
              "Models": [],
              "Nested": { "Retries": 1 }
            }
            """;

        string result = SerializePreservingComments(original, new SampleConfig { Temperature = 99, SourceFolder = "D:\\new" });

        Assert.Contains("// this stays fast for local runs, don't crank it up", result);
        Assert.Contains("\"Temperature\": 99", result);
        Assert.Contains("\"SourceFolder\": \"D:\\\\new\"", result);
    }

    [Fact]
    public void TrailingCommentBeforeClosingBrace_Survives() {
        string original = """
            {
              "Temperature": 36
              // keep me, I'm at the end
            }
            """;

        string result = SerializePreservingComments(original, new SampleConfig { Temperature = 1 });

        Assert.Contains("// keep me, I'm at the end", result);
        Assert.Contains("\"Temperature\": 1", result);
    }

    [Fact]
    public void CommentBeforeAPropertyInsideANestedObject_Survives() {
        string original = """
            {
              "Temperature": 36,
              "Nested": {
                // don't retry too aggressively, the API rate-limits us
                "Retries": 1
              }
            }
            """;

        string result = SerializePreservingComments(original, new SampleConfig { Nested = new NestedSampleConfig { Retries = 5 } });

        Assert.Contains("// don't retry too aggressively, the API rate-limits us", result);
        Assert.Contains("\"Retries\": 5", result);
    }

    [Fact]
    public void CommentInsideAnArray_SurvivesElementsBeingReplaced() {
        string original = """
            {
              "Models": [
                // prefer flash for cost, fall back to pro only if quality regresses
                "gemini-3.6-flash",
                "gemini-2.5-flash"
              ]
            }
            """;

        string result = SerializePreservingComments(original, new SampleConfig { Models = ["gemini-4-flash", "gemini-3.6-flash"] });

        Assert.Contains("prefer flash for cost, fall back to pro only if quality regresses", result);
        Assert.Contains("gemini-4-flash", result);
        Assert.Contains("gemini-3.6-flash", result);
    }

    [Fact]
    public void MultipleCommentsAndPropertiesInTheSameFile_AllSurviveTogether() {
        string original = """
            {
              // section: sampling
              "Temperature": 36,
              "SourceFolder": "D:\\old",
              // section: models
              "Models": [
                "gemini-3.6-flash"
              ]
              // end of file
            }
            """;

        string result = SerializePreservingComments(original, new SampleConfig {
            Temperature = 50,
            SourceFolder = "D:\\new",
            Models = ["gemini-4-flash"],
        });

        Assert.Contains("// section: sampling", result);
        Assert.Contains("// section: models", result);
        Assert.Contains("// end of file", result);
        Assert.Contains("\"Temperature\": 50", result);
        Assert.Contains("\"SourceFolder\": \"D:\\\\new\"", result);
        Assert.Contains("gemini-4-flash", result);
    }
}
