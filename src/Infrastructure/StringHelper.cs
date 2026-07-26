using System;
using System.Text.RegularExpressions;

namespace Infrastructure;

public static partial class StringHelper {
    /// <summary>
    /// Truncates a string to the specified length and appends an ellipsis if necessary.
    /// </summary>
    public static string Truncate(this string value, int maxLength) {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
    }

    /// <summary>
    /// Removes all carriage returns and line feeds from a string.
    /// </summary>
    public static string RemoveNewLines(this string value) {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Replace("\r", "").Replace("\n", " ");
    }

    /// <summary>
    /// Checks if a string contains another string, ignoring case.
    /// </summary>
    public static bool ContainsIgnoreCase(this string source, string toCheck) {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(toCheck)) return false;
        return source.Contains(toCheck, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\\end\{([a-zA-Z0-9_\-\*]+)\>")]
    private static partial Regex MalformedEndTagRegex();

    /// <summary>
    /// Fixes malformed LaTeX end tags where a closing angle bracket '>' was used instead of a curly brace '}'.
    /// </summary>
    public static string FixMalformedEndTags(this string value) {
        if (string.IsNullOrEmpty(value)) return value;

        // Fast pre-check before doing regex evaluation
        if (!value.Contains(@"\end{", StringComparison.Ordinal)) return value;

        return MalformedEndTagRegex().Replace(value, match => {
            var tagName = match.Groups[1].Value;
            return $@"\end{{{tagName}}}";
        });
    }
}
