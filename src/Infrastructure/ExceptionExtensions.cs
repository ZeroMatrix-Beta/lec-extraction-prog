using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LectureExtraction.Infrastructure;

/// <summary>
/// [AI Context] The single format for an exception inside a console message: <c>TypeName: Message</c>.
/// The type is what tells "the server said no" (ClientError) from "the connection dropped"
/// (HttpRequestException, IOException) when only the log is left; three hand-written variants of
/// this (with ": ", " - " and "Art der Exception: ..., Fehler: ...") had grown across the code.
/// Cleans up raw JSON error blobs, RPC QuotaFailure/RetryInfo payloads, and redundant documentation
/// URLs emitted by the Google AI Studio SDK and HTTP APIs into concise, human-readable summaries.
/// [Human] Einheitliche Darstellung einer Exception in Meldungen: "Typ: Nachricht" (ohne rohe JSON-Blobs).
/// </summary>
public static partial class ExceptionExtensions {
    public static string Describe(this Exception ex) {
        string cleaned = CleanMessage(ex.Message);
        return string.IsNullOrWhiteSpace(cleaned)
            ? ex.GetType().Name
            : $"{ex.GetType().Name}: {cleaned}";
    }

    /// <summary>
    /// [AI Context] Parses and cleans up raw error text that may contain embedded JSON or Google RPC payloads.
    /// If no JSON or boilerplate is detected, returns the original text untouched.
    /// [Human] Befreit Fehlermeldungen von JSON-Strukturen und formatiert sie kompakt und lesbar.
    /// </summary>
    public static string CleanMessage(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string trimmed = raw.Trim();

        // 1. Pure JSON payload (object or array)
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) {
            if (TryParseJsonError(trimmed, out string formattedJson)) {
                return formattedJson;
            }
        }

        // 2. Embedded JSON error payload (e.g. "Response status code does not indicate success: 404 (Not Found). {\"error\": ...}"),
        //    compact or pretty-printed
        Match jsonStart = ErrorJsonStartRegex().Match(trimmed);
        if (jsonStart.Success) {
            string prefix = trimmed[..jsonStart.Index].Trim().TrimEnd('.', ':');
            string jsonPart = trimmed[jsonStart.Index..];
            if (TryParseJsonError(jsonPart, out string formattedJson)) {
                return string.IsNullOrWhiteSpace(prefix) ? formattedJson : $"{prefix} - {formattedJson}";
            }
        }

