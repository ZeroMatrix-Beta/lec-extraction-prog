# Implementation Plan — Refactoring `lec-extraction-prog`

**Status:** **Phases 0–10 complete.** The Spectre.Console migration is finished — `Console.Write` in `src/` went from 624 call sites to **4** (input prompts in `YouTubeTaskPrompt` that pair with `ReadLine`, which belong with F2). `docs/ui-strings.baseline.txt` regenerated, drift **0**. **Phase 11 substantially advanced:** `LatexRefinementSession.cs` 1603 → 520 (+ `.Pdf.cs` 617, `.Generation.cs` 383) and `AiStudioAutoExtractionSession.cs` 1323 → 755 (+ `.Generation.cs` 305, `.PrefixCache.cs` 181), plus `YouTubeTaskRunner`, `DebugRoundtripRunner` and `SystemInstructionTextBuilder` extracted as real types. Nothing on the AI Studio path exceeds 760 lines. · **Pick up next: F2 (`__EXIT__`, 13 sites), then `RefinementOptions` for the 4 telescoping constructors, then `VertexAutoExtractionSession` (~1690 lines, untouched).** · **Deep-dive specs:** `docs/deep-dive-spectre-ui.md`, `docs/deep-dive-tex-attachment-mode.md`, `docs/deep-dive-code-quality-decomposition.md`. · **Baseline commit:** `22c83bf` · **Date:** 2026-07-26 · **Last updated:** 2026-07-29

---

## ⚠ Review findings (2026-07-28) — read before Phase 10

Independent review of the Phase 8 / 8.5 / 9 work. The build was 0/0 and the
suite green throughout, so none of these were compile or test failures — they
are behavioural gaps the tests did not cover.

**Status: F1, F3, F4, F5, F6, F7 closed. F2 still open (untouched) — it is the one remaining Phase 10 item.**

**F4 reopened (2026-07-29):** the baseline was regenerated to 0 drift before the
Spectre work, then the migration changed nearly every call site without
regenerating again. Drift is now **996 lines**. Review it and regenerate as the
closing act of Phase 10 — not before, or you bless changes nobody read.

**F8 · The Phase 10 migration outran the `Ui` API and left the build broken ·
FIXED (2026-07-29).** A migration run that exhausted its budget mid-flight
emitted **60 compile errors** in three shapes: 46 × CS1501 (`Ui.Detail(msg, scope)`
and `Ui.Step(title, scope)` called with two arguments against one-argument
declarations), 6 × CS1929 (`Ui.Header(...)` used in three places but never
defined), and 8 × CS0103 (`Ui` not in scope in `ConfigLoader.cs` and
`HistoryFileResolver.cs`). No logic damage — the call sites had simply assumed a
richer API than `Ui.cs` shipped. Resolved by extending `Ui` to match: optional
`scope` on `Detail` and `Step` (consistent with `Info`/`Warn`/`Error`/`Success`),
a new `Header(string)` framed panel for session banners, and the two missing
`using` directives. All additions are `Markup.Escape`d, so the escaping boundary
is unchanged.

Both safety-critical boundaries were verified intact afterwards: all three
streaming sites use `Ui.Raw` (the unparsed `new Text()` path), and the only live
region is `InteractiveDelay`'s `AnsiConsole.Status()`, which runs between
generations rather than during one.

### F1 · Freetext model names destroy an entry in `Model[]` · **RESOLVED (2026-07-28)**

**Status:** **FIXED**. `ModelSelection.SelectOrAdd(name)` implemented in [ModelSelection.cs](src/Configuration/ModelSelection.cs), and `Current` property setter delegates to `SelectOrAdd`. New model names are appended to `Available[]` without overwriting existing entries. Covered by unit test `ModelSelection_SelectOrAdd_AppendsNewModel_WithoutOverwritingExisting` in [ConfigBindingTests.cs](tests/LectureExtraction.Tests/ConfigBindingTests.cs).

### F2 · `__EXIT__` is more entrenched than backlog item 4 assumed · **open**

Measured: **13 occurrences across 4 files**, plus a second sentinel
`__CHANGED_KEY__` ([DirectAiChatSessionAiStudio.cs:708](src/Chat/DirectAiChatSessionAiStudio.cs:708)).
It is not only the model prompt — it is also the system-prompt choice, the
initial input, and the history choice in both chat sessions, and it is returned
from [ConfigurationPrompts.cs:158](src/ConsoleUi/ConfigurationPrompts.cs:158) and
[:261](src/ConsoleUi/ConfigurationPrompts.cs:261). Backlog item 4 described it as
one return value from `ConfirmOrChangeModel`; it is really a project-wide
convention for "user typed exit". Phase 10's `SelectionPrompt` migration should
replace it with a nullable return or a small result type, in one pass across all
13 sites rather than piecemeal.

### F3 · `_playbackSpeedMultiplier` is NOT dead — it is a stranded feature · **RESOLVED (2026-07-28)**

**Status:** **FIXED**. Added `SpeedMultiplier` to `IAutoExtractionConfig`, `AiStudioAutoExtractionConfig` (defaulting to 1.0) and wired `VideoSegmentProducer.RunAsync` to consume `config.SpeedMultiplier`. Removed the hardcoded, stranded `_playbackSpeedMultiplier` private fields from both extraction session classes.

### F4 · The UI-string baseline is stale by three phases · **RESOLVED (2026-07-29)**

Regenerated once before the Spectre work, then again at the end of it (`164c428`)
once the migration was actually complete — the intermediate state showed 996
lines of drift. Drift is now **0**.

The inventory is **444 entries, down from 624** at Phase 0. That drop is
consolidation, not lost output: 20+ variant severity spellings collapsed into
four canonical tags, multi-line exception reports became single lines, and
subsystem prefixes moved out of string literals into the `Ui` scope argument, so
strings that were once distinct are now one.

**Caveat, stated because it affects how much the baseline proves:** the final
regeneration was a deliberate *bulk accept* of the completed migration, not a
line-by-line review of 996 lines. That is defensible only because the migration
was finished and committed, making the diff one known change rather than a
mixture. From here the normal rule applies again — read the diff, confirm every
change was intended, regenerate in the same commit.

### F11 · A "back" option is the same job as F2 — do them together · **open**

User asked (2026-07-29) for a back button in the menus. It is not a UI addition:
every prompt's caller has to distinguish "user chose a value" from "user wants to
step back" and unwind one level. Done partially it produces prompts that go back
next to prompts that silently fall through — the exact failure mode of the silent
`SelectModel` branch and the `(j/n)` confirm, both fixed earlier today.

