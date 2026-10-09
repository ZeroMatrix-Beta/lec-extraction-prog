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
7. **Mandatory Build Verification Before Task Completion**:
   Before finishing any task or handing over to the user, you MUST run `dotnet build`. Ensure the output is exactly `0 Warning(s)` and `0 Error(s)`. Any warnings (e.g. `CA1860`, `SYSLIB1045`, `IDE0028`, `IDE0060`) must be fixed immediately.
8. **No Access to External Prompt Directory**:
   You do not need read or write access to the external directory `C:\Users\miche\latex\prompt-engineering` or its subdirectories. The prompt engineering files there are managed outside the scope of this project. Do not request permissions for this directory path.
9. **Running the program headlessly**:
   The program has two entry points, chosen in `Program.Main` by argument count: no
   arguments starts the interactive Spectre menu, anything else runs the CLI
   (`src/Cli/`). **Never launch it without arguments from an automated context** — the
   menu blocks on a prompt that a non-interactive terminal cannot answer.
   - Always run `lecx plan` (free, no API call) before any command that costs money.
   - **Never pass `--save-config`.** Config writeback is off by default in CLI mode so
     an unattended run cannot rewrite the user's live `*Config.json`.
   - Exit code `6` means *partial* success and must not be treated as `0`.
   - Full guidance: `docs/cli/agents.md`. Command reference: `docs/cli/commands.md`.
10. **Prompts go through `IPromptSource`**:
   `Ui.Select/SelectMany/Confirm/Ask` resolve their answers through
   `Ui.PromptSource` (`src/ConsoleUi/`). The interactive implementation is the keyboard;
   the CLI installs `PresetPromptSource`, which auto-answers only questions carrying an
   explicit default (and only under `--yes`) and **throws on menus**. When adding a
   prompt, do not call Spectre directly — route it through `Ui`, or it will crash
   unattended runs instead of failing with a readable message.
11. **Extraction Session Structure**:
   - `AiStudioAutoExtractionSession` is split into:
     - `AiStudioAutoExtractionSession.cs`: Core pipeline execution, file processing, transcription, and YouTube workflows.
     - `AiStudioAutoExtractionSession.PrefixCache.cs`: Prefix cache initialization and warm-up logic (`TryLoadSystemInstructionWithHistoryAsync`, `WarmUpWithBatchedHistoryAsync`).
   - `VertexAutoExtractionSession` is split into:
     - `VertexAutoExtractionSession.cs`: Core pipeline execution, file processing, transcription, and YouTube workflows.
     - `VertexAutoExtractionSession.PrefixCache.cs`: Prefix cache initialization and warm-up logic.
