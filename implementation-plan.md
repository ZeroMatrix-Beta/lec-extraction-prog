# Implementation Plan — Refactoring `lec-extraction-prog`

**Status:** Phase 3 done, Phase 4 in progress · **Baseline commit:** `22c83bf` · **Date:** 2026-07-26 · **Last updated:** 2026-07-28

**Note (2026-07-28):** Phase 3 is now fully closed — the three items a prior
session left open (`VideoSegmentProducer`, `TexDocumentWriter`,
`ContextCacheCoordinator`/`PrefixCachePrimer`) were all investigated this
session. `VideoSegmentProducer` and `TexDocumentWriter` turned out to be
genuine byte-identical (or near-identical) duplication and were extracted.
`ContextCacheCoordinator`/`PrefixCachePrimer` were investigated and confirmed
to be the "hardest, most-drifted pair" the plan predicted — real per-callsite
divergence, not extracted further (see Phase 3 section below for the
reasoning, same category as `SystemInstructionLoader`/`RefinementLauncher`).
One safe sub-extraction did come out of that investigation: the two inline
cache-creation blocks duplicated *within* `LatexRefinementSession.ExecuteGenerativeStepAsync`
itself were merged. Now moving into **Phase 4** — decomposing the god methods
(`ProcessFilesAsync`, `ExecuteGenerativeStepAsync`, etc., see §4). Same
caution as before applies here too: these are the ~1900-line twin classes
with zero automated test coverage over this logic and real paid-API cost to
validate against, so each split is verified by build + UI-string diff, one
method at a time, one commit at a time.

**What Phase 3 did NOT do, permanently** (investigated and deliberately left
unmerged — real per-backend/per-callsite divergence, not drift to fix):
* `SystemInstructionLoader` — AI Studio's implicit prefix-cache warm-up has
  no Vertex equivalent.
* `RefinementLauncher` — AI Studio's `applyParams` override + dedicated API
  key resolution has no Vertex equivalent.
* `ContextCacheCoordinator` / `PrefixCachePrimer` — extraction-session cache
  creation includes history `Contents`, refinement-session cache creation is
  plain system-instruction text; several console strings differ per call
  site in ways that would break the frozen-UI-strings rule if merged.
* The `CleanupGcsBucketAsync` / `ForcePurgeGcsBucketAsync` chat-session pair
  — real behavioral differences between them (free-tier guard, richer
  Vertex error diagnostics, mixed EN/DE strings).

---

## 0. Baseline (measured, not guessed)

| Metric | Value |
|---|---|
| C# files | 37 (all in the repository root, no folders) |
| C# lines | 11 563 |
| Build | `dotnet build` → **0 Warning(s), 0 Fehler** ✅ |
| Automated tests | **none** |
| Working tree | clean |

This baseline matters: **the build is already at 0/0**, which is the bar rule 7 of
`.agents/rules/AGENTS.md` demands. Every phase below must end at 0/0 again.

---

## 1. What is actually wrong (evidence, not opinion)

### 1.1 Twin classes — the single biggest problem

Four pairs of classes are structural clones that were copy-pasted and then drifted:

| Pair A | Pair B | Shared distinct lines |
|---|---|---|
| `AiStudioAutoExtractionSession.cs` (1 928) | `VertexAutoExtractionSession.cs` (1 854) | 693 |
| `DirectAiChatSessionAiStudio.cs` (757) | `DirectAiChatSessionVertex.cs` (649) | 323 |
| `AiStudioAutoExtractionConfig.cs` (177) | `VertexAutoExtractionConfig.cs` (115) | — |
| `DirectAiChatSessionAiStudioConfig.cs` | `DirectAiChatSessionVertexConfig.cs` | — |

The two extraction sessions expose an almost identical method set —
`StartAsync`, `SetupContextAndProcessAsync`, `EnsureSessionSetupAsync`,
`ProcessYouTubeTasksAsync`, `PrintCommandsMenu`, `ReplLoopAsync`, `SelectModel`,
`DebugChatAsync`, `ProcessFilesAsync`, `GetUniqueTexPath`,
`PrepareAndUploadPartAsync`, `GenerateTexFromUploadedPartAsync`,
`SupportsThinking`. The two chat sessions likewise share
`RunChatSessionAsync`, `PromptWithCommands`, `ShowCommands`,
`TryHandleBuiltInCommandsAsync`, `StreamGeminiResponseAsync`,
`GetInitialHistoryCommand`, `SupportsThinking`.

**~3 400 lines of the 11 563 are duplicated logic.** Every bug fix currently has
to be applied twice, and the drift between the twins is already visible (the
AI Studio session grew `WarmUpSystemInstructionCacheAsync` and the dummy-part0
prefix cache; the Vertex session grew `InitializeContextCachingAsync` and
`CleanupBucketAsync`).

### 1.2 Verbatim duplicated helpers

* `private static bool SupportsThinking(string)` — copy-pasted **5×**
  (both extraction sessions, both chat sessions, `LatexRefinementSession`).
* `private static string GetUniqueTexPath(string)` — copy-pasted **2×**.
* Bucket cleanup exists three times under three different names:
  `CleanupBucketAsync` (Vertex extraction), `CleanupGcsBucketAsync` (AI Studio
  chat), `ForcePurgeGcsBucketAsync` (Vertex chat).

### 1.3 God methods

