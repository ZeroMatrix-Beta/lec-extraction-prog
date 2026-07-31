using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LectureExtraction.Cli;

/// <summary>
/// The stdout/stderr split. A command produces exactly one payload; whether that payload is
/// rendered as JSON for a caller or as text for a human is decided here, not at each call site, so
/// the two renderings can never drift into reporting different things.
/// </summary>
public static class CliOutput {
    private static readonly JsonSerializerOptions PayloadOptions = new() {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Domain records (PreparedVideo and friends) are PascalCase by C# convention, while the
        // hand-written payloads are camelCase. Without a policy the JSON contract would depend on
        // whether a command happened to emit a record or an anonymous type.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Emits the command's result: serialized to stdout under <c>--json</c>, otherwise handed to
    /// <paramref name="renderHuman"/>. Under <c>--quiet</c> without <c>--json</c> nothing is
    /// written at all - the exit code is the answer.
    /// </summary>
    public static void Payload(CliContext context, object payload, Action renderHuman) {
        if (context.Json) {
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, PayloadOptions));
            return;
        }

        if (!context.Quiet) {
            renderHuman();
        }
    }

    /// <summary>Serializes a value with the same settings the payload uses.</summary>
    public static string ToJson(object value) => JsonSerializer.Serialize(value, PayloadOptions);
}