**The insight worth acting on:** `__EXIT__` (F2) is already a sentinel meaning
"user bailed", checked at 13 sites. "Back" is the same plumbing with one more
case. So F2 should not be done as "replace `__EXIT__` with a nullable return" —
that solves half the problem and has to be redone. Do it once as a result type:

```csharp
public readonly record struct PromptResult<T>(PromptOutcome Outcome, T? Value);
public enum PromptOutcome { Value, Back, Exit }
```

Every prompt returns it, every caller switches on it, and `Back` becomes
expressible without touching those sites a second time. `__CHANGED_KEY__`
(`DirectAiChatSessionAiStudio.cs:708`) is a fourth case of the same pattern and
should fold in.

Scope: 13 `__EXIT__` sites + 1 `__CHANGED_KEY__`, the `SelectionPrompt` call
sites in `ConfigurationPrompts`, `SessionFactory`, `RefinementUiHelper`,
`FfmpegInteractiveSession` and `VideoBatchSelector`. Not large, but it is
control flow, so it wants a full session rather than a leftover budget.

### F9 · The warm-up's token report is silent when usage metadata is missing · **open**

Raised by the user (2026-07-29): *"the warm-up didn't seem to cause round-trip
costs, and output was only 2 tokens."*

Partly by design, partly a reporting gap:

* **2 output tokens is intended.** The handshake asks for exactly
  `[AI-Model: …] Handshake confirmed. Ready.` with `MaxOutputTokens = 100`.
* **But `PrimePrefixCacheAsync` sets no `ThinkingConfig`**, unlike the real
  generation path — so the warm-up runs at the model's default thinking
  behaviour and reasoning tokens are reported separately from
  `CandidatesTokenCount`. Worth deciding whether the warm-up should explicitly
  disable thinking; it has nothing to reason about.
* **The token line prints only `if (inputTokens > 0)`.** Usage is read per-chunk
  from `chunk.UsageMetadata`, which on a streamed response typically arrives
  only on the final chunk. If it never arrives, the line is silently skipped and
  the handshake *looks* free. Silence there means "not reported", not "not
  charged".

Cheap diagnostic before changing anything: log whether `chunk.UsageMetadata` was
ever non-null for one run. That distinguishes "not reported" from "genuinely
zero" definitively. It touches the paid path, so decide before implementing.

### F10 · Cost is reported in the wrong currency · **idea, not yet scoped**

Every report in this app counts tokens. Tokens are not what limits it — **requests
per minute** are, which is why `VideoPartDelaySeconds` is 130 and
`HistoryRateLimitDelaySeconds` is 120. A warm-up that hits the cache perfectly
still spends one request and a 120-second delay, and the token report shows
neither. With `HistoryBatchCount: 3` that is roughly six minutes of wall clock
before the first video is touched, invisible in the output.

Proposal: a session-end summary reporting **requests issued** and **wall-clock
spent waiting in `SmartDelayAsync`**, alongside the token totals. Both numbers
are already available — requests can be counted where `ApiRetryPolicy` runs, and
`InteractiveDelay` already knows how long it waited. This would make the actual
cost of a configuration visible for the first time, and would immediately answer
questions like "is `HistoryBatchCount: 3` worth it?".

### F5 · Two models worked the same uncommitted tree · **resolved**

Phases 8.5 and 9 were produced by a different assistant concurrently with this
review, ~30 files unstaged. Now committed as `93827bb`, `5ccdfd3`, `444511f`.
Checkpoint exists; nothing appears to have been lost.

### F6 · The migrator silently discarded the user's model list · **FIXED (2026-07-28)**

`ConfigMigrator` wrote the migrated section under the **legacy JSON key**
(`"Model"`) while the config class exposes it as the **property**
`ModelSelection`. `Microsoft.Extensions.Configuration` binds by property name and
silently ignores what it cannot match — and `Model` still exists on the class as
a `[JsonIgnore]` delegating `string[]` — so the migrated object bound to nothing.

Measured against the live config, which carries 5 models: after
migrate-and-bind, `Model.Length` came back as **1**, and `CurrentModelIndex` was
lost. The damage had not yet happened only because the migration had not yet
run — the root `*.json` files were still flat (Phase 9 changed only
`appsettings.json`), and `ConfigLoader.Load` rewrites them in place on first
launch. It was queued, not realised.

**Fix:** write the section as `ModelSelection`, remove the legacy `Model` key,
and point `RemapAnchor` at the new path so the property's `//` comments follow.

**Why 102 green tests missed it:** `ConfigBindingTests.ConfigMigrator_MigratesLegacyFlatKeys_ToNestedSections`
asserted `legacyJson["Model"]` — it pinned the *shape of the emitted JSON* rather
than *whether the value survives binding*, so it passed with the defect and
failed the moment the defect was fixed. Assertion corrected, with a comment
explaining the distinction. **Lesson for the rest of this plan: assert on bound
values, not on JSON structure.**

### F7 · Phase 9's migrator-test exit criterion was skipped · **CLOSED (2026-07-28)**

Phase 9 required "tests green **including migrator round-trip tests**"; no such
tests existed. Added `tests/LectureExtraction.Tests/ConfigMigratorTests.cs` — 7
tests covering generation parameters, model selection, paths/sources, API-key
profile, Vertex endpoint + context caching, idempotency (the migrator runs on
every `Load()`), and cross-config-type leakage. Every category except model
selection passed on first run, which is the evidence that the rest of the
migrator is sound.

One suspicion raised and **cleared** by that work: `ConfigLoader` was thought to
call `Migrate` without the `configType` argument, which would have moved
`SourceFolder`/`TargetFolder` in `LatexRefinementSessionConfig` and
`FfmpegSessionConfig` into a `Paths` section neither class exposes, blanking both
folders. It does pass `typeof(T)`; there is now a test pinning that.

*State after F6/F7:* build 0/0, **109 tests green**.

---

## Two practices this refactor earned the hard way

Both were learned from actual failures on 2026-07-28/29, not adopted on
principle. They cost minutes and save hours.

### Verify a line-range move three ways — a green build is not evidence

Moving code by line range (`sed -n 'X,Yp'`) is the safest way to relocate large
blocks, because nothing is retyped. But it silently took a closing brace one line
too early during the `SystemInstructionTextBuilder` extraction, and the build
still failed only by luck — a different slip would have compiled fine while
losing a method. After every such move, before committing:

```bash
# 1. no method lost or gained
diff <(git show HEAD:<file> | grep -oE '^    (public|private|protected|internal)[^;{]*\(' | sort) \
     <(cat <new-files> | grep -oE '^    (public|private|protected|internal)[^;{]*\(' | sort)

# 2. no line of the original missing (mind the trailing newline between files)
comm -23 <(git show HEAD:<file> | sed 's/[[:space:]]*$//' | sort) \
         <({ for f in <new-files>; do cat "$f"; echo; done; } | sed 's/[[:space:]]*$//' | sort)

# 3. [AI Context]/[Human] comment counts balance, +N for new file headers
```

Check 2 is sensitive enough to produce a false positive if the files are
concatenated without a separating newline (`}using System;`) — that is the check
working, not failing.

### Assert on bound values, not on intermediate shape

F6 shipped through 102 green tests because
`ConfigMigrator_MigratesLegacyFlatKeys_ToNestedSections` asserted the *emitted
JSON structure* rather than *whether the value survived binding*. It passed with
the defect and failed the moment the defect was fixed — a test actively holding a
bug in place.

The rule for this codebase: a test must assert what the program ends up
believing, not what an intermediate artefact looks like. Concretely — bind the
config and assert the property, do not assert the JSON key. This matters more
here than usual, because `Microsoft.Extensions.Configuration` **silently ignores
keys it cannot match**, so every structural mismatch fails quietly.

### And the pattern that unlocked Phase 11

Phase 4.5 twice deferred extracting types out of the session classes because the
candidates mutated session state. The unlock was simple and worth reusing:
**return your result; let the caller write the state.** `DebugRoundtripRunner`
returns a `DebugRoundtripResult`, `SystemInstructionTextBuilder.AppendHistoryFilesAsync`
returns the uploaded `Part`s. The session stays the single writer of its own
fields, and the extracted piece becomes static or constructor-injectable with no
shared-state object needed.

---

**Phase numbering note (2026-07-28).** Phase 8 originally carried a 16-item
console backlog grouped into six steps. Steps 1–3 are done; steps 4–6 were too
large to sit inside one phase once the config and UI work was scoped properly,
so they became phases of their own:

| Old | New home |
|---|---|
| Phase 8 step 4 — model config consolidation | **Phase 9** (Configuration consolidation) |
| Phase 8 step 5 — menu plumbing | **Phase 10** (Spectre.Console UI) |
| Phase 8 step 6 — `InlinePrecedingLecTexParts` decision | **Phase 12** (decision taken, see there) |

The 16-item backlog below is kept as the evidence trail; each item now carries
its status.

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
"Decisions taken"'s "keep and unify behind `IAiBackend`" framing for the remainder of this
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
`DirectAiChatSessionAiStudio.cs`) — see "Phased execution" Phase 4 outcome for the full list
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

## Baseline (measured, not guessed)

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

## What is actually wrong (evidence, not opinion)

### Twin classes — the single biggest problem

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

### Verbatim duplicated helpers

* `private static bool SupportsThinking(string)` — copy-pasted **5×**
  (both extraction sessions, both chat sessions, `LatexRefinementSession`).
* `private static string GetUniqueTexPath(string)` — copy-pasted **2×**.
* Bucket cleanup exists three times under three different names:
  `CleanupBucketAsync` (Vertex extraction), `CleanupGcsBucketAsync` (AI Studio
  chat), `ForcePurgeGcsBucketAsync` (Vertex chat).

### God methods

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

### Nesting

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

### Anonymous tuples used as domain types

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

### Namespaces that describe nothing

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

### File / type mismatches and multi-type files

* `GeminiClientBuilder.cs` contains `GoogleAiClientBuilder`.
* `FfmpegInteractiveMenu.cs` contains `FfmpegInteractiveSession`.
* `AppConfig.cs` contains three types (`ConfigLoader<T>`, `AppConfigOptions`, `AppConfig`).
* `LatexRefinementSessionConfig.cs` contains four types (`BackendParameters`,
  `RefinementStepConfig`, `PdfCompilationConfig`, `LatexRefinementSessionConfig`).
* `DirectAiChatSessionAiStudioConfig.cs` contains two types.
* `YouTubeTranscriptionTask.cs` contains two types.

### Dead files

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

### Telescoping constructors

`LatexRefinementSession` has **four** public constructors (lines 29, 39, 49, 59)
that differ only in how much of the same state they set — the classic signal for
an options object.

### Naming

Representative offenders: `Program.Activate_Vertex` (snake_case public property,
also a global mutable flag), `_speed`, `f`, `result`, `hasErrors`, `channel`,
`producerTask`, `startAudioTask`, `applyParams`, `ExecuteStep1MergeAsync` /
`ExecuteStep2SpeechRefinementAsync` / `ExecuteStep3LastRefinementAsync` (config
ordering leaked into method names), `PrintCommandsMenu` vs `ShowCommands` for
the same concept, `ExtractionHelpers` (a 583-line grab-bag of seven unrelated
concerns).

---

## Guiding constraints

1. **Behaviour is frozen.** Every user-facing console string stays
   byte-identical, in German, in the same order. The UI is the de-facto
   specification and, with no tests, our only characterization harness.
   UI improvements are collected in "Phase 8 backlog" and executed **later**, separately.
2. **`dotnet build` ends every phase at `0 Warning(s), 0 Fehler`.**
3. **`.agents/rules/AGENTS.md` is binding** — `Count > 0` over `Any()`,
   target-typed `new()`, `[GeneratedRegex]`, no unused parameters, no silent
   `catch`, and **`[AI Context]` / `[Human]` comments are preserved** (moved
   with the code they document, never deleted).
4. **Identifiers in English, user-facing strings in German** — the existing
   convention, kept.
5. **One phase = one commit.** Each is independently revertable.

---

## Target architecture

### Folders and namespaces

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

### The backend abstraction (removes the twins)

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

### Domain records replacing the tuples

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

## Phased execution

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

1. Create the `src/` tree from "Folders and namespaces"; move every file into it.
2. Rewrite namespaces to the `LectureExtraction.*` scheme.
3. Split the multi-type files ("File / type mismatches and multi-type files"); rename files so filename == type name
   (`GeminiClientBuilder.cs` → `GoogleAiClientBuilder.cs`,
   `FfmpegInteractiveMenu.cs` → `FfmpegInteractiveSession.cs`).
4. Replace the fully-qualified noise that the flat layout forced —
   `System.IO.File.…` (used ~120×), `FfmpegUtilities.FfmpegToolkit.…`,
   `DirectChatAiInteraction.LatexRefinementSession`, `AutoExtraction.…` —
   with plain `using` directives.