| Method | File | Lines |
|---|---|---|
| `ProcessFilesAsync` | `AiStudioAutoExtractionSession.cs:1099` | **450** |
| `ExecuteGenerativeStepAsync` | `LatexRefinementSession.cs:703` | **441** |
| `ProcessFilesAsync` | `VertexAutoExtractionSession.cs:1052` | **447** |
| `GenerateTexFromUploadedPartAsync` | both extraction sessions | **242 / 239** |
| `ExecutePdfFixAttemptAsync` | `LatexRefinementSession.cs:1168` | 202 |
| `StreamGeminiResponseAsync` | both chat sessions | 178 / 164 |
| `DebugChatAsync` | both extraction sessions | 155 / 161 |
| `TryHandleBuiltInCommandsAsync` | both chat sessions | 149 / 137 |
| `RunAntiGravityAgentFixLoopAsync` | `LatexRefinementSession.cs:1370` | 150 |

`ProcessFilesAsync` alone contains: chronological sorting, a bounded-channel
producer/consumer pipeline, FFmpeg cache validation, video preprocessing,
splitting, part renaming, per-part resume-from-disk caching, parallel
pre-upload scheduling, rate-limit pacing, audio extraction scheduling, LaTeX
header formatting, offset-file generation, token accounting, failure rollback,
refinement-client construction and refinement session launch. That is at least
**twelve** distinct responsibilities in one method body.

### 1.4 Nesting

Lines indented ≥ 5 levels / ≥ 6 levels:

```
542 / 281   VertexAutoExtractionSession.cs
379 / 162   AiStudioAutoExtractionSession.cs
286 / 141   LatexRefinementSession.cs
154 /  50   ConsoleUiHelper.cs
 92 /  33   DirectAiChatSessionAiStudio.cs
 88 /  49   Program.cs
```

Almost all of it is `if (x != null) { if (y) { foreach { if { … } } } }` that
guard clauses would flatten.

### 1.5 Anonymous tuples used as domain types

The channel payload in `ProcessFilesAsync` is a **six-element tuple**:

```csharp
Channel.CreateBounded<(string originalFile, string fileSpecificOutputFolder,
    string tmpFolderForFile, List<(string FilePath, double StartTime)> parts,
    bool isCached, double fullOriginalVideoDuration)>(…)
```

Two more tuple-returning methods compound it:
`(bool success, string? parsedPrompt, List<Part> attachmentParts)` and
`(string texOutput, int inputTokens, int outputTokens, int cachedTokens)`.
Token counts are then tracked in four parallel loose `int` locals.

### 1.6 Namespaces that describe nothing

| Type | Current namespace | Problem |
|---|---|---|
| `ConsoleUiHelper` | `FfmpegUtilities` | It is the *generic* console UI, used by `Program` for source folders, models and API keys — nothing to do with FFmpeg. |
| `LatexRefinementSession` | `DirectChatAiInteraction` | It is a batch pipeline, not a chat. |
| `RefinementUiHelper` | `AutoExtraction` | It drives refinement, not extraction. |
| `IAutoExtractionConfig` | `Config` | Its implementations live in `AutoExtraction`. |
| `AttachmentHandler`, `ApiResilience`, `SessionLogger`, `StringHelper` | `Infrastructure` | Grab-bag; `AttachmentHandler` is Google-AI-specific. |
| `YouTubeTranscriptionTask` | `Config` | It is a domain model, not configuration. |

There is no root namespace at all — nine unrelated top-level namespaces
(`AutoExtraction`, `Config`, `Infrastructure`, `FfmpegUtilities`,
`DocumentUtilities`, `GoogleGenAi`, `DirectChatAiInteraction`,
`DirectChatAiInteraction.AiStudio`, `DirectChatAiInteraction.Vertex`).

### 1.7 File / type mismatches and multi-type files

* `GeminiClientBuilder.cs` contains `GoogleAiClientBuilder`.
* `FfmpegInteractiveMenu.cs` contains `FfmpegInteractiveSession`.
* `AppConfig.cs` contains three types (`ConfigLoader<T>`, `AppConfigOptions`, `AppConfig`).
* `LatexRefinementSessionConfig.cs` contains four types (`BackendParameters`,
  `RefinementStepConfig`, `PdfCompilationConfig`, `LatexRefinementSessionConfig`).
* `DirectAiChatSessionAiStudioConfig.cs` contains two types.
* `YouTubeTranscriptionTask.cs` contains two types.

### 1.8 Dead files

| File | Content |
|---|---|
| `ConsoleUserInterface.cs` | 0 bytes |
| `ThinkingLevelParser.cs` | 0 bytes |
| `RunGit.cs` | one comment: "Deleted helper script" |
| `TestJsonComments.cs` | two comments: "…now disabled. You can safely delete this file" |
| `DirectAiChatSessionVertexAIConfig.json` | 0 bytes, and never loaded — `ConfigLoader<T>` resolves `{typeof(T).Name}.json`, but this type is only ever reached as the nested `DirectAiChatSessionVertexConfig.AI` property |

Note: `DirectAiChatSessionVertexAIConfig.**cs**` is **not** dead — it is the
Vertex counterpart of `DirectAiChatSessionAiStudioGenerationConfig` and is used
by `DirectAiChatSessionVertex`. It is kept, renamed to
`VertexChatGenerationConfig` in Phase 7.

Loose scripts and artefacts also sit in the project root: `fix_vertex.py`,
`get_git.py`, `read_git_obj.py`, `git_show_result.txt` (94 KB),
`chat_log.md` (**11 MB**), `build_test_bin/`.

### 1.9 Telescoping constructors

`LatexRefinementSession` has **four** public constructors (lines 29, 39, 49, 59)
that differ only in how much of the same state they set — the classic signal for
an options object.

### 1.10 Naming

Representative offenders: `Program.Activate_Vertex` (snake_case public property,
also a global mutable flag), `_speed`, `f`, `result`, `hasErrors`, `channel`,
`producerTask`, `startAudioTask`, `applyParams`, `ExecuteStep1MergeAsync` /
`ExecuteStep2SpeechRefinementAsync` / `ExecuteStep3LastRefinementAsync` (config
ordering leaked into method names), `PrintCommandsMenu` vs `ShowCommands` for
the same concept, `ExtractionHelpers` (a 583-line grab-bag of seven unrelated
concerns).

