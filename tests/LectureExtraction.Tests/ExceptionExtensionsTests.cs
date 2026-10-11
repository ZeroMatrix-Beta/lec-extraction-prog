using System;
using System.IO;
using LectureExtraction.Infrastructure;
using Xunit;

namespace LectureExtraction.Tests;

public class ExceptionExtensionsTests {
    [Fact]
    public void Plain_exception_is_preserved_verbatim() {
        var ex = new IOException("disk full");
        Assert.Equal("IOException: disk full", ex.Describe());
    }

    [Fact]
    public void Google_sdk_daily_quota_error_is_cleaned_without_json_blobs() {
        const string rawMessage =
            "You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit.\n" +
            "* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 20, model: gemini-3.5-flash\n" +
            "Please retry in 56m20.914009181s.\n" +
            "Details:\n" +
            "{\"@type\":\"type.googleapis.com/google.rpc.Help\",\"links\":[{\"description\":\"Learn more about Gemini API quotas\",\"url\":\"https://ai.google.dev/gemini-api/docs/rate-limits\"}]}\n" +
            "{\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"quotaMetric\":\"generativelanguage.googleapis.com/generate_content_free_tier_requests\",\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\",\"quotaDimensions\":{\"model\":\"gemini-3.5-flash\",\"location\":\"global\"},\"quotaValue\":\"20\"}]}\n" +
            "{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"3380s\"}";

        var ex = new InvalidOperationException(rawMessage);
        string described = ex.Describe();

        // Must not contain raw JSON
        Assert.DoesNotContain("{\"@type\"", described);
        Assert.DoesNotContain("Details:", described);
        Assert.DoesNotContain("https://ai.google.dev", described);

        // Must contain essential info
        Assert.Contains("You exceeded your current quota", described);
        Assert.Contains("limit: 20", described);
        Assert.Contains("gemini-3.5-flash", described);
        Assert.Contains("Please retry in 56m20.914009181s", described);
        Assert.Contains("GenerateRequestsPerDayPerProjectPerModel-FreeTier", described);
        // RetryInfo only repeats "Please retry in ..." in seconds
        Assert.DoesNotContain("Retry in 3380s", described);
    }

    [Fact]
    public void Plain_text_details_section_is_kept() {
        const string msg = "Could not parse config. Details: line 3 is invalid\nsecond line with the actual cause";
        var ex = new InvalidOperationException(msg);

        Assert.Equal($"InvalidOperationException: {msg}", ex.Describe());
    }

    [Fact]
    public void Pretty_printed_json_error_after_prefix_is_formatted_cleanly() {
        const string msg = "Response status code does not indicate success: 500.\n{\n  \"error\": {\n    \"code\": 500,\n    \"message\": \"Internal error, see https://ai.google.dev/x\",\n    \"status\": \"INTERNAL\"\n  }\n}";
        var ex = new HttpRequestException(msg);

        Assert.Equal("HttpRequestException: Response status code does not indicate success: 500 - INTERNAL (500): Internal error, see https://ai.google.dev/x", ex.Describe());
    }

    [Fact]
    public void Json_error_with_non_numeric_code_is_formatted_without_the_code() {
        const string json = "{\"error\": {\"code\": \"429\", \"message\": \"Too many requests\", \"status\": \"RESOURCE_EXHAUSTED\"}}";
        var ex = new InvalidOperationException(json);

        Assert.Equal("InvalidOperationException: RESOURCE_EXHAUSTED: Too many requests", ex.Describe());
    }

    [Fact]
    public void Pure_json_error_is_formatted_with_status_and_message() {
        const string json = "{\"error\": {\"code\": 404, \"message\": \"models/gemini-pro is not found\", \"status\": \"NOT_FOUND\"}}";
        var ex = new InvalidOperationException(json);

        Assert.Equal("InvalidOperationException: NOT_FOUND (404): models/gemini-pro is not found", ex.Describe());
    }

    [Fact]
    public void Json_error_with_error_info_reason_includes_reason() {
        const string json = "{\"error\": {\"code\": 400, \"message\": \"API key not valid\", \"status\": \"INVALID_ARGUMENT\", \"details\": [{\"@type\": \"type.googleapis.com/google.rpc.ErrorInfo\", \"reason\": \"API_KEY_INVALID\"}]}}";
        var ex = new InvalidOperationException(json);

        Assert.Equal("InvalidOperationException: INVALID_ARGUMENT (400): API key not valid [Reason: API_KEY_INVALID]", ex.Describe());
    }

    [Fact]
    public void Http_prefix_with_json_error_is_formatted_cleanly() {
        const string msg = "Response status code does not indicate success: 404 (Not Found). {\"error\": {\"code\": 404, \"message\": \"Resource not found\", \"status\": \"NOT_FOUND\"}}";
        var ex = new HttpRequestException(msg);

        string described = ex.Describe();
        Assert.DoesNotContain("{\"error\"", described);
        Assert.Contains("NOT_FOUND (404): Resource not found", described);
    }

    [Fact]
    public void Single_line_quota_details_is_formatted_cleanly() {
        const string msg = "Quota exceeded. Details: {\"violations\":[{\"quotaId\":\"GenerateRequestsPerMinutePerProjectPerModel-FreeTier\"}]}";
        var ex = new InvalidOperationException(msg);

        Assert.Equal("InvalidOperationException: Quota exceeded [GenerateRequestsPerMinutePerProjectPerModel-FreeTier]", ex.Describe());
    }

    [Fact]
    public void Null_or_empty_message_returns_exception_type_alone() {
        var ex = new Exception("");
        Assert.Equal("Exception", ex.Describe());
    }
}