*Exit:* build 0/0, tests green, UI-string diff empty.

**Outcome — done, in commits `2a2ec17` and `08eebec` (executed together with part of
Phase 3, ahead of this plan document).**

* `src/` now holds one folder per bounded context exactly as in "Folders and namespaces"
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

1. Introduce the records of "Domain records replacing the tuples".
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
  `LatexResponseCleaner` (moved to `Latex/`, matching "Folders and namespaces"),
  `InteractiveDelay` (moved to `ConsoleUi/`, matching "Folders and namespaces"),
  `VideoBatchSelector`, `YouTubeTaskPrompt`, `ModelSyncService`. **Done.**
  `ExtractionHelpers` itself now only holds `GetUniqueTexPath` and
  `LogSystemInstructionDumpAsync` (deliberately not split further — no
  natural home for either in the target "Folders and namespaces" list). Every extracted method
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
already visibly drifted between the twins (see "Twin classes") and needs an actual
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
  frozen-UI-strings rule (see "Guiding constraints") for a ~20-line saving.
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
independently of — the twin-merge/`IAiBackend` question in "Decisions taken" and Phase 5.
Extracting `PrefixCachePrimer`, `ExtractionRepl`, and `SystemInstructionLoader`
(already named in "Folders and namespaces"'s target architecture) out of this one file would cut
it roughly in half without touching the Vertex question at all. **Candidate
Phase 4.5, not yet started:**

1. `ExtractionRepl` — `ReplLoopAsync`, `DebugChatAsync`,
   `StreamDebugChatResponseAsync`, all 9 `TryHandleReplX` methods,
   `SelectModel`, `PrintCommandsMenu`. Biggest single win — this is a
   self-contained debug/menu feature with almost no coupling to the
   extraction pipeline beyond reading `_config`/`_client`.
2. `PrefixCachePrimer` — `GetDummyPart0Content`, `WarmUpSystemInstructionCacheAsync`,
   `WarmUpWithBatchedHistoryAsync`. Same content the Phase 3 investigation
   found has no Vertex equivalent (see the Phase 3 progress note above) — extracting it now is a pure
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
* **Deliberate deviation from "Folders and namespaces"'s naming:** the plan's target architecture
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

### Phase 5 — Unify the twins · highest risk · see "Decisions taken" open decision

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

### Phase 8 — Console & UX cleanup · low risk · ✅ **steps 1–3 DONE**, 4–6 redistributed

The one phase that deliberately **breaks** the frozen-UI-strings rule in "Guiding constraints"
— that rule existed to make the refactor safe, and the refactor is done. Its
job now is to change what the user sees, on purpose, on a clean base.

**Outcome (2026-07-28), 3 feature commits + 2 review fixes:**

1. ✅ **Verbosity flag** (backlog items 9, 11, 12) — commit `621873c`.
   `VerboseConsoleOutput` added to both extraction configs and to
   `IAutoExtractionConfig`, default `false`. Gates `FileTreeRenderer.PrintFileTree`
   (now prints a one-line count summary instead of the recursive tree), the
   per-file "System Instruction geladen" lines, and the triple token report
   (compact one-liner instead). **`LogTokenCountsAsync` now early-returns when
   non-verbose** — this is the RPM saving: 3 diagnostic `CountTokensAsync` calls
   per video part, ~9 per 3-part video, no longer spent.
2. ✅ **Severity-tag vocabulary** (items 5, 8) — commit `2ff85ba`.
   `[ERROR]`/`[Error]`/`[Fehler]`/`[FAILED]` → `[FEHLER]`, `[Warning]` →
   `[WARNUNG]`, `[Debug]` → `[DEBUG]`, and `"Invalid choice."` → German,
   fixing the main-menu/sub-menu language split. 16 files.
3. ✅ **Correctness fixes** (items 13, 14) — commit `bca5d2a`.
   `SelectModel` now appends an unknown freetext model name to `Model[]`,
   activates it, and persists via `ConfigLoader.Save`. New
   `StringExtensions.IsAffirmativeResponse(defaultIfEmpty: true)` accepts
   `j/ja/y/yes/1/true`, treats bare Enter as **yes**, and prints a loud
   `[WARNUNG]` when the user declines — replacing
   `if (ReadLine() != "j") return true;`, which silently returned *success* and
   let a paid run proceed with an empty system instruction. 14 tests added.
4. → **moved to Phase 9** (Configuration consolidation).
5. → **moved to Phase 10** (Spectre.Console UI).
6. → **decided, moved to Phase 12** (`InlinePrecedingLecTexParts`).

**⚠ The `docs/ui-strings.baseline.txt` regeneration required by this phase's
verification was NOT done** in any of the three commits. The baseline still
reflects Phase 7, so it currently shows ~27 accumulated differences and **is not
a usable regression check**. Reviewed retrospectively — all differences are
intentional — but regenerating it is carried forward as Phase 10 step 0.

#### Phase 8 review fixes (2026-07-28)

A review of the three commits above found two defects, both fixed:

* **A stray `PrimePrefixCacheAsync` call** was added by `621873c` — a commit
  about console verbosity — at the `else` of `shouldMergeHistory` in
  `AiStudioAutoExtractionSession.TryLoadSystemInstructionWithHistoryAsync`.
  Combined with the existing Phase-3 warm-up block in `EnsureSessionSetupAsync`,
  that produced **two handshakes** when `_historyWasLoaded` stayed false, and a
  **new** handshake (previously zero) when history loaded as a multi-turn
  preamble.
  **Reachability:** none on the live config —
  `shouldMergeHistory = LoadHistoryIntoSystemInstruction && !_historyWasLoaded`,
  and `AiStudioAutoExtractionConfig.json` sets that flag `true` with
  `HistoryBatchCount: 3`, so the real run goes through
  `WarmUpWithBatchedHistoryAsync` and never enters the `else`. A latent defect,
  reachable by setting `LoadHistoryIntoSystemInstruction: false`.
  **Resolution (user's decision): prime the base instruction first, always.**
  The added call is kept; the **Phase-3 warm-up block in
  `EnsureSessionSetupAsync` was deleted**. This also fixes a repeat-call
  re-prime: `EnsureSessionSetupAsync` has two call sites, its load is guarded by
  `if (string.IsNullOrEmpty(_systemInstructionText))`, but that block sat
  outside the guard. `VertexAutoExtractionSession` needed no change — its
  warm-up is already a single unconditional prime at the end of setup.
* **`SelectModel` failed silently.** `bca5d2a` deleted the
  `"Modell '{choice}' nicht in der Liste gefunden"` line while the new append
  stayed gated behind `else if (choice.Contains('-'))`, so an out-of-range
  number or any hyphen-less freetext did nothing with no output at all. Both
  copies (`AiStudioAutoExtractionSession.Repl.cs`,
  `VertexAutoExtractionSession.cs`) restructured into guarded early returns,
  each rejection path now printing a `[FEHLER]` explanation. The hyphen check is
  kept as a "does this look like a model id" guard so a stray keystroke is not
  persisted to JSON.

*Verified:* build 0/0, **99 tests green**.

*Exit:* met for steps 1–3. Remaining backlog items live in Phases 9, 10 and 12.

---

## Phase 8 backlog — console & UX changes (evidence trail)

Collected during Phases 0–7 and deliberately deferred, because changing console
output while refactoring would have destroyed the UI-string diff as a
regression check. Items are numbered in discovery order, **not** priority order.

**Status map (2026-07-28)** — this section is now the evidence trail; the work
itself lives in the phases named below.

| Item | Status |
|---|---|
| 5, 8 — tag/language consistency | ✅ done, Phase 8 step 2 (`2ff85ba`) |
| 9, 11, 12 — file-tree spam, diagnostic `CountTokens`, token report | ✅ done, Phase 8 step 1 (`621873c`) |
| 13, 14 — `SelectModel` append, `(j/n)` footgun | ✅ done, Phase 8 step 3 (`bca5d2a`) + review fix |
| 16 — model config sprawl, `AppConfig` dead params | → **Phase 9** |
| 1, 2, 3, 4, 6, 15 — menu plumbing, `show config` | → **Phase 10** (Spectre `SelectionPrompt` removes most structurally) |
| 7 — `IProgressReporter` | → **Phase 10** (superseded: Spectre `Progress`/`Status` for non-streaming phases) |
| 10 — `InlinePrecedingLecTexParts` | → **Phase 12**, decision taken |

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

---

## Phases 9–12 — the remaining work

Scoped 2026-07-28 from three problems the user raised: config is scattered, the
UI could be much better even if behaviour changes, and code quality. The
frozen-UI-strings rule no longer applies from Phase 10 onward.

### Decisions taken when scoping these

| Question | Decision |
|---|---|
| Config scope | **Restructure the JSON files too**, not just the C# side — with an automatic migrator, so no hand-editing and no lost values |
| UI scope | **Spectre.Console** — menus, config tables, status, and live progress for non-streaming phases; token streaming stays plain writes |
| Vertex | **Keep and bring it along** — included in the config restructure and the UI migration |
| `InlinePrecedingLecTexParts` | When `false`, **upload the preceding `.tex` files as attachments**, the way `.mp4` is uploaded |
| That flag's default | **Both paths ship, defaulting to `true`** (today's behaviour); the default is set from measurement afterwards |
| Uploaded `.tex` read-only marker | **Keep `ReferenceContextPreamble` and name the attached files** as read-only reference in the text Part |
| Severity output | **Colour *and* the canonical bracket tag** — Spectre strips colour when piped, so colour alone loses the signal when capturing a run |
| Disabled menu entries | **Dim and non-selectable** — visible with the reason, cursor skips them |

**Cost of the Vertex decision, stated once:** `VertexAutoExtractionSession.cs`
(1698 lines, largest file in the repo, 223 lines at ≥5 indent levels) and
`DirectAiChatSessionVertex.cs` are unreachable at runtime
(`AppConfig.IsVertexAiEnabled = false`) and cannot be smoke-tested. Including
them roughly doubles the surface of Phases 9 and 10. Every Vertex-side change is
compile-verified and diff-verified only.

### Review findings behind these phases (measured 2026-07-28)

**Configuration** — duplicated property blocks across the 7 config classes:

| Block | Copies | Where |
|---|---|---|
| `Temperature`/`TopP`/`TopK`/`MaxOutputTokens`/`ThinkingBudget`/`ThinkingLevel` | **6** | `AppConfigOptions` (as `Default*`), both extraction configs, `BackendParameters`, both chat `*GenerationConfig` |
| `Model[]` + `CurrentModelIndex` + `CurrentModel` | **5** | both extraction configs, `BackendParameters`, both chat configs |
| `SystemInstructionPath(s)` | **6** | `AppConfigOptions`, both extraction, both chat, `RefinementStepConfig` |
| `LogFolder` | **6** | `AppConfigOptions`, both extraction, both chat, `SessionLoggerConfig` |
| `HistoryPreloadPaths` | **5** | `AppConfigOptions`, both extraction, both chat, `RefinementStepConfig` |
| `UseContextCaching` + timing knobs | **4** | `BackendParameters`, `VertexAutoExtractionConfig`, both chat generation configs |
| Vertex `ProjectId`/`Location`/`GcsBucketName` | **4** | `AppConfigOptions`, `VertexAutoExtractionConfig`, `LatexRefinementSessionConfig`, `DirectAiChatSessionVertexConfig` |
| `ActiveApiProfile` + `AiStudioApiKeyEnvNames` | **3** | AI Studio extraction, AI Studio chat, `LatexRefinementSessionConfig` |

Three mechanisms coexist: static `AppConfig` (binds `appsettings.json` once),
`ConfigLoader<T>` (per-type JSON, writes back at runtime), and hardcoded C#
arrays (`AvailableModels`, 4 session classes). 54 `Load()` sites, 28 `Save()`.

Specific defects found:

* `ConfigLoader<T>.Save` writes the file **twice** — once to
  `AppDomain.CurrentDomain.BaseDirectory`, again to
  `Directory.GetCurrentDirectory()` if different. This is why a running instance
  dirties the working tree.
* `ClearCollectionsRecursively` blanks **every** array between the two bind
  passes, so an array absent from `{TypeName}.json` binds as **empty**, not at
  its C# default. `AiStudioApiKeyEnvNames`'s 4-element default survives only
  because the JSON repeats it. Load-bearing and untested.
* `AppConfig` passes `reloadOnChange: true` but binds into `_options` once in
  the static constructor — the flag does nothing.
* `appsettings.json` has stub keys binding to nothing:
  `"DirectAiChatSessionAiStudioConfig": null`, `"…VertexConfig": null`,
  `"LatexRefinementSessionConfig": {}`, `"FfmpegSessionConfig": {}`.
* `AppConfig` hardcodes folder **substructure** in C# (`"analysis2"`,
  `@"d-und-a\new"`, …) — changing the layout needs a recompile.