---

## 2. Guiding constraints

1. **Behaviour is frozen.** Every user-facing console string stays
   byte-identical, in German, in the same order. The UI is the de-facto
   specification and, with no tests, our only characterization harness.
   UI improvements are collected in §5 and executed **later**, separately.
2. **`dotnet build` ends every phase at `0 Warning(s), 0 Fehler`.**
3. **`.agents/rules/AGENTS.md` is binding** — `Count > 0` over `Any()`,
   target-typed `new()`, `[GeneratedRegex]`, no unused parameters, no silent
   `catch`, and **`[AI Context]` / `[Human]` comments are preserved** (moved
   with the code they document, never deleted).
4. **Identifiers in English, user-facing strings in German** — the existing
   convention, kept.
5. **One phase = one commit.** Each is independently revertable.

---

## 3. Target architecture

### 3.1 Folders and namespaces

Root namespace `LectureExtraction`, one folder per bounded context:

```
src/
├─ App/                    LectureExtraction.App
│   ├─ Program.cs                     entry point only (~20 lines)
│   ├─ MainMenu.cs                    top-level loop
│   ├─ SourceFolderMenu.cs
│   ├─ ApiKeyProfileMenu.cs
│   └─ SessionFactory.cs              all the wiring now inlined in Program
├─ Configuration/          LectureExtraction.Configuration
│   ├─ ConfigLoader.cs
│   ├─ AppConfig.cs / AppConfigOptions.cs
│   ├─ ExtractionConfig.cs            (+ AiStudio/Vertex specialisations)
│   ├─ ChatSessionConfig.cs           (+ specialisations)
│   ├─ RefinementConfig.cs / RefinementStepConfig.cs / BackendParameters.cs
│   │                                 / PdfCompilationConfig.cs
│   └─ FfmpegConfig.cs, SessionLoggerConfig.cs
├─ Extraction/             LectureExtraction.Extraction
│   ├─ LectureExtractionSession.cs    the single unified session
│   ├─ VideoSegmentProducer.cs        FFmpeg producer half of the pipeline
│   ├─ SegmentTranscriber.cs          upload + generate for one segment
│   ├─ TexDocumentWriter.cs           headers, offset files, unique paths
│   ├─ AudioTrackExtractor.cs
│   ├─ RefinementLauncher.cs
│   ├─ ExtractionRepl.cs              interactive command loop
│   ├─ YouTubeTaskRunner.cs
│   └─ Model/  PreparedVideo.cs, VideoSegment.cs, SegmentUpload.cs,
│              SegmentTranscript.cs, TokenUsage.cs
├─ Refinement/             LectureExtraction.Refinement
│   ├─ RefinementPipeline.cs          replaces LatexRefinementSession
│   ├─ RefinementStepRunner.cs        replaces ExecuteGenerativeStepAsync
│   ├─ Steps/  MergeStep.cs, SpeechRefinementStep.cs, FinalPolishStep.cs
│   ├─ PdfBuilder.cs                  compile + retry + log formatting
│   ├─ PdfRepairLoop.cs               ExecutePdfFixAttemptAsync + agent loop
│   └─ RefinementOptions.cs           kills the 4 telescoping constructors
├─ Chat/                   LectureExtraction.Chat
│   ├─ InteractiveChatSession.cs      the single unified chat session
│   ├─ ChatCommandHandler.cs
│   └─ ResponseStreamPrinter.cs
├─ GoogleAi/               LectureExtraction.GoogleAi
│   ├─ IAiBackend.cs + AiStudioBackend.cs + VertexBackend.cs
│   ├─ GoogleAiClientBuilder.cs
│   ├─ AttachmentUploader.cs          was AttachmentHandler
│   ├─ ApiRetryPolicy.cs              was ApiResilience
│   ├─ GenerationConfigBuilder.cs
│   ├─ ModelCapabilities.cs           the single SupportsThinking
│   ├─ SystemInstructionLoader.cs
│   ├─ ContextCache/  ContextCacheCoordinator.cs, ContextCacheState.cs,
│   │                 ContextCacheStore.cs
│   └─ GcsWorkspace.cs                the one bucket-purge implementation
├─ Media/                  LectureExtraction.Media
│   ├─ FfmpegToolkit.cs, FfmpegInteractiveSession.cs, VideoDateParser.cs
├─ Latex/                  LectureExtraction.Latex
│   ├─ LatexCompiler.cs, LatexTimestampAdjuster.cs, LatexResponseCleaner.cs
├─ ConsoleUi/              LectureExtraction.ConsoleUi
│   ├─ DirectoryTreeRenderer.cs, FileSelectionPrompt.cs,
│   ├─ ConfigurationPrompts.cs, InteractiveDelay.cs
└─ Infrastructure/         LectureExtraction.Infrastructure
    ├─ SessionLogger.cs, StringExtensions.cs, PathHelpers.cs
```

### 3.2 The backend abstraction (removes the twins)

```csharp
namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Abstracts the two Google generative backends so that the
/// extraction and chat pipelines exist exactly once. AI Studio uses an API key
/// and the Files API; Vertex uses a GCP project plus a GCS bucket and is the
/// only backend supporting explicit context caching.
/// [Human] Kapselt die Unterschiede zwischen AI Studio und Vertex AI, damit
/// die Pipeline nur einmal existiert.
/// </summary>
public interface IAiBackend {
    string DisplayName { get; }                 // "AI Studio" / "Vertex AI"
    IReadOnlyList<string> AvailableModels { get; }
    bool SupportsExplicitContextCaching { get; }
    Client CreateClient();
    IAttachmentUploader CreateUploader(string workingFolder);
    Task PurgeRemoteWorkspaceAsync();           // no-op on AI Studio
}
```

