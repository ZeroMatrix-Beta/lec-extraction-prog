using LectureExtraction.GoogleAi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// --profile for the refinement, which keeps its own key list. The lists below are the two configs as
/// they stood on 2026-10-26, when <c>--profile 4</c> ran the refinement on profile 0's key: a number
/// must name the profile table's key, wherever that key sits in the refinement list.
/// </summary>
public class ApiKeyProfileResolverTests {
    private static readonly string[] ProfileTable = [
        "API_KEY-automated-content-extraction",
        "API_KEY-ai-studio-test-project-1",
        "API_KEY-ai-studio-test-project-2",
        "API_KEY-ai-studio-test-project-3",
        "API_KEY-latex-refinement"
    ];

    private static readonly string[] RefinementList = [
        "API_KEY-latex-refinement",
        "API_KEY-ai-studio-test-project-1",
        "API_KEY-ai-studio-test-project-2",
        "API_KEY-ai-studio-test-project-3",
        "API_KEY-automated-content-extraction"
    ];

    [Theory]
    [InlineData("0", "API_KEY-automated-content-extraction")]
    [InlineData("2", "API_KEY-ai-studio-test-project-2")]
    [InlineData("4", "API_KEY-latex-refinement")]
    [InlineData(" 4 ", "API_KEY-latex-refinement")]
    public void A_number_names_the_profile_tables_key(string profile, string expectedKey) {
        int index = ApiKeyProfileResolver.ResolveIndexInStageList(profile, ProfileTable, RefinementList);

        Assert.Equal(expectedKey, RefinementList[index]);
    }

    [Theory]
    [InlineData("latex-refinement", "API_KEY-latex-refinement")]
    [InlineData("API_KEY-automated-content-extraction", "API_KEY-automated-content-extraction")]
    public void A_name_is_looked_up_as_given(string profile, string expectedKey) {
        int index = ApiKeyProfileResolver.ResolveIndexInStageList(profile, ProfileTable, RefinementList);

        Assert.Equal(expectedKey, RefinementList[index]);
    }

    [Fact]
    public void A_key_missing_from_the_stage_list_is_minus_one() {
        string[] withoutExtractionKey = RefinementList[..4];

        Assert.Equal(-1, ApiKeyProfileResolver.ResolveIndexInStageList("0", ProfileTable, withoutExtractionKey));
        Assert.Equal(-1, ApiKeyProfileResolver.ResolveIndexInStageList("no-such-key", ProfileTable, RefinementList));
    }
}
