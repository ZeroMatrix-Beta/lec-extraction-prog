using System;
using System.Text.Json;
using LectureExtraction.Configuration;
using Newtonsoft.Json;

namespace LectureExtraction.Tests;

public class NumberOfPartsTests {
    [Fact]
    public void Auto_HasCorrectProperties() {
        var auto = NumberOfParts.Auto;

        Assert.True(auto.IsAuto);
        Assert.Null(auto.FixedParts);
        Assert.Equal("auto", auto.ToString());
    }

    [Fact]
    public void Fixed_HasCorrectProperties() {
        var fixedParts = new NumberOfParts(2);

        Assert.False(fixedParts.IsAuto);
        Assert.Equal(2, fixedParts.FixedParts);
        Assert.Equal("2", fixedParts.ToString());
    }

    [Theory]
    [InlineData("auto", true, 0)]
    [InlineData("AUTO", true, 0)]
    [InlineData("Auto", true, 0)]
    [InlineData("  auto  ", true, 0)]
    [InlineData("1", false, 1)]
    [InlineData("2", false, 2)]
    [InlineData("10", false, 10)]
    public void TryParse_ParsesValidInputs(string input, bool expectedAuto, int expectedFixed) {
        Assert.True(NumberOfParts.TryParse(input, out var parts));
        Assert.Equal(expectedAuto, parts.IsAuto);
        if (!expectedAuto) {
            Assert.Equal(expectedFixed, parts.FixedParts);
        }
    }

    [Theory]
    [InlineData("seven")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsInvalidInputs(string? input) {
        Assert.False(NumberOfParts.TryParse(input, out _));
    }

    [Fact]
    public void Parse_ThrowsOnInvalidInput() {
        Assert.Throws<FormatException>(() => NumberOfParts.Parse("seven"));
        Assert.Throws<FormatException>(() => NumberOfParts.Parse("-5"));
    }

    [Theory]
    // Under 40 min -> 1 part
    [InlineData(0, 3)] // fallback when duration is zero/unknown
    [InlineData(-10, 3)] // fallback when duration is negative
    [InlineData(10 * 60, 1)] // 10 min
    [InlineData(30 * 60, 1)] // 30 min
    [InlineData(39 * 60 + 59, 1)] // 39m 59s
    // 40 to < 85 min -> 2 parts
    [InlineData(40 * 60, 2)] // 40 min
    [InlineData(45 * 60, 2)] // 45 min
    [InlineData(50 * 60, 2)] // 50 min
    [InlineData(60 * 60, 2)] // 60 min
    [InlineData(75 * 60, 2)] // 75 min
    [InlineData(84 * 60 + 59, 2)] // 84m 59s
    // >= 85 min -> 3 parts
    [InlineData(85 * 60, 3)] // 85 min (around 85 min still 3 parts)
    [InlineData(90 * 60, 3)] // 90 min
    [InlineData(100 * 60, 3)] // 100 min
    [InlineData(129 * 60 + 59, 3)] // 129m 59s
    // Long videos scale further (+1 part per 45 min after 85 min)
    [InlineData(130 * 60, 4)] // 130 min = 85 + 45 -> 4 parts
    [InlineData(135 * 60, 4)] // 135 min -> 4 parts
    [InlineData(175 * 60, 5)] // 175 min = 85 + 90 -> 5 parts
    public void Auto_ResolvesExpectedParts_ByDuration(double durationSeconds, int expectedParts) {
        var auto = NumberOfParts.Auto;
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30 * 60)]
    [InlineData(45 * 60)]
    [InlineData(90 * 60)]
    [InlineData(180 * 60)]
    public void Fixed_AlwaysResolvesToConfiguredValue_RegardlessOfDuration(double durationSeconds) {
        var fixedTwo = new NumberOfParts(2);
        Assert.Equal(2, fixedTwo.Resolve(durationSeconds));

        var fixedFive = new NumberOfParts(5);
        Assert.Equal(5, fixedFive.Resolve(durationSeconds));
    }

    [Fact]
    public void NewtonsoftJson_SerializesAndDeserializes_Correctly() {
        var configWithNumber = new DummyConfig { Parts = 2 };
        string jsonNumber = JsonConvert.SerializeObject(configWithNumber);
        Assert.Contains("\"Parts\":2", jsonNumber);

        var restoredFromNumber = JsonConvert.DeserializeObject<DummyConfig>(jsonNumber);
        Assert.NotNull(restoredFromNumber);
        Assert.False(restoredFromNumber.Parts.IsAuto);
        Assert.Equal(2, restoredFromNumber.Parts.FixedParts);

        var configWithAuto = new DummyConfig { Parts = NumberOfParts.Auto };
        string jsonAuto = JsonConvert.SerializeObject(configWithAuto);
        Assert.Contains("\"Parts\":\"auto\"", jsonAuto);

        var restoredFromAuto = JsonConvert.DeserializeObject<DummyConfig>(jsonAuto);
        Assert.NotNull(restoredFromAuto);
        Assert.True(restoredFromAuto.Parts.IsAuto);

        // Deserializing from JSON string "2"
        string jsonStringNumber = "{\"Parts\": \"2\"}";
        var restoredFromStringNumber = JsonConvert.DeserializeObject<DummyConfig>(jsonStringNumber);
        Assert.NotNull(restoredFromStringNumber);
        Assert.False(restoredFromStringNumber.Parts.IsAuto);
        Assert.Equal(2, restoredFromStringNumber.Parts.FixedParts);
    }

    [Fact]
    public void SystemTextJson_SerializesAndDeserializes_Correctly() {
        var configWithNumber = new DummyConfig { Parts = 2 };
        string jsonNumber = System.Text.Json.JsonSerializer.Serialize(configWithNumber);
        Assert.Contains("\"Parts\":2", jsonNumber);

        var restoredFromNumber = System.Text.Json.JsonSerializer.Deserialize<DummyConfig>(jsonNumber);
        Assert.NotNull(restoredFromNumber);
        Assert.False(restoredFromNumber.Parts.IsAuto);
        Assert.Equal(2, restoredFromNumber.Parts.FixedParts);

        var configWithAuto = new DummyConfig { Parts = NumberOfParts.Auto };
        string jsonAuto = System.Text.Json.JsonSerializer.Serialize(configWithAuto);
        Assert.Contains("\"Parts\":\"auto\"", jsonAuto);

        var restoredFromAuto = System.Text.Json.JsonSerializer.Deserialize<DummyConfig>(jsonAuto);
        Assert.NotNull(restoredFromAuto);
        Assert.True(restoredFromAuto.Parts.IsAuto);
    }

    [Fact]
    public void TypeConverter_ConvertsStrings() {
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(typeof(NumberOfParts));
        Assert.True(converter.CanConvertFrom(typeof(string)));

        var auto = (NumberOfParts)converter.ConvertFromString(null, System.Globalization.CultureInfo.InvariantCulture, "auto")!;
        Assert.True(auto.IsAuto);

        var fixedParts = (NumberOfParts)converter.ConvertFromString(null, System.Globalization.CultureInfo.InvariantCulture, "4")!;
        Assert.False(fixedParts.IsAuto);
        Assert.Equal(4, fixedParts.FixedParts);
    }

    private sealed class DummyConfig {
        public NumberOfParts Parts { get; set; } = 3;
    }
}
