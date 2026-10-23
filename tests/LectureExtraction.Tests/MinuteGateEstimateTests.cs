using Google.GenAI.Types;
using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

public class MinuteGateEstimateTests {
    [Fact]
    public void Counts_text_of_system_and_contents_but_not_media() {
        var system = new Content { Role = "system", Parts = [new Part { Text = new string('a', 400) }] };
        var contents = new List<Content> {
            new() {
                Role = "user",
                Parts = [
                    new Part { Text = @"\frac{a}{b}" },
                    new Part { InlineData = new Blob { Data = new byte[10_000], MimeType = "image/png" } },
                    new Part { FileData = new FileData { FileUri = "https://example.invalid/v", MimeType = "video/mp4" } },
                ]
            },
            new() { Role = "model", Parts = [new Part { Text = "OK" }] },
        };

        Assert.Equal(400 + 11 + 2, MinuteGateEstimate.TextCharacters(system, contents));
        Assert.Contains("413 Textzeichen ÷ 4 = 103", MinuteGateEstimate.Describe(system, contents));
    }

    [Fact]
    public void No_system_instruction_and_empty_parts_are_fine() {
        var contents = new List<Content> { new() { Role = "user" } };
        Assert.Equal(0, MinuteGateEstimate.TextCharacters(null, contents));
    }
}