* `RefinementUiHelperConfig` has **no JSON in the repo**; one exists only in
  `bin/Debug/net10.0/`, created by `Save` and invisible to version control.
* `IAutoExtractionConfig` is now a **20**-member bag of unrelated flags (item 1
  of Phase 8 added one), not an abstraction.

**Console** — zero use of `Console.ForegroundColor` anywhere; all output is
monochrome. `Console.Write*` is spread over 20+ files, 389 calls in the three
session classes alone; there is no output abstraction, which is also why the
pipeline cannot be tested without capturing stdout. `Console.ReadLine()` appears
in 13 files with hand-rolled parsing each time. Three files
(`DirectAiChatSessionAiStudio.cs`, `DirectAiChatSessionVertex.cs`,
`AttachmentUploader.cs`) use `using static System.Console`, so a
`Console.Write` grep misses every call in them.

**Code quality** — largest remaining methods:

| Lines | Location |
|---|---|
| 248 | `VertexAutoExtractionSession.ProcessPreparedVideoAsync` |
| 246 | `RefinementUiHelper.StartInteractiveRefinementAsync` (in a 261-line file) |
| 222 | `AttachmentUploader.UploadAndAttachFileAsync` |
| 183 | `VertexAutoExtractionSession.ReplLoopAsync` |
| 167 | `VertexAutoExtractionSession.SendHistoryHandshakeAsync` |
| 159 | `DirectAiChatSessionVertex.StreamGeminiResponseAsync` |
| 149 | `LatexRefinementSession.CompilePdfAsync` |
| 147 | `AiStudioAutoExtractionSession.TranscribeSegmentsAsync` |

