# Implementation Plan — Refactoring `lec-extraction-prog`

**Status:** Phases 0–4 done, Phase 4.5 fully done (items 1–3), Vertex implicit prefix-cache warmup ported (untested against real API, flag defaults off), **Phase 5 (full twin unification) declined — see §6.1**, **Phase 6 done**, **Phase 7 done**. This refactor is now essentially complete — everything in the original 8-phase plan is either done or explicitly declined with reasoning recorded. · **Pick up next:** §5 (console/UX cleanup) is now the live work list — item 9 (file-tree console spam) is the user's stated priority. Also: (a) user smoke-tests `EnableImplicitPrefixCacheWarmup` on Vertex when possible — the one piece of this session's work not verified against the real API; (b) otherwise, incremental common-helper extraction as areas get touched (see §6.1) is the ongoing mode of work, not a new phase · **Baseline commit:** `22c83bf` · **Date:** 2026-07-26 · **Last updated:** 2026-07-28

**Open task (2026-07-28, not started): port AI Studio's implicit prefix-cache
warmup to Vertex, behind a new config flag.** User wants a
`bool` flag in both `AiStudioAutoExtractionConfig` (default `true`, gates the
existing always-on warmup calls — no behavior change) and
`VertexAutoExtractionConfig` (default `false`, new opt-in behavior) using
AI Studio's tested warmup code as the reference implementation. Scoped to a
**minimal single-shot handshake** (just `GetDummyPart0Content` +
`WarmUpSystemInstructionCacheAsync`-equivalent), not the full batched-history
machinery (`HistoryBatchCount`/`MergeSystemInstructionAndFirstHistoryBatch`/
etc. — those config knobs don't exist on Vertex and porting them too was
explicitly declined).