        // 3. Google GenAI SDK multi-line message (contains "Details:" or lines with JSON)
        return CleanGoogleSdkErrorMessage(trimmed);
    }

    private static string CleanGoogleSdkErrorMessage(string raw) {
        // The SDK appends "Details:" and one JSON object per line. Only that form is split off: a
        // plain-text "Details: ..." from any other source is part of the message and stays as it is.
        string text = raw;
        List<string> details = [];
        int detailsIdx = raw.IndexOf("Details:", StringComparison.OrdinalIgnoreCase);
        bool splitDetails = detailsIdx >= 0 &&
            TryParseDetailsBlock(raw[(detailsIdx + "Details:".Length)..], details);
        if (splitDetails) {
            text = raw[..detailsIdx].TrimEnd().TrimEnd('.', ':');
        }

        if (!splitDetails &&
            !raw.Contains("{\"@type\"", StringComparison.OrdinalIgnoreCase) &&
            !raw.Contains("https://ai.google.dev", StringComparison.OrdinalIgnoreCase)) {
            return raw;
        }

        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        List<string> textLines = [];

        foreach (string rawLine in lines) {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;

            if ((line.StartsWith('{') || line.StartsWith('[')) && ParseAndExtractDetails(line, details)) {
                continue;
            }

            int jsonObjIdx = line.IndexOf("{\"@type\"", StringComparison.OrdinalIgnoreCase);
            if (jsonObjIdx > 0 && ParseAndExtractDetails(line[jsonObjIdx..], details)) {
                line = line[..jsonObjIdx].Trim().TrimEnd('.', ':');
            }

            string cleanedLine = StripDocumentationUrls(line).Trim();
            if (cleanedLine.StartsWith("* ", StringComparison.Ordinal)) {
                cleanedLine = cleanedLine[2..].Trim();
            }

            if (cleanedLine.Length > 0) {
                textLines.Add(cleanedLine);
            }
        }

        var sb = new StringBuilder();
        if (textLines.Count > 0) {
            sb.Append(string.Join(" ", textLines));
        }

        string currentText = sb.ToString();
        List<string> newDetails = [];
        foreach (string d in details) {
            if (currentText.Contains(d, StringComparison.OrdinalIgnoreCase)) continue;
            // RetryInfo repeats the "Please retry in ..." the message already gives, only in seconds.
            if (d.StartsWith("Retry in ", StringComparison.Ordinal) &&
                currentText.Contains("retry in", StringComparison.OrdinalIgnoreCase)) continue;
            newDetails.Add(d);
        }

        if (newDetails.Count > 0) {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('[').Append(string.Join("; ", newDetails)).Append(']');
        }

        return sb.Length > 0 ? sb.ToString() : raw;
    }

    private static string StripDocumentationUrls(string line) {
        string cleaned = DocsBoilerplateRegex().Replace(line, "").Trim();
        cleaned = RedundantPunctuationRegex().Replace(cleaned, ".").Trim();
        return cleaned;
    }

    private static bool TryParseJsonError(string json, out string formatted) {
        formatted = "";
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == JsonValueKind.Object) {
                root = errorProp;
            }

            if (root.ValueKind != JsonValueKind.Object) {
                return false;
            }

            string? status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            string? message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            int? code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int codeVal) ? codeVal : null;

            List<string> extraDetails = [];
            if (root.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array) {
                foreach (var detail in detailsProp.EnumerateArray()) {
                    ExtractDetailInfo(detail, extraDetails);
                }
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(status)) {
                sb.Append(status);
                if (code.HasValue) sb.Append($" ({code.Value})");
                sb.Append(": ");
            }
            else if (code.HasValue) {
                sb.Append($"Error {code.Value}: ");
            }

            sb.Append(!string.IsNullOrWhiteSpace(message) ? message : "Unknown API error");

            if (extraDetails.Count > 0) {
                sb.Append(" [").Append(string.Join("; ", extraDetails)).Append(']');
            }

            formatted = sb.ToString();
            return true;
        }
        catch (JsonException) {
            return false;
        }
    }

    /// <summary>
    /// True when every non-empty line of <paramref name="block"/> is a JSON object or array; only
    /// then are their details added to <paramref name="output"/>.
    /// </summary>
    private static bool TryParseDetailsBlock(string block, List<string> output) {
        string[] lines = block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<string> found = [];
        foreach (string line in lines) {
            if (!ParseAndExtractDetails(line, found)) return false;
        }
        output.AddRange(found);
        return lines.Length > 0;
    }

    /// <summary>
    /// False when <paramref name="jsonCandidate"/> is not a JSON object or array; the caller then keeps it as text.
    /// </summary>
    private static bool ParseAndExtractDetails(string jsonCandidate, List<string> output) {
        try {
            using var doc = JsonDocument.Parse(jsonCandidate);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) {
                foreach (var item in root.EnumerateArray()) {
                    ExtractDetailInfo(item, output);
                }
                return true;
            }
            if (root.ValueKind == JsonValueKind.Object) {
                ExtractDetailInfo(root, output);
                return true;
            }
            return false;
        }
        catch (JsonException) {
            return false;
        }
    }

    private static void ExtractDetailInfo(JsonElement element, List<string> output) {
        if (element.ValueKind != JsonValueKind.Object) return;

        // ErrorInfo (e.g. API_KEY_INVALID)
        if (element.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String) {
            string r = reason.GetString()!;
            if (r.Length > 0) output.Add($"Reason: {r}");
        }

        // QuotaFailure
        if (element.TryGetProperty("violations", out var violations) && violations.ValueKind == JsonValueKind.Array) {
            foreach (var v in violations.EnumerateArray()) {
                if (v.ValueKind != JsonValueKind.Object) continue;
                string? quotaId = v.TryGetProperty("quotaId", out var qId) && qId.ValueKind == JsonValueKind.String ? qId.GetString() : null;
                string? quotaValue = v.TryGetProperty("quotaValue", out var qVal) && qVal.ValueKind == JsonValueKind.String ? qVal.GetString() : null;
                if (!string.IsNullOrWhiteSpace(quotaId)) {
                    output.Add(!string.IsNullOrWhiteSpace(quotaValue) ? $"{quotaId} (limit: {quotaValue})" : quotaId);
                }
            }
        }

        // RetryInfo
        if (element.TryGetProperty("retryDelay", out var delay) && delay.ValueKind == JsonValueKind.String) {
            string d = delay.GetString()!;
            if (d.Length > 0) output.Add($"Retry in {d}");
        }

        // BadRequest / fieldViolations
        if (element.TryGetProperty("fieldViolations", out var fieldViolations) && fieldViolations.ValueKind == JsonValueKind.Array) {
            foreach (var fv in fieldViolations.EnumerateArray()) {
                if (fv.ValueKind != JsonValueKind.Object) continue;
                string? field = fv.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                string? desc = fv.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (!string.IsNullOrWhiteSpace(field) || !string.IsNullOrWhiteSpace(desc)) {
                    output.Add($"Field {field}: {desc}");
                }
            }
        }
    }

    [GeneratedRegex(@"(?:For more information on this error,?\s*head to:?\s*https?://\S+|To monitor your current usage,?\s*head to:?\s*https?://\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex DocsBoilerplateRegex();

    [GeneratedRegex(@"\s*\.\s*\.")]
    private static partial Regex RedundantPunctuationRegex();

    [GeneratedRegex(@"\{\s*""error""\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorJsonStartRegex();
}