`LectureExtractionSession` and `InteractiveChatSession` then take an
`IAiBackend` and exist once each. Backend-only features become explicit:

* AI Studio only: prefix-cache priming (`dummy-part0.tex`) → `PrefixCachePrimer`.
* Vertex only: explicit context caching → `ContextCacheCoordinator`;
  GCS purge → `GcsWorkspace`.

### 3.3 Domain records replacing the tuples

```csharp
public readonly record struct TokenUsage(int Input, int Output, int Cached) {
    public int Fresh => Math.Max(0, Input - Cached);
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.Cached + b.Cached);
}

public sealed record VideoSegment(string FilePath, double StartTimeSeconds);

public sealed record PreparedVideo(
    string SourceVideoPath,
    string OutputFolder,
    string TempFolder,
    IReadOnlyList<VideoSegment> Segments,
    bool CameFromCache,
    double SourceDurationSeconds);

public sealed record SegmentUpload(bool Succeeded, string? Prompt, List<Part> Attachments);
public sealed record SegmentTranscript(string LatexBody, TokenUsage Usage);
```

The four parallel `fileTotal*Tokens` locals collapse into one accumulating
`TokenUsage`; `partFreshTokens` becomes the computed `Fresh` property.

---

## 4. Phased execution

Ordered so that mechanical, compiler-verified changes come first and risky
semantic changes come last, on an already-clean base.

### How to verify any phase

```bash
dotnet build                                              # must print 0 Warnung(en), 0 Fehler
dotnet test                                               # must be all green
./tools/dump-ui-strings.sh | diff docs/ui-strings.baseline.txt -   # must be empty
```

The third check is the important one. It extracts every `Console.Write*` call in
the project, sorts and deduplicates them, so the result is independent of which
file a string lives in. Moving code between files therefore produces **no diff**
— only *changing what the user sees* does.

### Phase 0 — Safety net · low risk · ✅ DONE

1. Delete the dead files: `ConsoleUserInterface.cs`, `ThinkingLevelParser.cs`,
   `RunGit.cs`, `TestJsonComments.cs`, `DirectAiChatSessionVertexAIConfig.cs`,
   `DirectAiChatSessionVertexAIConfig.json`.
2. Move loose scripts and artefacts out of the project root into `tools/`
   (`fix_vertex.py`, `get_git.py`, `read_git_obj.py`) and add `chat_log.md`
   (11 MB), `git_show_result.txt`, `build_test_bin/` to `.gitignore`.
3. Add `lec-extraction-prog.sln` + `tests/LectureExtraction.Tests` (xUnit)
   covering the **pure** functions that later phases will move — these are the
   only things testable without hitting a paid API:
   * `VideoDateParser.Parse` (all supported filename schemes + invalid input)
   * `LatexTimestampHelper.AdjustTimestamps` /
     `ExtractContentWithoutTimestampHeader`
   * `ExtractionHelpers.CleanLatexResponse`, `NormalizeRelativePath`,
     `CleanCopySuffix`, `FindCommonBaseDirectory`, `GenerateMarkdownFileTree`
   * `StringHelper.FixMalformedEndTags`, `Truncate`, `RemoveNewLines`
   * `SupportsThinking` (pinning behaviour before the 5 copies are merged)
   * `GetUniqueTexPath` collision handling
4. Capture a **console-string inventory**: extract every string literal that
   reaches `Console.Write*` into `docs/ui-strings.baseline.txt`. Re-generating
   and diffing this file is the regression check for phases 1–7.

*Exit:* build 0/0, tests green, baseline file committed.

**Outcome — all exit criteria met.**

* Deleted: `ConsoleUserInterface.cs`, `ThinkingLevelParser.cs`, `RunGit.cs`,
  `TestJsonComments.cs`, `DirectAiChatSessionVertexAIConfig.json`.
* Moved to `tools/`: `fix_vertex.py`, `get_git.py`, `read_git_obj.py`.
* Untracked but kept on disk (`git rm --cached` + `.gitignore`):
  `build_test_bin/` (16 MB, 51 tracked files including DLLs) and
  `git_show_result.txt` (94 KB).
* Added `lec-extraction-prog.sln` and `tests/LectureExtraction.Tests` —
  **80 tests, all green**, covering `VideoDateParser`, `LatexTimestampHelper`,
  `StringHelper`, the pure helpers in `ExtractionHelpers`, and config binding.
* Added `tools/dump-ui-strings.sh` + `docs/ui-strings.baseline.txt`
  (**624 unique console strings**).
* Added `<Compile Remove="tests/**" />` to the main csproj — required, because
  the project file sits in the repository root and its default globs would
  otherwise compile the test project into the executable.

Two findings worth recording:

1. **All five `SupportsThinking` copies are byte-identical**, so merging them in
   Phase 3 is provably safe.
2. `ExtractionHelpers.SystemMessageRegex` only strips `%[SYSTEM]`-style chatter
   when the comment marker is flush against the bracket. The normal LaTeX form
   `% [SYSTEM] …` (with a space) leaks into the `.tex` output. This is pinned as
   a *known-gap* test, not silently "fixed" — it is a behaviour change and
   belongs in its own commit, not in a refactor.

### Phase 1 — Layout, namespaces, one type per file · low risk, high churn

Purely mechanical and fully compiler-verified.

1. Create the `src/` tree from §3.1; move every file into it.
2. Rewrite namespaces to the `LectureExtraction.*` scheme.
3. Split the multi-type files (§1.7); rename files so filename == type name
   (`GeminiClientBuilder.cs` → `GoogleAiClientBuilder.cs`,
   `FfmpegInteractiveMenu.cs` → `FfmpegInteractiveSession.cs`).