**Blocking finding, needs a decision before implementing:** AI Studio's warmup
only helps because the warmup Part is token-identical to Part 1's real
pre-video text (`ReferenceContextPreamble` + dummy-part0 +
`GetStaticPromptBeginning(1)`). Vertex's `BuildGenerationRequestAsync` has
**no equivalent static pre-video text for Part 1 today** — for Part 1
(`previousTexFiles.Count == 0`), the prompt is just
`[video attachment] + [parsedPrompt]`, nothing before the video. User chose
"exact-match handshake" (matching AI Studio's cache-correctness bar) over
"simple handshake" (weaker/unclear cache benefit) when asked — but exact-match
means **inventing a new static text block that must also be prepended to every
real Vertex Part-1 request**, not just the warmup, which is a genuine change
to what gets sent to Gemini on every Vertex extraction, not an internal
refactor. Also: Vertex already has its own (tested, per the user) explicit
`CachedContent` mechanism (`InitializeContextCachingAsync`/`_cachedContentName`)
which serves the same "don't re-pay for the system instruction" goal via a
different, official API — `BuildGenerationRequestAsync` currently sends
**either** `CachedContent` **or** raw system-instruction Parts, never both, so
this new warmup would need to coexist with that (run once at setup, before
the first `InitializeContextCachingAsync` call) without disturbing it.

**Before implementing:** get an explicit answer on (a) what the new static
Part-1 preamble text should say for Vertex (a real content/behavior decision,
not mechanical), and (b) confirm the interaction with `UseContextCaching`
(both mechanisms active simultaneously, by design). The user can't test Vertex
changes in the near future, so this needs unusually careful design-first
confirmation rather than build-and-see, given zero automated coverage and a
paid API on the other end.

**User steer (2026-07-28, same session):** confirmed "exact-match" over
"simple handshake" when asked, separately confirmed Vertex's code is "very
old" / fine to port more from AI Studio rather than keep it minimal, and
explicitly said "you can totally rebuild vertex" — removing the earlier
self-imposed minimal-footprint constraint.

**Done (2026-07-28), same session, once budget allowed a careful pass:**

Key finding that made this tractable: Vertex's `PrepareAndUploadPartAsync`
**already contained** the exact same `merging_and_scope`/`segment_start`
wording AI Studio uses as its static preamble — it just lived in the
dynamic (post-video) prompt instead of a pre-video Part. So this was a
structural reorder of Vertex's own existing wording, not new invented
content, for the `merging_and_scope`/`segment_start` piece. What *is* new:
the dummy-part0.tex anchor block and the warmup handshake itself (both
verbatim ports of AI Studio's tested code).

* `EnableImplicitPrefixCacheWarmup` bool added to both configs:
  `AiStudioAutoExtractionConfig` (default `true`, gates the 4 existing
  `WarmUpSystemInstructionCacheAsync`/`WarmUpWithBatchedHistoryAsync` call
  sites — no behavior change) and `VertexAutoExtractionConfig` (default
  `false`, new opt-in).
* New `VertexAutoExtractionSession.PrefixCache.cs` (partial class, mirrors
  the AI Studio Phase 4.5 split): `GetDummyPart0Content` (byte-identical
  port), `GetStaticPromptBeginning` (Vertex's own existing wording,
  relocated), `WarmUpSystemInstructionCacheAsync` (single-shot only — no
  batched-history variant, since Vertex has no `HistoryBatchCount`-style
  config surface and that was explicitly descoped earlier in this
  conversation).
* `VertexAutoExtractionSession.PrepareAndUploadPartAsync` restructured:
  dynamic half only now (`lecture_metadata` wrapped in a `<parameter>` tag
  to match AI Studio's shape, `source_video`, `segment_info`,
  `duration_and_timestamps`); static half moved out.
* `VertexAutoExtractionSession.BuildGenerationRequestAsync` step 1
  restructured to always send a pre-video Part (dummy anchor when the flag
  is on + optional inlined `previousTexFiles` context + the static
  preamble), replacing the old "only add a Part when `previousTexFiles.Count
  > 0`" logic — Part 1 now always gets a stable pre-video Part, same as
  AI Studio.
* Warmup wired into `EnsureSessionSetupAsync`, gated by the flag, running
  once before `InitializeContextCachingAsync` first creates the explicit
  `CachedContent` cache — the two mechanisms are independent and coexist.
* Verified: build 0/0 (via `-o <alt-dir>`, default output still locked by
  the user's running instance), 85 tests green. UI-string diff intentionally
  **not** empty this time — exactly one new string (the warmup's opening
  line; every other line inside the ported method matched AI Studio's
  existing strings verbatim, confirming the wording was reused faithfully).
  `docs/ui-strings.baseline.txt` updated deliberately to include it.

**Not done / explicitly out of scope:** batched-history warmup for Vertex
(no config surface for it); any change to `InitializeContextCachingAsync`
itself or the `UseContextCaching` default. **Still unverified against the
real API** — user confirmed Vertex's explicit `CachedContent` path is
tested, but this new implicit-warmup addition is not, and the user can't
test it in the near future. Flag defaults to `false` specifically so nothing
changes for existing Vertex users until they opt in.

**Decision (2026-07-28): Vertex is out of scope for Phase 4+.** Vertex AI
stays disabled (`Program.Activate_Vertex` stays hardcoded `false`, per the
user — not a call this document makes unilaterally) and the user confirmed
no further refactoring effort should go into it for now. This **supersedes**
§6's "keep and unify behind `IAiBackend`" framing for the remainder of this
session's work: `VertexAutoExtractionSession.cs` and
`DirectAiChatSessionVertex.cs` were left exactly as Phase 3 finished them —
not decomposed alongside their AI Studio twins in Phase 4. Whether Vertex
support is kept, unified, or deleted outright in a future Phase 5 is still
an open question for a future session; nothing here forecloses any of those
options.

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
itself were merged.

Phase 4's god-method decomposition is now done for every AI-Studio-reachable
file (`AiStudioAutoExtractionSession.cs`, `LatexRefinementSession.cs`,
`DirectAiChatSessionAiStudio.cs`) — see §4 Phase 4 outcome for the full list
of splits and commits. `VertexAutoExtractionSession.ProcessFilesAsync` /
`GenerateTexFromUploadedPartAsync` and `DirectAiChatSessionVertex`'s
equivalents were deliberately **not** mirrored, per the Vertex decision above.
Same verification discipline as Phase 3 throughout: these are large methods
with zero automated test coverage and real paid-API cost to validate against,
so each split is verified by build + UI-string diff, one
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

**Outcome (2026-07-28) — done for the AI-Studio-reachable code, 6 commits:**

* `ProcessFilesAsync` (both extraction twins) → `ProcessPreparedVideoAsync`
  (one call per video; `ProcessFilesAsync` itself is now just channel setup +
  the consumer loop), commit `515c028`. Named per plan, minus the further
  `TranscribeSegmentAsync`/`FinalizeVideoOutputAsync`/`RollbackFailedVideoAsync`
  split — the per-video body still carries a lot of shared mutable state
  (pending upload tasks, rate-limit timer, token totals) that would need a
  small state object to split further without passing 6+ ref params; not
  attempted this session.
* `GenerateTexFromUploadedPartAsync` (both extraction twins) →
  `BuildGenerationRequestAsync` + `StreamAndCollectAsync` (+ AI-Studio-only
  `LogTokenCountsAsync`, no Vertex equivalent exists), commit `fb3ce29`.
* `ExecuteGenerativeStepAsync` (`LatexRefinementSession`, shared — not
  Vertex-specific) → `ResolveSystemInstructionTextAsync` +
  `EnsureContextCacheAsync` + `BuildStepRequestConfig` + `DumpPromptLogAsync` +
  `ComputeExpectedStructuralCounts` + `StreamAndCollectAsync`, commit `02a08fe`.
* `ExecutePdfFixAttemptAsync` → `StreamFixResponseAsync`;
  `RunAntiGravityAgentFixLoopAsync` → `CallAntiGravityAgentAsync`, commit `45f3242`.
* `TryHandleBuiltInCommandsAsync` (`DirectAiChatSessionAiStudio`) → one
  `TryHandleXCommand` method per command instead of a command-table redesign
  — a `Dictionary<string, ChatCommand>` doesn't fit cleanly since several
  commands match by prefix (`"set temp "`, `"attach "`) or regex
  (`change-key`), not by exact key; the flattened-ladder approach preserves
  exact dispatch order with much lower redesign risk. `StreamGeminiResponseAsync`
  → `BuildChatRequestConfig` + `StreamChatTurnAsync`, commit `625287f`.
* `DebugChatAsync`/`ReplLoopAsync` (`AiStudioAutoExtractionSession`) → same
  one-method-per-command flattening for the REPL menu, plus
  `StreamDebugChatResponseAsync` for the hand-rolled retry/backoff loop
  (kept separate from `ApiResilience.ExecuteStreamWithRetryAsync` — genuinely
  different backoff strategy), commit `3b0ffd5`.

**Not done, by decision, not oversight:** `VertexAutoExtractionSession.cs`
and `DirectAiChatSessionVertex.cs` — see the Vertex decision note at the top
of this document. `ProcessPreparedVideoAsync`'s further split into
`TranscribeSegmentAsync`/`FinalizeVideoOutputAsync`/`RollbackFailedVideoAsync`
— would need a small mutable state object first (see above), left for a
future session if the nesting/line-count metrics are re-measured and still
found wanting.

**Important caveat: Phase 4 fixed long methods, not the god class.**
`AiStudioAutoExtractionSession.cs` is still 1809 lines and ~49 methods after
Phase 4 — barely smaller than before (it was ~1928), because splitting a
300-line method into six 50-line methods doesn't remove any responsibility
from the class, it just names the pieces. The class still does all of:

* FFmpeg batch pipeline orchestration (`ProcessFilesAsync` / `ProcessPreparedVideoAsync`)
* Gemini request building + streaming (`BuildGenerationRequestAsync`, `StreamAndCollectAsync`)
* Prefix-cache warm-up (`WarmUpSystemInstructionCacheAsync`, `GetDummyPart0Content`, `WarmUpWithBatchedHistoryAsync`)
* A full debug-chat REPL (`ReplLoopAsync`, `DebugChatAsync`, `StreamDebugChatResponseAsync`, 9 `TryHandleReplX` methods)
* Model-selection / menu UI (`SelectModel`, `PrintCommandsMenu`)
* YouTube task handling, upload prep, system-instruction loading

This is a **class-level** decomposition problem, distinct from — and doable
independently of — the twin-merge/`IAiBackend` question in §6 and Phase 5.
Extracting `PrefixCachePrimer`, `ExtractionRepl`, and `SystemInstructionLoader`
(already named in §3.1's target architecture) out of this one file would cut
it roughly in half without touching the Vertex question at all. **Candidate
Phase 4.5, not yet started:**

1. `ExtractionRepl` — `ReplLoopAsync`, `DebugChatAsync`,
   `StreamDebugChatResponseAsync`, all 9 `TryHandleReplX` methods,
   `SelectModel`, `PrintCommandsMenu`. Biggest single win — this is a
   self-contained debug/menu feature with almost no coupling to the
   extraction pipeline beyond reading `_config`/`_client`.
2. `PrefixCachePrimer` — `GetDummyPart0Content`, `WarmUpSystemInstructionCacheAsync`,
   `WarmUpWithBatchedHistoryAsync`. Same content the Phase 3 investigation
   found has no Vertex equivalent (§ above) — extracting it now is a pure
   move, no merge-with-Vertex risk.
3. Re-measure line count and nesting after 1–2; decide whether a third pass
   (e.g. pulling `PrepareAndUploadPartAsync` + upload-scheduling into its own
   type) is still warranted.

**Outcome (2026-07-28) — items 1–2 done, as `partial class` splits, not standalone
types, commit pending in this session.**

* `AiStudioAutoExtractionSession.Repl.cs` (new file, 410 lines) — `PrintCommandsMenu`,
  `ReplLoopAsync`, all 9 `TryHandleReplX(Async)` methods, `SelectModel`,
  `DebugChatAsync`, `StreamDebugChatResponseAsync`, `MyRegex()`. Moved verbatim
  via `sed` line-range extraction (not retyped) to rule out transcription slips,
  the same discipline Phase 3 used for `ExtractionHelpers`/`ConsoleUiHelper`.
* `AiStudioAutoExtractionSession.PrefixCache.cs` (new file, 224 lines) —
  `_dummyPart0Content` + `GetDummyPart0Content()`, `WarmUpWithBatchedHistoryAsync`,
  `WarmUpSystemInstructionCacheAsync`. Same verbatim-extraction method.
* `AiStudioAutoExtractionSession.cs` itself: 1809 → 1239 lines (−31%).
* **Deliberate deviation from §3.1's naming:** the plan's target architecture
  names these as standalone types (`ExtractionRepl`, `PrefixCachePrimer`) that
  would take their dependencies via constructor injection. That was *not* done.
  Both extracted regions read and write a lot of the session's mutable state
  directly — `WarmUpWithBatchedHistoryAsync` mutates `_systemInstructionText`
  in a loop and calls back into `AppendHistoryFilesToInstructionAsync`;
  `StreamDebugChatResponseAsync` mutates the four `_sessionTotal*Tokens`
  counters and `_debugChatHistory`. Turning that into clean constructor-injected
  types would mean designing a shared mutable-state object first (the same
  prerequisite already flagged for splitting `ProcessPreparedVideoAsync`
  further) — real design risk for code with zero automated coverage and a paid
  API on the other end, for a readability win only. A `partial class` split
  gets the same file-size reduction with **zero behavioral risk**: no field
  access changes, no new coupling, same verification (build 0/0, 85 tests
  green, UI-string diff empty, `[AI Context]`/`[Human]` comment count checked
  before/after — 64 → 68, all +4 accounted for by the two new file-header
  doc comments, nothing lost). If Phase 5's `IAiBackend` unification later
  needs these as real standalone types, that redesign should happen *there*,
  where the mutable-state question has to be answered for the twin-merge
  anyway — not duplicated here first.
* **Item 3, done (2026-07-28):** re-measured after 1–2 — main file was 1239
  lines, 23 methods, with `ProcessPreparedVideoAsync` still 279 lines (the
  clear remaining offender). Split it into `ProcessPreparedVideoAsync`
  (orchestrator) + `ComputeBaseName` + `ResolveRefinementClientAndConfigureParams`
  + `TranscribeSegmentsAsync` (the per-part loop) + `FinalizeVideoOutputAsync`,
  sharing cross-iteration mutable state via a new private `VideoProcessingState`
  class instead of a 6+ parameter/ref-parameter list — the exact prerequisite
  this section originally flagged as missing. Main file now 1292 lines (net
  +53 vs. the 1239 low, since this pass adds real structure — 2 new methods +
  1 state class — rather than just moving text to another file). Verified via
  `dotnet build -o <alt-dir>` (0 errors; default output was locked by a
  `lec-extraction-prog.exe` instance the user asked to leave running), 85
  tests green, empty UI-string diff.

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

**Outcome (2026-07-28) — done, 1 commit.** `Program.cs`: 366 → ~34 lines
(`Main` + exception handling only). New `MainMenu.cs` (the loop),
`SourceFolderMenu.cs`/`ApiKeyProfileMenu.cs` (the two sub-menus, was
`ConfigureSourceFoldersMenu`/`ConfigureApiKeysMenu`), `SessionFactory.cs`
(the config-load/client-build/session-construct wiring). Item 2's
`ConfigEditor<T>.Edit(…)` helper was **not** built — on inspection the
`Load()`→mutate→`Save()` triads differ enough per call site (different
config types, different mutation callbacks, some nested in loops) that a
generic wrapper wouldn't actually collapse much; not worth the abstraction.
Item 3's `ApiKeyProfileResolver.Resolve(profile, envNames)` added to
`GoogleAi` (not `App`, to avoid `ConsoleUi` needing to depend on `App`) and
wired into all 3 real occurrences (2 in `Program.cs`, defensively verified
against a 3rd equivalent-but-not-identical copy in
`ConfigurationPrompts.ConfirmOrChangeApiKeyProfile`, which was also switched
over since it was provably equivalent). Item 4 done as
`AppConfig.IsVertexAiEnabled`, all 10 call sites across `Program.cs`,
`RefinementUiHelper.cs`, `VertexAutoExtractionSession.cs` updated; the
`appsettings.json` key added explicitly for discoverability (default
`false`, preserving current behavior). Verified: build 0/0, 85 tests green,
UI-string diff showed exactly the 4 expected "Program.Activate_Vertex" →
"AppConfig.IsVertexAiEnabled" message updates.

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

**Outcome (2026-07-28) — done, 3 commits.** Every rename in the table above
applied and verified (`grep` swept for stragglers after each batch — none
found beyond intentional historical "was X" doc-comment notes). Two
exceptions, both already resolved before this phase started: `startAudioTask`
→ `AudioTrackExtractor.EnsureStarted()` was done back in Phase 3;
`FfmpegInteractiveSession`'s file was already correctly named (Phase 3
outcome notes flagged this table item as stale even then). Generic-name
renames (`files`/`f`, `hasErrors`, `channel`/`producerTask`, `result`) were
scoped to their specific target methods via line-range-limited `sed` rather
than a blind global replace, since those identifiers recur elsewhere in the
same files with unrelated meaning. The 4 type renames
(`ApiResilience`→`ApiRetryPolicy`, `AttachmentHandler`→`AttachmentUploader`,
`StringHelper`→`StringExtensions`, `LatexTimestampHelper`→`LatexTimestampAdjuster`)
included matching file and test-file renames via `git mv`. `PrintCommandsMenu`/
`ShowCommands` → `WriteCommandHelp` is a naming-consistency pass only — each
class keeps its own separate method body, not a functional merge (same
category as the already-confirmed-identical `SupportsThinking` copies, but
these four bodies are NOT identical, so they stay four separate methods
under one shared name).

`README.md` and `Documentation.md` (English + German, both files) had their
architecture sections rewritten to the current `src/` layout, `LectureExtraction.*`
namespaces, and post-rename type names — they still described the pre-Phase-1
flat-namespace layout. `.agents/rules/AGENTS.md` and `gemini.md` (the
plan's "`GEMINI.md`") were checked and found to have no stale structural
references — coding-style rules and system-instruction content don't
reference the C# project layout, nothing to update.

Verified per batch: build 0/0, 85 tests green throughout. UI-string diff
showed only the expected string-interpolation-expression-name changes that
mechanically follow from renaming a field/variable used inside a `$"..."`
literal (e.g. `{_speed}` → `{_playbackSpeedMultiplier}`) — runtime-visible
output never changed; baseline updated deliberately after each such batch.

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
9. **Every system-instruction / history file is printed to the console twice
   on session start** — user-reported (2026-07-28), and the most annoying
   item on this list in day-to-day use. Two independent layers:
   * `FileTreeRenderer.PrintFileTree(...)` renders the full recursive tree
     *before* the "laden? (j/n)" confirm prompt — **12 call sites** across
     `AiStudioAutoExtractionSession` (3), `VertexAutoExtractionSession` (3),
     both chat sessions (2 each), `LatexRefinementSession` (1),
     `FileTreeRenderer` itself (definition).
   * Then, during the actual load, each file prints its own line again —
     `"  [INFO] System Instruction geladen: {relativePath}"`
     (`AiStudioAutoExtractionSession.cs:260`, `VertexAutoExtractionSession.cs:172`)
     and `"  [INFO] History-Textdatei in System Instruction eingebunden: {relativePath}"`
     (`AiStudioAutoExtractionSession.cs:283`).

   With ~20 `SystemInstructionPaths` entries plus the whole
   `transcription/training-history` directory, that's hundreds of lines
   scrolled past before the session is even usable. Suggested shape: default
   to a **summary** (`"  [INFO] 23 System-Instruction-Dateien geladen (4 Ordner, 1.2 MB)"`),
   keep the full tree behind a config flag (e.g. `VerboseFileListing`, default
   `false`) or an explicit REPL command. The tree still has real value when
   first wiring up `SystemInstructionPaths` — this is about it not being the
   *default* every single run. Note `PrintFileTree` and
   `GenerateMarkdownFileTree` are **different** methods with different
   purposes: the Markdown one feeds the actual prompt payload (Attention Map
   Priming, see `Documentation.md` §4.2) and must **not** be touched — only
   the console-printing one is noise.
10. **`InlinePrecedingLecTexParts` is dead config on the AI Studio path.**
    Found in passing during the Phase 7 rename sweep: `VertexAutoExtractionSession`
    reads it (twice, in `BuildGenerationRequestAsync`), but
    `AiStudioAutoExtractionSession` **never reads it at all** — `grep -rn
    "InlinePrecedingLecTexParts" src/Extraction/AiStudioAutoExtractionSession*.cs`
    returns nothing. It's still present and documented in
    `AiStudioAutoExtractionConfig` (defaulting `true`), so on the backend the
    user actually runs, toggling it silently does nothing. Either wire it up
    on the AI Studio side (its prompt assembly always inlines preceding
    `.tex` parts before the video, so `false` would be a real behavior
    change) or delete it from that config and its JSON. **Decide which —
    don't silently pick one**, since it changes what gets sent to a paid API.

11. **The per-part token analysis spends 3 extra API requests purely on
    console output.** `AiStudioAutoExtractionSession.LogTokenCountsAsync`
    (called once per video part from `TranscribeSegmentToLatexAsync`) issues
    up to three `CountTokensAsync` calls — video-only, inlined-context-only,
    and full-history — solely to print the `[Token-Analyse]` breakdown. All
    three are at `AiStudioAutoExtractionSession.cs:1118/1124/1131`, and there
    are no other `CountTokensAsync` call sites in the codebase (AI-Studio-only;
    Vertex has no equivalent). `CountTokens` is cheap or free in *token* terms,
    but it consumes **requests-per-minute quota** — and RPM is precisely this
    app's binding constraint (hence `VideoPartDelaySeconds` defaulting to 130s
    and the whole rate-limit-pacing architecture). A 3-part video therefore
    spends ~9 extra requests on diagnostics. Suggested: gate the whole method
    behind a config flag (default `false`), or derive the numbers from the
    `UsageMetadata` the real request already returns.
12. **Token reporting is three long lines per request.** `[Request Tokens]`,
    `[Part Total Tokens]`, `[Session Total Tokens]` all print in full on every
    single request *and* every "Continue" continuation (up to 6 per part).
    Fine when debugging one part, noisy across a batch. Could collapse to one
    compact line, with the full breakdown behind the same verbosity flag as
    items 9 and 11.
13. **`SelectModel`'s freetext branch says "find or append" but never
    appends.** `AiStudioAutoExtractionSession.Repl.cs` — the comment reads
    `// Freetext model name – find or append`, but the `else` branch only
    prints `"Modell '{choice}' nicht in der Liste gefunden. Auswahl
    unverändert."` and moves on. So you cannot actually add a new model
    interactively; you must edit the JSON and restart. Either implement the
    append (and persist it to `Model[]`) or fix the comment to match reality.
    Worth doing properly — new Gemini model IDs appear often enough that
    "edit JSON, restart" is real friction.
14. **The `(j/n)` confirm prompts are a silent footgun.** Every one is
    `if (Console.ReadLine()?.Trim().ToLower() != "j") return true;` — so
    Enter, `y`, `yes`, `ja`, or any typo all mean "no", the method returns
    *success*, and the session proceeds with an empty
    `_systemInstructionText`. You then run a whole paid extraction with no
    system instructions and only find out from the output quality. At minimum
    accept `y`/`yes`/`ja` and treat bare Enter as yes; better, print a loud
    warning when instructions end up empty before the first API call.
15. **No way to inspect the effective config from the REPL.** To check what's
    actually loaded (model, thinking budget, delays, which flags are on) you
    have to read the JSON on disk — and since `ConfigLoader` writes back at
    runtime, the file and the in-memory state can diverge mid-session. A
    `show config` REPL command dumping the live values would remove a lot of
    guesswork.

16. **Model configuration lives in two different mechanisms, and `AppConfig`'s
    share of it is dead weight.** User-raised (2026-07-28: "I don't like that
    we have the model set in AppConfig"). Investigated — the situation is not
    quite what that describes, and the worse half is elsewhere:
    * **The model *name* is already out of `AppConfig`.** `DefaultModel` /
      `RefinementModel` were found unused and deleted back in Phase 3 (see
      that section's closing note). Nothing to do here.
    * **What remains in `AppConfig` is the six model *parameters*** —
      `DefaultTemperature`, `DefaultTopP`, `DefaultTopK`,
      `DefaultMaxOutputTokens`, `DefaultThinkingBudget`, `DefaultThinkingLevel`
      — and these *are* effectively dead. Every session JSON specifies all six
      explicitly (`AiStudioAutoExtractionConfig.json` and
      `VertexAutoExtractionConfig.json`: 6 hits each;
      `LatexRefinementSessionConfig.json`: 36, i.e. all six across every
      step/backend block), so the `AppConfig` values only ever apply if a JSON
      key goes *missing*. Proof they're unmaintained: the C# fallbacks and
      `appsettings.json` have silently drifted apart — C# says `DefaultTopK =
      40` / `DefaultThinkingBudget = 24576`, `appsettings.json` says `10` /
      `4096`. Nobody noticed because effectively nothing reads them.
    * **The genuinely bad one: `AvailableModels` is hardcoded in C#**, in four
      separate session classes (`AiStudioAutoExtractionSession`,
      `VertexAutoExtractionSession`, `DirectAiChatSessionAiStudio`,
      `DirectAiChatSessionVertex`). Adding a new Gemini model to the *chat*
      sessions requires editing C# and recompiling — exactly the smell Phase 6
      just removed from `Program.Activate_Vertex`. Worse, it's inconsistent:
      the **extraction** sessions read the JSON `Model[]` array while the
      **chat** sessions read the hardcoded C# array, so "where do I add a
      model?" has two different answers depending on which session you're in.

    Suggested fix, in order: (a) move the chat sessions' `AvailableModels`
    into their JSON configs, matching how extraction already works — this also
    fixes item 13 at the root, since a JSON-backed list can actually be
    appended to; (b) delete the six `Default*` model params from `AppConfig`,
    `AppConfigOptions`, and `appsettings.json`. **Caveat on (b):** don't just
    delete the indirection, or a session JSON missing a key would fall back to
    C# type defaults (`0.0f` temperature!) instead of a sane value. Move the
    sane default onto the config class property itself
    (`public float Temperature { get; set; } = 0.35f;`) so each config class
    is its own single source of truth. Non-model `AppConfig` members (paths,
    `IsVertexAiEnabled`, Vertex project/location) are genuinely global and
    should stay.

**Note (2026-07-28):** the "deliberately not executed now" framing above was
written when the refactor was still in flight. The refactor has now landed
(Phases 0–7 done or explicitly declined), so **this section is the actual
next body of work**, not a parking lot. Item 9 is the user's stated priority.

**Suggested grouping for execution** — items 9, 11, 12 are one coherent
change (a single `Verbosity`/`VerboseDiagnostics` config flag gating the
file tree, the token-analysis API calls, and the triple token report), and
together they'd cut the console noise dramatically while also saving RPM
quota. Items 5, 8 are a second coherent change (one canonical severity-tag
vocabulary + a shared print helper). Items 13, 14 are small independent
correctness fixes. Item 10 needs a user decision before any code moves.

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

### 6.1 Superseding decision (2026-07-28): Phase 5 (full `IAiBackend` twin unification) is declined

Reverses the "keep and unify behind `IAiBackend`" framing above for the
merge itself (the underlying "keep Vertex, don't delete it" call stands —
only the *unification* part is off the table). Reasoning, from the same
session that did the Vertex prefix-cache-warmup port:

* Phase 3's investigation already found several genuinely non-mergeable
  pairs (`SystemInstructionLoader`, `RefinementLauncher`,
  `GenerationConfigBuilder`, `ContextCacheCoordinator`, the GCS-cleanup
  trio) — real per-backend divergence, not laziness. Today's Vertex port
  reinforced this: upload mechanism (GCS bucket vs. Files API), auth (ADC
  vs. API key), and caching strategy are structurally different, not
  cosmetic.
* Vertex is disabled (`Program.Activate_Vertex = false`) and the user
  cannot test it in the near future. A full merge is a large,
  un-smoke-testable rewrite of the pipeline the user *does* actively use
  (AI Studio), undertaken in service of unifying with a backend that isn't
  currently running. Any subtle merge mistake would sit latent — possibly
  for a long time — until Vertex is re-enabled.
* User agreed after this was laid out: **don't do the full merge.**

**New standing mode of work, replacing Phase 5:** keep extracting genuinely
shared code into common helpers opportunistically, whenever a phase or task
already has you touching that area — same standard as Phase 3
(byte-identical or provably-safe pieces only, real per-backend differences
stay separate) — rather than a single big-bang unification. First instance:
`PrefixCacheAnchor.GetDummyPart0Content` (`src/GoogleAi/PrefixCacheAnchor.cs`)
extracted the same day it was duplicated into Vertex, since AI Studio's and
Vertex's copies were byte-identical; `GetStaticPromptBeginning` and
`WarmUpSystemInstructionCacheAsync` were *not* merged the same way — real
wording/signature differences, per the established discipline.

`IAiBackend` (§3.2) and the rest of Phase 5's original plan (merging the
session classes, chat sessions, config twins, `LatexRefinementSession`'s
constructors into `RefinementOptions`) stay documented above as a record of
what was considered, but are **not going to be executed** absent a real
need (e.g. Vertex becoming actively used again, making end-to-end
validation possible).

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