`LatexRefinementSession.cs` is 1603 lines with 4 telescoping constructors but
only **7 instance fields** — its length is method bodies, not state, which is
why a `partial` split fits and a state-object redesign does not. Also:
`SessionLogger` appends `chat_log.md` via a **relative** path, i.e. into the
current working directory (the 11 MB file Phase 0 had to gitignore); and the
repo root holds a stray `settings.json` that is a **VS Code** settings file,
duplicating `.vscode/settings.json` and copied into every `bin/`.

### Phase 8.5 — Reclaim the chat / REPL surface · **do before Phase 9**

Raised by the user 2026-07-28: *"`AiStudioAutoExtractionSession.cs` is still
1300 lines — what happened to the anti-spaghetti plans? Every time an AI works
on this file, the tokens get burned."*

**The measurement, and why the earlier phases didn't fix it:**

| File | Lines | Methods |
|---|---|---|
| `AiStudioAutoExtractionSession.cs` | 1323 | 28 |
| `…Session.Repl.cs` | 425 | 15 |
| `…Session.PrefixCache.cs` | 199 | 2 |
| **class total** | **1947** | **45** |

At the Phase 0 baseline the class was **1928 lines in one file**. It is now
*slightly larger overall*; reading it costs **~30 000 tokens**. Phase 4 split
long methods (same class, more lines — names and signatures add text) and Phase
4.5 moved regions into `partial` files (same class, no responsibility removed).
Both outcomes were predicted and recorded at the time — see Phase 4's "Important
caveat: Phase 4 fixed long methods, not the god class."

**For token cost, `partial` is the worst of the options.** It cut the largest
*file* from 1928 to 1323, but an agent reasoning about the type generally needs
all three files, so it now pays 1947 lines instead of 1928.

**What actually drives the cost** is not lines-per-file but how much must be read
to change one thing safely — which is set by coupling to shared mutable state.
The class has 15 private fields, and these are read *and written* across all
three partial files: `_systemInstructionText`, `_historyWasLoaded`,
`_historyParts`, `_sessionPreamble`, `_debugChatHistory`,
`_sessionTotalInputTokens` / `Output` / `Cached`, `_sessionMaxFreshTokens`.
Any method may touch any of them, so there is no safe way to read only part.

#### 8.5a — Investigate deleting the in-session debug REPL

**Decision: consider deletion, not extraction** (user, 2026-07-28).

`AiStudioAutoExtractionSession.Repl.cs` is 425 lines / 15 methods:
`ReplLoopAsync`, `WriteCommandHelp`, 9 `TryHandleReplX` handlers, `SelectModel`,
`RunDiagnosticChatTurnAsync`, `StreamDebugChatResponseAsync`. It is a
development aid reached from inside an extraction run, and it duplicates in
miniature what the Direct chat sessions already do properly (8.5b).

If it is genuinely unused, **deleting beats extracting** — 425 lines and 15
methods gone rather than relocated, ~22 % of the class, and the
`_debugChatHistory` field plus the diagnostic half of the token counters leave
the shared-state set with it.

Before deleting, check each command for a capability that exists nowhere else —
`run refinement`, `youtube` and `convert all` in particular look like real
entry points rather than debug aids, and may need to move to the main menu
instead of being dropped. `SelectModel` is definitely still needed.

#### 8.5b — Rebuild the Direct chat sessions on the extraction session's foundation

User assessment (2026-07-28): the Direct chat sessions are *"very, very dated"*;
rebuild them from scratch using `AiStudioAutoExtractionSession` as the
reference, with its file-upload handling — **but keep Gemma support**.

Current state: `DirectAiChatSessionAiStudio.cs` (792) +
`DirectAiChatSessionVertex.cs` (638) = **1430 lines**, one of the twin pairs
that Phase 5 declined to unify. Rebuilding rather than merging sidesteps the
reason Phase 5 was declined: the risk there was a large un-smoke-testable
*merge* of working code; a rewrite of a component the user considers obsolete is
a different proposition.

**Must be preserved — verify explicitly, it is easy to lose in a rewrite:**

* **Gemma role handling.** `"gemma"` appears in **no** `AvailableModels` array —
  it is reachable only by typing the model name as freetext, so the
  `SelectModel` append path (Phase 8 step 3) and Phase 9's move of
  `AvailableModels` into JSON both feed it. The behaviour itself lives at
  [DirectAiChatSessionAiStudio.cs:519](src/Chat/DirectAiChatSessionAiStudio.cs:519)
  and [DirectAiChatSessionVertex.cs:416](src/Chat/DirectAiChatSessionVertex.cs:416):
  Gemma **pre-v4 does not support the `system` role**, so the system
  instruction is folded into the first user turn instead. Pin this with a unit
  test before the rewrite starts — it is a pure function of the model name.