4. Replace the fully-qualified noise that the flat layout forced —
   `System.IO.File.…` (used ~120×), `FfmpegUtilities.FfmpegToolkit.…`,
   `DirectChatAiInteraction.LatexRefinementSession`, `AutoExtraction.…` —
   with plain `using` directives.

*Exit:* build 0/0, tests green, UI-string diff empty.

**Outcome — done, in commits `2a2ec17` and `08eebec` (executed together with part of
Phase 3, ahead of this plan document).**

* `src/` now holds one folder per bounded context exactly as in §3.1
  (`App`, `Configuration`, `Extraction`, `Refinement`, `Chat`, `GoogleAi`
  (+ `GoogleAi/ContextCache`), `Media`, `Latex`, `ConsoleUi`, `Infrastructure`).
* Every file's namespace is `LectureExtraction.<Folder>` — verified by grepping
  all `namespace` declarations under `src/`, no stragglers left on the old
  `AutoExtraction` / `Config` / `FfmpegUtilities` / `DirectChatAiInteraction` /
  `GoogleGenAi` names.
* Not yet done from the original Phase 1 checklist: some config classes were
  split into one-type-per-file as part of this work (`BackendParameters.cs`,
  `RefinementStepConfig.cs`, `PdfCompilationConfig.cs`,
  `DirectAiChatSessionAiStudioGenerationConfig.cs` etc. now exist standalone),
  which is technically Phase 3 scope pulled forward. Twin classes
  (`AiStudioAutoExtractionSession` 1932 lines / `VertexAutoExtractionSession`
  1859 lines) and the god methods inside them are untouched.
* Build 0/0, 80 tests green, confirmed 2026-07-27.

### Phase 2 — Domain model · low risk

1. Introduce the records of §3.3.
2. Replace the 6-tuple channel, the two tuple-returning methods and the four
   loose token counters, in **both** extraction sessions.

*Exit:* build 0/0, UI-string diff empty.

**Outcome — done, commit `409542a`.** `TokenUsage`, `VideoSegment` (placed in
`Media`, not `Extraction.Model`, since `FfmpegToolkit` produces it and must
not depend on the extraction pipeline), `PreparedVideo`, `SegmentUpload`,
`SegmentTranscript` all added and wired into both extraction sessions. Build
0/0, 85 tests green.

### Phase 3 — Extract shared services · medium risk

Pull genuinely shared code out of the twins into single implementations that
both twins call. The twins still exist after this phase — they just get thin.

| New type | Absorbs | Status |
|---|---|---|
| `ModelCapabilities` | the 5 `SupportsThinking` copies | **Done, commit `23c0c1b`** |
| `TexDocumentWriter` | 2× `GetUniqueTexPath` (**done, folded into existing `ExtractionHelpers` rather than a new type, commit `23c0c1b`**); `BuildTexPartHeader`/`BuildTexCombinedHeader`/`BuildTexModelParameterBlock` **done, commit `05080e3`** (AI Studio had these as methods, Vertex had the identical templates inlined — confirmed byte-identical before merging) | **Done** |
| `AudioTrackExtractor` | the `startAudioTask` local function + cache check | **Done** |
| `GcsWorkspace` | the 2 byte-identical `CleanupBucketAsync` copies (Vertex extraction, LaTeX refinement) — **done**. `CleanupGcsBucketAsync` (AI Studio chat) / `ForcePurgeGcsBucketAsync` (Vertex chat) intentionally left alone: real behavioral differences (`IsAiStudio` free-tier guard, richer Vertex error diagnostics incl. a billing-account branch, English vs German strings) | Partial |
| `SystemInstructionLoader` | the path-resolution + concat block repeated in both sessions **and** in `ExecuteGenerativeStepAsync` | Downgraded — see progress note, not pursuing further |
| `VideoSegmentProducer` | the whole FFmpeg producer lambda (~115 lines) from both `ProcessFilesAsync` | **Done, commit `d9b54d7`** — confirmed byte-identical (modulo cosmetic naming/regex-method naming) before merging |
| `RefinementLauncher` | refinement-client construction, `applyParams`, refinement session start (~70 lines, both sessions) | Investigated, real per-backend divergence (AI-Studio-only `applyParams` override + dedicated API key resolution; Vertex has neither, uses ADC) — not pursuing further |
| `GenerationConfigBuilder` | thinking/temperature/topP/topK/maxTokens assembly, repeated 5× | Investigated; found and fixed a real clamp-bug (AI Studio wasn't capping `ThinkingBudget` at 32768 like Vertex does — see progress note). Not extracting a shared type beyond that fix |
| `ContextCacheCoordinator` | the ~150-line cache create/validate/extend block in `ExecuteGenerativeStepAsync` + `InitializeContextCachingAsync` | Investigated (2026-07-28), real per-callsite divergence — see below. Not extracting the cross-file coordinator. Did extract the one safe piece: the two inline cache-creation blocks duplicated *within* `ExecuteGenerativeStepAsync` itself, commit `b3374a5` |
| `PrefixCachePrimer` | `GetDummyPart0Content`, `WarmUpSystemInstructionCacheAsync`, `WarmUpWithBatchedHistoryAsync` | Investigated (2026-07-28): this is AI-Studio-only code with no Vertex counterpart at all, so there is no cross-twin duplication to remove — extracting it into its own file would be a pure organizational move (shrinking `AiStudioAutoExtractionSession`), not deduplication. Deprioritized below Phase 4 |

Also split the two grab-bags:
* `ExtractionHelpers` (611 lines) → `HistoryFileResolver`, `FileTreeRenderer`,
  `LatexResponseCleaner` (moved to `Latex/`, matching §3.1),
  `InteractiveDelay` (moved to `ConsoleUi/`, matching §3.1),
  `VideoBatchSelector`, `YouTubeTaskPrompt`, `ModelSyncService`. **Done.**
  `ExtractionHelpers` itself now only holds `GetUniqueTexPath` and
  `LogSystemInstructionDumpAsync` (deliberately not split further — no
  natural home for either in the target §3.1 list). Every extracted method
  body was diffed byte-for-byte against the original before being wired up,
  including the unicode/emoji-bearing ones (📁 tree icons, ⏳ delay marker,
  the `‐`-`―` copy-suffix regex) after a transcription slip on the
  first attempt was caught and fixed. All ~99 call sites across
  `App`/`Chat`/`ConsoleUi`/`Extraction`/`GoogleAi`/`Refinement` updated to the
  new type names; the old `ExtractionHelpersTests.cs` was split the same way
  into `LatexResponseCleanerTests.cs` / `FileTreeRendererTests.cs`.
* `ConsoleUiHelper` (547 lines) → `DirectoryTreeRenderer`,
  `FileSelectionPrompt`, `ConfigurationPrompts`. **Done.** (Already lived in
  `LectureExtraction.ConsoleUi`, not `FfmpegUtilities` — that part of the
  plan item was stale, fixed in an earlier phase.) Same byte-for-byte diff
  discipline as the `ExtractionHelpers` split; all call sites across
  `App`/`Chat`/`Extraction`/`Media` updated; `ConsoleUiHelper.cs` deleted.

**Phase 3 is now fully closed (2026-07-28).** All items in the table above
are either done or investigated-and-deliberately-not-merged for documented
reasons. Build 0/0, 85 tests green, UI-string diff empty after every commit.
`TexDocumentWriter`'s offset-file *writing* (the two `File.WriteAllTextAsync`
calls per twin) stays inline in each `ProcessFilesAsync` — it differs only in
which path gets written to and isn't worth a wrapper on its own.

