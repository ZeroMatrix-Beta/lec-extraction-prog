using LectureExtraction.Infrastructure;

namespace LectureExtraction.Tests;

/// <summary>
/// Characterization tests for the <see cref="StringExtensions"/> extension methods.
/// <c>FixMalformedEndTags</c> matters most: it repairs a specific model output defect
/// (<c>\end{env&gt;</c> instead of <c>\end{env}</c>) that would otherwise break LaTeX compilation.
/// </summary>
public class StringHelperTests {
    [Theory]
    [InlineData("hello", 10, "hello")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello world", 5, "hello...")]
    [InlineData("", 5, "")]
    public void Truncate_AppendsEllipsis_OnlyWhenItActuallyCuts(string value, int maxLength, string expected) {
        Assert.Equal(expected, value.Truncate(maxLength));
    }

    [Theory]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\nb", "a b")]
    [InlineData("a\rb", "ab")]
    [InlineData("no newlines", "no newlines")]
    public void RemoveNewLines_DropsCarriageReturns_AndTurnsLineFeedsIntoSpaces(string value, string expected) {
        Assert.Equal(expected, value.RemoveNewLines());
    }

    [Theory]
    [InlineData("Hello World", "ELL", true)]
    [InlineData("Hello World", "xyz", false)]
    [InlineData("", "a", false)]
    [InlineData("a", "", false)]
    public void ContainsIgnoreCase_IsCaseInsensitive_AndFalseForEmptyOperands(string source, string toCheck, bool expected) {
        Assert.Equal(expected, source.ContainsIgnoreCase(toCheck));
    }

    [Fact]
    public void FixMalformedEndTags_RepairsAngleBracketInsteadOfClosingBrace() {
        Assert.Equal(@"\end{equation}", @"\end{equation>".FixMalformedEndTags());
    }

    [Fact]
    public void FixMalformedEndTags_RepairsStarredAndHyphenatedEnvironments() {
        Assert.Equal(@"\end{align*}", @"\end{align*>".FixMalformedEndTags());
        Assert.Equal(@"\end{spoken-clean}", @"\end{spoken-clean>".FixMalformedEndTags());
    }

    [Fact]
    public void FixMalformedEndTags_LeavesWellFormedTagsAlone() {
        const string wellFormed = @"\begin{equation}x=1\end{equation}";

        Assert.Equal(wellFormed, wellFormed.FixMalformedEndTags());
    }

    [Fact]
    public void FixMalformedEndTags_WithNoEndTagAtAll_ShortCircuits() {
        const string noEndTag = @"\section{Intro}";

        Assert.Equal(noEndTag, noEndTag.FixMalformedEndTags());
    }
}
