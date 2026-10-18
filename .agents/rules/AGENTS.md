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
8. **The Prompt Repo `C:\Users\miche\latex\prompt-engineering`**:
   The `*Config.json` files here load its prompt files by absolute path, so renaming or moving a file there breaks this project. Reading it is fine when a task touches the prompts or those paths. Change it only when told to; changes there are committed in that repo (same rule as `gemini.md` §4).
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
   - One `AutoExtractionSession` serves AI Studio and Vertex; what differs per platform is an
     `IExtractionBackend` (`AiStudioExtractionBackend`, `VertexExtractionBackend`, the latter with
     `ExplicitContextCache`). A fix to the pipeline goes into the session, never into one backend only.
   - The pre-merge `VertexAutoExtractionSession` is kept unchanged as the reference until a real Vertex
     run compares it with the merged path (`UseLegacyVertexSession` selects it). Do not delete it
     without the user's say-so.
   - `AutoExtractionSession` is split into:
     - `AutoExtractionSession.cs`: Core pipeline execution, file processing, transcription, and YouTube workflows.
     - `AutoExtractionSession.Generation.cs`: Per-segment upload, request building, streaming (through `GenerationLoop`).
     - `AutoExtractionSession.PrefixCache.cs`: Prefix cache initialization and warm-up logic (`TryLoadSystemInstructionWithHistoryAsync`, `WarmUpWithBatchedHistoryAsync`).

## 12. Operational Runbook: Converting & Processing Lecture Videos
When the user asks to process/convert lecture videos, **do not spend turns searching code or re-reading pipeline internals**. Follow this exact runbook:

### Course & Directory Mapping
- **Analysis 2**:
  - Source videos: `D:\lecture-videos\analysis2`
  - Extraction output: `D:\lecture-videos\analysis2\extracted_output`
  - Main LaTeX document: `C:\Users\miche\latex\eth-analysis2-fs2026-ai-transcription\analysis2-fs2026-color.tex`
  - Content snippets folder: `C:\Users\miche\latex\eth-analysis2-fs2026-ai-transcription\content`
- **Grundstrukturen**:
  - Source videos: `D:\lecture-videos\grundstrukturen`
  - Extraction output: `D:\lecture-videos\grundstrukturen\extracted_output`
  - Main LaTeX document: `C:\Users\miche\latex\grundstrukturen-ai-transcription-spring-2025\grundstrukturen.tex`
  - Content snippets folder: `C:\Users\miche\latex\grundstrukturen-ai-transcription-spring-2025\content`

### Fast Execution Workflow
1. **Check Status**:
   ```powershell
   dotnet run --no-build -- status --folder "<output-folder>"
   ```
2. **Plan Next Video (Free pre-flight)**:
   ```powershell
   dotnet run --no-build -- plan --video "<video-path>" --out "<output-folder>" --model <model>
   ```
   *(Note: The pipeline automatically discovers and prioritizes pre-compressed siblings `-speed-1-compressed.mp4` / `-compressed.mp4`)*.
3. **Run Extraction & Refinement**:
   ```powershell
   dotnet run --no-build -- run --video "<video-path>" --out "<output-folder>" --model <model> --profile <profile> --skip-token-counting
   ```
   - Always run with `BypassSandbox: true` and `WaitMsBeforeAsync: 10000` (runs in background).
   - Use `--skip-token-counting` to save request quota.
   - Quotas on Free Tier: 20 RPD per model per API profile. Profiles: 0 (`automated-content-extraction`), 1 (`ai-studio-test-project-1`), 2 (`ai-studio-test-project-2`), 3 (`ai-studio-test-project-3`), 4 (`latex-refinement`).
4. **LaTeX Integration**:
   - Once extraction + refinement finishes (producing `step4-...-final.tex`), copy `step4-...-final.tex` to `<latex-project>\content\`.
   - Add `\include{content/step4-...-final.tex}` into the main `.tex` file in chronological order.
   - Verify compilation: `lualatex -interaction=nonstopmode <main>.tex`.