*Exit:* build 0/0, UI-string diff empty. Both extraction sessions should now be
well under 1 000 lines each. **Not yet true** — see Phase 4, this is what the
god-method decomposition is for.

**Progress note (2026-07-27):** Items with byte-identical duplicate bodies
(confirmed by diffing before merging) are done — those carried zero
judgment-call risk. Everything else in this table involves code that has
already visibly drifted between the twins (see §1.1) and needs an actual
side-by-side read, not a mechanical move.

`SystemInstructionLoader` was investigated and downgraded: the genuinely
shared file-resolution/tree-printing work already lives in `ExtractionHelpers`
(both sessions already call it identically). What's left un-shared is thin,
backend-specific orchestration — notably AI Studio's implicit-prefix-cache
warm-up (`WarmUpWithBatchedHistoryAsync` / `WarmUpSystemInstructionCacheAsync`),
which has **no Vertex equivalent at all** because Vertex uses the real
`CachedContent` API (`ContextCacheCoordinator`/`InitializeContextCachingAsync`)
instead of an implicit-prefix trick. Confirmed with the user — this is a
correct, permanent per-backend difference, not drift to fix. Not extracting
further; not worth a new type for ~10 lines of glue plus one small
AI-Studio-only warning branch.

`RefinementLauncher` was investigated and dropped for the same reason: AI
Studio applies a 3-step `applyParams` override (copies the chosen extraction
model/temperature/topP/topK/maxTokens/thinking settings onto each refinement
step) and resolves its own dedicated API key by env name; Vertex does neither
(no param override block at all, relies on ADC instead of an API key). The
only line genuinely identical between them is the "deactivate Step 1 merger
when `NumberOfParts <= 1`" check — not worth extracting alone.

`GenerationConfigBuilder` (the `GenerateContentConfig` assembly repeated
~5×) was investigated and surfaced a real discrepancy: at both AI Studio
`ThinkingBudget` call sites (`DebugChatAsync` and the main
`GenerateTexFromUploadedPartAsync` path), the budget was sent unclamped,
while **both** Vertex call sites clamped it to 32768 before sending. Websearch
on 2026-07-27 found no evidence that AI Studio and Vertex have different
`thinkingBudget` ceilings for the same model — both platforms document the
same 0-32768 range for the Gemini 2.5-era models this code path targets — so
the AI Studio side looks like a missing safety clamp rather than an
intentional per-backend limit. Fixed by adding the same `if (budget > 32768)
budget = 32768;` clamp to both AI Studio sites, matching Vertex. No
configured `ThinkingBudget` value in any JSON file exceeds 32768 today
(32765 / 32768), so this changes zero observed behavior right now — it's a
guard against future misconfiguration. UI-string diff confirmed clean.
Not extracting a shared `GenerationConfigBuilder` type beyond this — the
remaining 3-4 call sites per session were not individually checked for
further drift; assume more may exist if this area gets revisited.

**Progress note (2026-07-28):** Finished the remaining three items.
`VideoSegmentProducer` turned out to be genuinely byte-identical between the
twins (only cosmetic local-variable and generated-regex-method naming
differed) and was extracted verbatim. `TexDocumentWriter`'s header builders
were likewise byte-identical in content (AI Studio already had them as
methods; Vertex had the same string templates inlined) and were extracted.

`ContextCacheCoordinator`/`PrefixCachePrimer` were investigated and confirmed
as the plan predicted — the hardest, most-drifted pair, real divergence, not
just drift:
* Cache *creation* differs in shape: `VertexAutoExtractionSession.InitializeContextCachingAsync`
  builds `SystemInstruction` from a `Parts` list plus optional history
  `Contents`; `LatexRefinementSession.ExecuteGenerativeStepAsync` builds it
  from a single plain-text `SystemInstruction`. Forcing both through one
  shape would add real coupling for a handful of shared lines.