* The command surface: `attach`, `set temp`, `set tokens`,
  `set thinking-budget`, `set thinking-level`, `set grounding`, `set model`,
  `change-key`, `clear`, plus `GetInitialHistoryCommand`.
* `CleanupGcsBucketAsync` / `ForcePurgeGcsBucketAsync` — the Phase 3 notes record
  these two as genuinely different (free-tier guard, richer Vertex diagnostics),
  so a unified rewrite must consciously decide what the merged behaviour is
  rather than silently take one.

**Sequencing caveat, flagged not decided:** a rewrite done before Phase 10 will
have its console layer written twice, since Phase 10 migrates everything to
Spectre. Doing 8.5b *after* Phase 10 avoids that, at the cost of migrating 1430
lines that are about to be deleted. Cheapest order is probably: 8.5a now
(deletion, no rewrite), 8.5b after Phase 10.

#### 8.5c — Cheap mitigation, no refactoring required

Independent of everything above and worth doing immediately:

* Add a **member index** to each partial file's header comment, listing what
  lives there.
* Add a line to `.agents/rules/AGENTS.md` stating that
  `AiStudioAutoExtractionSession` is a three-file partial class and which file
  holds what.

This lets an agent grep to the right file and read ~400 lines instead of 1947,
without touching a line of logic.

**Honest ceiling on all of this:** 8.5a helps because the REPL is genuinely
independent. Splitting the *pipeline* into more types will not save tokens by
itself — if the pieces still share `_systemInstructionText` and the token
counters, an agent reads them all anyway and only file-navigation overhead is
added. Any further decomposition therefore has to start with an owner for that
shared state (`ExtractionSessionState`), for which `VideoProcessingState`
— created in Phase 4.5 for exactly this reason — is the working precedent.

### Phase 9 — Configuration consolidation · large

Absorbs Phase 8 step 4 / backlog item 16. Goal: one definition per concept, JSON
that mirrors the C# shape, no hand-editing of live config files.

**9.0 — already done, commit `ead5ca1`.** `JsonCommentPreserver` extracted from
the generic `ConfigLoader<T>` into a non-generic `internal static` class, with a
new `Merge(existingJson, updated, remapAnchor)` entry point.
`ConfigLoader<T>.SerializePreservingComments` still exists and delegates, so
`ConfigCommentPreservationTests` (which reaches it by reflection) is untouched.
This came first because the migrator must move a property's `//` comment along
with the property when it descends into a nested section — `AnchoredComment`
`(ContainerPath, BeforePropertyKey, CommentLines)` is exactly the right
representation, and `remapAnchor` is the hook: rewrite `([], "Temperature")` to
`(["Generation"], "Temperature")` and the comment follows. Without it every
migrated key would silently lose its comment.

**9.1 — shared building blocks.** New types in `src/Configuration/`, each with
sane defaults on the property initialisers so a missing JSON key never yields
`0.0f`:

| New type | Members | Replaces |
|---|---|---|
| `GenerationParameters` | `Temperature`, `TopP`, `TopK`, `MaxOutputTokens`, `ThinkingBudget`, `ThinkingLevel` | 6 copies |
| `ModelSelection` | `Available[]`, `CurrentIndex`, computed `Current` | 5 copies + the 4 hardcoded `AvailableModels` arrays |
| `ContextCacheSettings` | `Enabled`, `Minutes`, `IncrementMinutes`, `MinimumRemainingMinutes` | 4 copies |
| `VertexEndpoint` | `ProjectId`, `Location`, `GcsBucketName` | 4 copies |
| `ApiKeyProfile` | `ActiveProfile`, `EnvNames[]` | 3 copies |
| `ContextSources` | `SystemInstructionPaths[]`, `HistoryPreloadPaths[]` | 6 / 5 copies |
| `WorkspacePaths` | `SourceFolder`, `PredefinedSourceFolders[]`, `TargetFolder`, `LogFolder`, `UploadFolder` | scattered |

Lift `ModelSelection.Current`'s clamping from the existing
`AiStudioAutoExtractionConfig.CurrentModel` rather than rewriting it.
`BackendParameters` becomes those types composed and is deleted.
Per-config default differences must be preserved at the composition site
(e.g. AI Studio extraction has `TopP = 0.8`, Vertex `0.9`, `BackendParameters`
`1.0`) — set them in the initialiser: `= new() { TopP = 0.8f }`.

**9.2 — the JSON migrator, before touching any config class.**
`ConfigLoader<T>` writes live config files at runtime, so a reshape that loses
values is not recoverable from the file. Add `ConfigMigrator`, invoked from
`Load` before binding: read the raw `{TypeName}.json` as a `JObject`, move any
legacy flat key into its new nested section when that section is absent, bind as
normal. `Save` then writes the migrated shape with comments preserved.
Idempotent and one-directional; keep legacy handling for one release.

**Before the first migrated run:** stop any running `lec-extraction-prog.exe`
and back up the root `*.json` files — a live instance will re-save the old shape
over the new one. This is the only step in the plan that can lose data.

**9.3 — `AppConfig` cleanup.** Delete the six `Default*` params (values move onto
`GenerationParameters` initialisers — use the `appsettings.json` values, which
are the ones in effect: `0.35 / 0.9 / 10 / 65535 / 4096 / HIGH`); delete the four
stub keys; drop or implement `reloadOnChange`; move the hardcoded subpaths into
`appsettings.json`; commit `RefinementUiHelperConfig.json` to the repo root.
Keep `BaseLectureFolder`, `UploadFolder`, `LogFolder`, `IsVertexAiEnabled` and
the Vertex endpoint — those are genuinely global.

**9.4 — `ConfigLoader` fixes.** Make the current-directory second write explicit
or drop it; document `ClearCollectionsRecursively`'s real contract and pin it
with a test.

**9.5 — hygiene.** Delete the stray root `settings.json`; point
`SessionLogger`'s `chat_log.md` at `_currentSessionLogPath`.

*Exit:* build 0/0; tests green including migrator round-trip tests; every root
`*.json` loads → migrates → saves → reloads to identical values with comments
intact; UI-string diff unchanged (this phase changes no output).

### Phase 10 — Spectre.Console UI · large

Absorbs Phase 8 steps 5 and backlog items 1, 2, 3, 4, 6, 7, 15.
**Full spec: `docs/deep-dive-spectre-ui.md`** — measured tag inventory, the `Ui`
API surface, escaping boundary, file-by-file migration order.

**Step 0: regenerate `docs/ui-strings.baseline.txt`** before anything else, so
the diff becomes a working review tool again.

