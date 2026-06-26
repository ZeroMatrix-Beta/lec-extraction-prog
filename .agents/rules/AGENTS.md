# Workspace Rules (`lec-extraction-prog`)

## C# Code Style & Performance Rules
1. **Prefer `Count > 0` over `Any()`**:
   For performance and clarity, always check `.Count > 0` or `.Length > 0` on collections/arrays instead of using `.Any()`.
2. **Target-typed `new()`**:
   Use `new()` when the target type is clear from context (e.g. `List<string> list = [];` or `new() { Text = ... }`).
3. **Use `[GeneratedRegex]` Source Generators**:
   Do not instantiate dynamic `Regex` or use static `Regex.Replace(...)` / `Regex.IsMatch(...)` in hot paths. Define `partial Regex ...` methods annotated with `[GeneratedRegex("...")]`.
4. **Clean Method Signatures**:
   Never leave unused parameters in method signatures.
5. **Visible Exception Handling**:
   Never swallow exceptions silently. Log `ex.GetType().Name` and `ex.Message` to the console.
6. **Preserve Architecture Comments**:
   Do not remove `[AI Context]` or `[Human]` summaries.