* Cache *validate-or-extend* logic (checking remaining TTL, extending near
  expiry, else a remote validity check) is structurally similar between the
  two call sites, but the German console messages printed at each step
  genuinely differ (e.g. `"Nur noch {remainingMin} min verbleibend..."` vs
  `"TTL knapp ({remainingMin} min)..."`) — merging them would violate the
  frozen-UI-strings rule (§2.1) for a ~20-line saving.
* One safe win *was* found: `ExecuteGenerativeStepAsync` itself had **two**
  near-identical inline cache-creation blocks (initial miss vs.
  expired-cache recreate) — pure same-file, same-method duplication with no
  cross-backend risk. Extracted into a private `CreateContextCacheAsync`
  helper, commit `b3374a5`.
* `PrefixCachePrimer` (AI Studio's implicit-prefix warm-up) has no Vertex
  equivalent at all — nothing to deduplicate, only a possible file-organization
  move. Deprioritized below Phase 4 since it doesn't reduce duplication.

Suggested order for what remains: the two grab-bag splits below (mechanical,
do anytime, no AI-call behavior involved — safest remaining Phase 3 work,
done), then Phase 4.

Also, in passing: `AppConfig.DefaultModel` / `AppConfig.RefinementModel`
(and the matching `appsettings.json` keys) were found unused — every session
already carries its own `Model` array in its own JSON config — and removed.

### Phase 4 — Decompose the god methods · medium risk

Break up what remains and flatten nesting with guard clauses and early returns.

* `ProcessFilesAsync` → `RunBatchAsync` (orchestration, ~40 lines) +
  `ProcessPreparedVideoAsync` + `TranscribeSegmentAsync` +
  `FinalizeVideoOutputAsync` + `RollbackFailedVideoAsync`.
* `GenerateTexFromUploadedPartAsync` → `BuildRequestAsync` +
  `StreamAndCollectAsync` + `RecordUsage`.
* `ExecuteGenerativeStepAsync` → `RunRefinementStepAsync` (~50 lines) +
  `SystemInstructionLoader` + `ContextCacheCoordinator` +
  `GenerationConfigBuilder` + `StreamAndCollectAsync` + `WriteStepOutputAsync`.
* `ExecutePdfFixAttemptAsync` / `RunAntiGravityAgentFixLoopAsync` → `PdfRepairLoop`.
* `TryHandleBuiltInCommandsAsync` → a command table
  (`IReadOnlyDictionary<string, ChatCommand>`) instead of an if/else ladder.
* `StreamGeminiResponseAsync` → `ResponseStreamPrinter`.
* `DebugChatAsync`, `ReplLoopAsync` → `ExtractionRepl` with one method per command.

**Target: no method over 60 lines, no block deeper than 3 levels.**

*Exit:* build 0/0, UI-string diff empty, nesting metric re-measured.

### Phase 5 — Unify the twins · highest risk · see §6 open decision

1. Introduce `IAiBackend` + `AiStudioBackend` + `VertexBackend`.
2. Merge `AiStudioAutoExtractionSession` + `VertexAutoExtractionSession` →
   `LectureExtractionSession`.
3. Merge `DirectAiChatSessionAiStudio` + `DirectAiChatSessionVertex` →
   `InteractiveChatSession`.
4. Merge the config twins onto a shared base
   (`ExtractionConfig` ← `AiStudioExtractionConfig`, `VertexExtractionConfig`),
   keeping the existing JSON files loadable **unchanged**.
5. Replace `LatexRefinementSession`'s four constructors with
   `RefinementOptions`.

*Exit:* build 0/0, UI-string diff empty, manual smoke test of menu paths 1–7.

### Phase 6 — Entry point and menus · low risk

1. `Program.cs` shrinks to `Main` + top-level exception handling (~20 lines).
2. Menu loops move to `MainMenu` / `SourceFolderMenu` / `ApiKeyProfileMenu`;
   the repeated `ConfigLoader<T>.Load()` → mutate → `Save()` triads become one
   `ConfigEditor<T>.Edit(…)` helper.
3. The API-key-env-name resolution — currently copy-pasted **four times** in
   `Program.cs` alone (lines 121-127, 218-225, plus the refinement variants) —
   becomes `ApiKeyProfileResolver.Resolve(profile, envNames)`.
4. `Program.Activate_Vertex` → `AppConfigOptions.IsVertexAiEnabled`, read from
   `appsettings.json` instead of a recompile-to-change static field.

### Phase 7 — Naming pass and documentation · low risk

Systematic rename of everything left. Representative table:

| Current | Proposed |
|---|---|
| `Program.Activate_Vertex` | `AppConfig.IsVertexAiEnabled` |
| `_speed` | `_playbackSpeedMultiplier` |
| `files` / `f` | `videoFilesToProcess` / `videoFile` |
| `hasErrors` | `anyVideoFailed` |
| `channel` / `producerTask` | `preparedVideoQueue` / `videoPreparationTask` |
| `result` | `segmentTranscript` |
| `startAudioTask` | `AudioTrackExtractor.EnsureStarted()` |
| `applyParams` | `ApplyModelParametersTo(BackendParameters)` |
| `GetDummyPart0Content` | `LoadPrefixCacheAnchorText` |
| `PrepareAndUploadPartAsync` | `UploadSegmentAndBuildPromptAsync` |
| `GenerateTexFromUploadedPartAsync` | `TranscribeSegmentToLatexAsync` |
| `GetUniqueTexPath` | `ResolveNonClashingTexPath` |
| `WarmUpSystemInstructionCacheAsync` | `PrimePrefixCacheAsync` |
| `AcknowledgeHistoryAsync` | `SendHistoryHandshakeAsync` |
| `DebugChatAsync` | `RunDiagnosticChatTurnAsync` |
| `ExecuteStep1MergeAsync` | `MergeSegmentsAndAlignTimestampsAsync` |
| `ExecuteStep2SpeechRefinementAsync` | `RefineAgainstSpeechAsync` |
| `ExecuteStep3LastRefinementAsync` | `ApplyFinalPolishAsync` |
| `ExecuteGenerativeStepAsync` | `RunRefinementStepAsync` |
| `ExecutePdfFixAttemptAsync` | `TryRepairFailedPdfBuildAsync` |
| `RunAntiGravityAgentFixLoopAsync` | `RunExternalAgentRepairLoopAsync` |
| `PrintCommandsMenu` / `ShowCommands` | `WriteCommandHelp` (one method) |
| `ConfirmOrChangeSourceFolder` | `PromptForSourceFolder` |
| `ApiResilience` | `ApiRetryPolicy` |
| `AttachmentHandler` | `AttachmentUploader` |
| `StringHelper` | `StringExtensions` |
| `LatexTimestampHelper` | `LatexTimestampAdjuster` |
| `FfmpegInteractiveSession` (in `FfmpegInteractiveMenu.cs`) | file renamed to match |

Then update `README.md`, `Documentation.md`, `GEMINI.md` and
`.agents/rules/AGENTS.md` to the new structure.

---

## 5. UI changes — collected, deliberately **not** executed now

Recorded here so they are not lost; to be done after the refactor lands, as its
own change, per your instruction.

1. The main menu re-loads `AiStudioAutoExtractionConfig` on **every** loop
   iteration purely to render one status line — cache it, invalidate on edit.
2. Menu option 2 is shown even when Vertex is disabled, then rejects the choice
   after selection — better to grey it out or hide it.
3. `"exit"` / `"quit"` handling is re-implemented in each of the ~6 menu loops
   with slightly different behaviour — unify into one `MenuPrompt`.
4. The `__EXIT__` magic string returned by `ConfirmOrChangeModel` should be a
   `bool TryPromptForModel(out string)` or a nullable return.
5. Invalid input prints `"Invalid choice."` in the main menu but
   `"Ungültige Auswahl."` in the sub-menus — inconsistent language.
6. Numbering is hard-coded in both the printed text and the `switch` — a menu
   table would keep them in sync automatically.
7. Progress/token reporting is `Console.WriteLine` scattered through the
   pipeline — a small `IProgressReporter` would let the pipeline stay silent
   and testable.
8. Bracket-tag vocabulary is inconsistent across the whole app: error is
   spelled `[FEHLER]` (20x), `[Fehler]` (5x), `[ERROR]` (1x), `[Error]` (1x)
   depending on which file printed it; same problem for warnings
   (`[WARNUNG]` / `[Warning]` / `[GCS Warnung]`), success
   (`[OK]` / `[SUCCESS]` / `[Erfolg]`), and info (`[INFO]` / `[Info]` /
   `[DEBUG]` / `[Debug]`). Found via `tools/dump-ui-strings.sh | grep -oE
   '\[[A-Za-z ]+\]' | sort | uniq -c | sort -rn` — 20+ variant spellings
   across 624 strings. Worth picking one canonical tag per severity level
   and a shared `SessionLogger`-style print helper, rather than a find/replace
   (some variants may be intentionally scoped, e.g. `[LaTeX Refinement] [FEHLER]`
   prefixing which subsystem failed).

---

## 6. Decisions taken

**Vertex AI — keep and unify behind `IAiBackend`.** `Program.Activate_Vertex` is
hard-coded `false`, so ~2 500 lines of Vertex code are currently unreachable, but
the capability is being preserved rather than deleted. Consequences:

* `IAiBackend` (§3.2) is part of the target architecture, and Phase 5 merges both
  twin pairs onto it.
* The Vertex path cannot be validated end-to-end (no reachable execution, paid
  API). It is therefore refactored **conservatively**: mechanical moves and
  renames only, no opportunistic logic changes, and its console strings are held
  byte-identical exactly like the AI Studio path.
* Vertex-only features stay explicit rather than being folded into the shared
  path: `ContextCacheCoordinator` and `GcsWorkspace` are reached only through
  `IAiBackend.SupportsExplicitContextCaching` / `PurgeRemoteWorkspaceAsync()`.

**Test project — yes.** Phase 0 adds `lec-extraction-prog.sln` and
`tests/LectureExtraction.Tests` (xUnit) covering the pure logic listed in
Phase 0 step 3.

---

## 7. Risk register

| Risk | Mitigation |
|---|---|
| No automated tests over the pipeline | Phase 0 adds tests for all pure logic; UI-string diff pins the rest; one commit per phase for cheap revert |
| Pipeline can only be validated by running against a **paid** API | Manual smoke test on one short video, once, after Phase 5 — not per phase |
| JSON config files must keep loading unchanged | Config property names are frozen; `ConfigLoader` comment-preserving round-trip is covered by a Phase 0 test |
| `ConfigLoader` writes back to the JSON files at runtime | Back up the six `*.json` config files before starting |
| Phase 1 touches every file (huge diff) | Kept purely mechanical; no logic edits mixed in, so review is a namespace/path scan |
| `[AI Context]` / `[Human]` comments lost during moves | Explicit checklist item per phase; grep count before/after must match |

---

## 8. Suggested order of work

```
Phase 0  Safety net              ~small     independent
Phase 1  Layout + namespaces     ~large     mechanical
Phase 2  Domain records          ~small
Phase 3  Extract services        ~large     ← biggest single win
Phase 4  Split god methods       ~large
Phase 5  Unify twins             ~medium    ← blocked on §6
Phase 6  Entry point + menus     ~small
Phase 7  Naming + docs           ~medium
```

Phases 0–4 are worth doing regardless of how §6 is decided, and already remove
the great majority of the spaghetti. Phase 3 alone should cut roughly 1 500
duplicated lines.