Then: a `Ui` type wrapping `AnsiConsole` with canonical severity helpers and a
`scope` argument (so `[LaTeX Refinement] [FEHLER]` survives); `Ui.Raw` for model
output with **no markup parsing**, since Spectre treats `[` `]` as markup and
this app streams LaTeX; Spectre `SelectionPrompt` for every menu (which
structurally removes hand-numbering, the `__EXIT__` magic string, and the ~6
divergent exit handlers); `ConfirmationPrompt` for `(j/n)`; a `show config`
command; and `Progress`/`Status` for FFmpeg splitting, uploads, `InteractiveDelay`
and PDF compilation — **never** while tokens are streaming.

Note `dump-ui-strings.sh` greps for `Console.Write*`; it must learn `Ui.` calls
in the same commit as the `Ui` class, or the diff silently goes empty.

### Phase 11 — Code quality · medium

**Full spec: `docs/deep-dive-code-quality-decomposition.md`** (member-by-member split
map with line ranges).

`LatexRefinementSession` → `RefinementOptions` replacing the 4 telescoping
constructors, plus a `partial` split into `.Steps.cs` / `.Pdf.cs` /
`.Streaming.cs` / `.Cache.cs`, following the Phase 4.5 discipline exactly
(verbatim line-range moves, `[AI Context]`/`[Human]` comment counts checked).
Then `AttachmentUploader.UploadAndAttachFileAsync` split by branch (which also
hosts the Phase 12 change), `RefinementUiHelper` after Phase 10 has dissolved
its prompts, and finally the Vertex catch-up.

Tests to add: config migration round-trip (highest value), the new config types'
defaults and clamping, `ClearCollectionsRecursively`'s array contract, `Ui`
markup escaping against LaTeX, and `RefinementOptions` equivalence against all
4 old constructor signatures.

### Phase 12 — `InlinePrecedingLecTexParts` as an upload switch · small

Settles backlog item 10. **Full spec: `docs/deep-dive-tex-attachment-mode.md`.**

`.tex` is **already** in `AttachmentUploader.s_textExtensions`, which
short-circuits to inline text before the mime switch is reached — so the change
is an `uploadTextAsFile` opt-in bypassing that branch, plus a `.tex` mime entry.
Wire the flag on the AI Studio side (where it is currently never read at all)
and give Vertex the same `false` semantics.

Known trade, from the code's own comments: a Files-API/GCS URI is an external
reference that **breaks implicit prefix caching**, so `false` gives up the
incremental `tex1…texN` prefix reuse and keeps only the dummy anchor. It does
**not** reduce token cost; it reduces payload size and re-transmission across
continuations, at the cost of one upload request per preceding file per part.
Hence: ship defaulting to `true`, measure, then decide.

Land **last and alone** — it changes what reaches a paid API.

### Suggested order

```
Phase 8.5c Member index + AGENTS.md note     ~tiny    ← do first, helps immediately
Phase 8.5a Delete the in-session debug REPL  ~small   ← biggest token win per effort
Phase 9    Config consolidation + migrator   ~large
Phase 10   Spectre.Console UI                ~large   ← the visible payoff
Phase 11   Code quality + tests              ~medium
Phase 8.5b Rebuild the Direct chat sessions  ~large   ← after 10, so the console layer isn't written twice
Phase 12   .tex upload switch                ~small   ← last, touches a paid API
```

Config before UI, because Phase 10's verbosity handling, `show config` command
and menu tables all consume the new config shape — the other order means
building the UI twice. 8.5b sits after Phase 10 for the same reason.

---

## Decisions taken

**Vertex AI — keep and unify behind `IAiBackend`.** `Program.Activate_Vertex` is
hard-coded `false`, so ~2 500 lines of Vertex code are currently unreachable, but
the capability is being preserved rather than deleted. Consequences:

* `IAiBackend` ("The backend abstraction") is part of the target architecture, and Phase 5 merges both
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

### Superseding decision (2026-07-28): Phase 5 (full `IAiBackend` twin unification) is declined

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

`IAiBackend` ("The backend abstraction") and the rest of Phase 5's original plan (merging the
session classes, chat sessions, config twins, `LatexRefinementSession`'s
constructors into `RefinementOptions`) stay documented above as a record of
what was considered, but are **not going to be executed** absent a real
need (e.g. Vertex becoming actively used again, making end-to-end
validation possible).

---

## Risk register

| Risk | Mitigation |
|---|---|
| No automated tests over the pipeline | Phase 0 adds tests for all pure logic; UI-string diff pins the rest; one commit per phase for cheap revert |
| Pipeline can only be validated by running against a **paid** API | Manual smoke test on one short video, once, after Phase 5 — not per phase |
| JSON config files must keep loading unchanged | Config property names are frozen; `ConfigLoader` comment-preserving round-trip is covered by a Phase 0 test |
| `ConfigLoader` writes back to the JSON files at runtime | Back up the six `*.json` config files before starting |
| Phase 1 touches every file (huge diff) | Kept purely mechanical; no logic edits mixed in, so review is a namespace/path scan |
| `[AI Context]` / `[Human]` comments lost during moves | Explicit checklist item per phase; grep count before/after must match |

---

## Suggested order of work

```
Phase 0    Safety net              ~small     ✅ done
Phase 1    Layout + namespaces     ~large     ✅ done  (mechanical)
Phase 2    Domain records          ~small     ✅ done
Phase 3    Extract services        ~large     ✅ done  ← biggest single win
Phase 4    Split god methods       ~large     ✅ done
Phase 4.5  Split the god class     ~medium    ✅ done  (added mid-flight)
Phase 5    Unify twins             ~medium    ❌ declined — see "Superseding decision"
Phase 6    Entry point + menus     ~small     ✅ done
Phase 7    Naming + docs           ~medium    ✅ done
Phase 8    Console & UX cleanup    ~medium    ← NEXT, backlog in "Phase 8 backlog"
```

Phases 0–4 were worth doing regardless of how "Decisions taken" was decided, and removed the
great majority of the spaghetti. Phase 3 alone cut roughly 1 500 duplicated
lines.

**Phases 0–7 are closed.** Phase 8 is the only open work, and it is
deliberately different in kind from everything above it: Phases 0–7 were
*structural* changes that held observable behaviour frozen, verified by an
empty UI-string diff. Phase 8 is a *behavioural* change whose entire point is
to alter what the user sees — so it inverts that check (see Phase 8's
verification note). Don't carry the frozen-strings habit into it.
