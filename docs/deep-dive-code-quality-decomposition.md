# Deep dive — code-quality decomposition (Phase 11)

Companion to [implementation-plan.md](../implementation-plan.md) — Phase 11 (code quality).

## `LatexRefinementSession` — 1603 lines, 7 fields, 4 constructors

Note the main plan said "34 fields"; that number came from a grep that counted
every `private` member. The real instance-field count is **7** (lines 23–29).
The class is long because of method bodies, not state — which makes it a good
candidate for a partial split and a poor one for a state-object redesign.

### Full member map, with line ranges

| Lines | Member | Goes to |
|---|---|---|
| 23–29 | the 7 fields | stays in main file |
| 31–69 | **4 constructors** | replaced by `RefinementOptions` |
| 75–107 | `StartAsync` | stays (entry point) |
| 113–211 | `ExecutePipelineAsync` (94) | stays (orchestration) |
| 212–365 | `CompilePdfAsync` (149) | `.Pdf.cs` |
| 366–400 | `CleanupPrecheckFiles` | `.Pdf.cs` |
| 401–421 | `CleanupHelperFiles` | `.Pdf.cs` |
| 422–477 | `FormatLatexLog` | `.Pdf.cs` |
| 478–590 | `MergeSegmentsAndAlignTimestampsAsync(string[]…)` (111) | `.Steps.cs` |
| 591–598 | `MergeSegmentsAndAlignTimestampsAsync(string…)` overload | `.Steps.cs` |
| 599–669 | `RefineAgainstSpeechAsync` | `.Steps.cs` |
| 670–698 | `ApplyFinalPolishAsync` | `.Steps.cs` |
| 699–704 | `RunRefinementStepAsync` (parts overload) | `.Steps.cs` |
| 705–795 | `RunRefinementStepAsync` (history overload) | `.Steps.cs` |
| 796–827 | `ResolveSystemInstructionTextAsync` | `.Steps.cs` |
| 828–900 | `EnsureContextCacheAsync` | `.Cache.cs` |
| 901–935 | `BuildStepRequestConfig` | `.Steps.cs` |
| 936–968 | `DumpPromptLogAsync` | `.Steps.cs` |
| 969–998 | `ComputeExpectedStructuralCounts` | `.Streaming.cs` |
| 999–1148 | `StreamAndCollectAsync` (143) | `.Streaming.cs` |
| 1149–1193 | `CreateContextCacheAsync` | `.Cache.cs` |
| 1194 | `CleanupBucketAsync` (one-liner delegate) | `.Cache.cs` |
| 1200–1311 | `TryRepairFailedPdfBuildAsync` (104) | `.Pdf.cs` |
| 1312–1413 | `StreamFixResponseAsync` (97) | `.Pdf.cs` |
| 1414–1489 | `RunExternalAgentRepairLoopAsync` | `.Pdf.cs` |
| 1490–1573 | `CallAntiGravityAgentAsync` | `.Pdf.cs` |
| 1574–1602 | `GetCleanBaseName` + 3 `[GeneratedRegex]` | stays in main file |

Resulting sizes, approximately:

```
LatexRefinementSession.cs            ~230   fields, ctor, StartAsync, ExecutePipelineAsync, helpers
LatexRefinementSession.Steps.cs      ~430   the three steps + shared step runner
LatexRefinementSession.Pdf.cs        ~570   compile, cleanup, log formatting, repair loops
LatexRefinementSession.Streaming.cs  ~180   stream/collect + structural counts
LatexRefinementSession.Cache.cs      ~120   context cache create/ensure/purge
```

### Notes on the split

* `.Pdf.cs` is still ~570 lines. That is acceptable — it is one coherent concern
  (get a PDF out, retry, repair) and the alternative is an arbitrary cut. Revisit
  only if it grows.
* The regexes must move **with** the class part that uses them or stay in the
  main file; `[GeneratedRegex]` requires the containing type to be `partial`,
  which it already is (line 22).
* Follow the Phase 4.5 method exactly: verbatim line-range extraction (`sed`),
  not retyping, then check the `[AI Context]`/`[Human]` comment count before and
  after. That discipline caught a real transcription slip during the
  `ExtractionHelpers` split.

### `RefinementOptions`

The 4 constructors differ only in which of 5 optional values they set — a
textbook telescoping case. All four bodies are pure assignment, no logic:

```csharp
public sealed record RefinementOptions {
    public required Client Client { get; init; }
    public required LatexRefinementSessionConfig Config { get; init; }
    public string? SingleFilePath { get; init; }
    public string[]? MultipleFiles { get; init; }
    public IAutoExtractionConfig? ExtractionConfig { get; init; }
    public string? AudioFilePath { get; init; }
    public List<Part>? PreUploadedAudioAttachments { get; init; }
}

public LatexRefinementSession(RefinementOptions options) { … }
```

There is a latent invariant worth encoding while touching this: `SingleFilePath`
and `MultipleFiles` are mutually exclusive (each constructor nulls the other).
Add a validating constructor or a factory pair
(`ForSingleFile(…)` / `ForBatch(…)`) rather than leaving both settable — that is
the one behavioural improvement in an otherwise mechanical change, and it is
compile-time safe.

**Test:** construct via each of the 4 old signatures' equivalents and assert the
resulting field state matches, so the migration is provably faithful. Fields are
private — compare via a `#if DEBUG` accessor or reflection, matching how
`ConfigCommentPreservationTests` already reaches privates.

**Call sites:** `src/App/SessionFactory.cs`, `src/Extraction/RefinementUiHelper.cs`,
and the refinement launch inside both extraction sessions. Small number, all
compile-checked.

## `AttachmentUploader.UploadAndAttachFileAsync` — 222 lines

Structure found (line numbers relative to file):

| Branch | Roughly | Extract to |
|---|---|---|
| text extensions (`s_textExtensions`, line 25) — system-instruction vs plain inline | 155–167 | `AttachInlineTextAsync` |
| mime resolution switch | 169–184 | `ResolveMimeType(ext)` — pure, testable |
| image inline blob (system instruction or `_inlineImages`) | 186–204 | `AttachInlineImageAsync` |
| AI Studio Files API upload + retry + activation wait | 206–~300 | `UploadViaFilesApiAsync` |
| Vertex GCS upload | ~305–345 | `UploadViaGcsAsync` |

`ResolveMimeType` becoming a pure static function is the useful part — it is the
only piece here that can be unit-tested, and Phase 12 modifies exactly it.

The `Func<Client>? ClientFactory` indirection (line 38, used at line 218) exists
so a key rotation mid-session picks up a new client. Preserve it exactly; it is
easy to drop accidentally when moving the upload block.

`ProcessAttachmentsAsync` (line 55, ~48 lines) and `ResolveFilePath` (line 103)
are already reasonable — leave them.

## `RefinementUiHelper.StartInteractiveRefinementAsync` — 246 of 261 lines

Effectively the whole file is one method. It is almost entirely prompting:
`Console.Write` ×46 and `Console.ReadLine` ×6 (menu at line 49, mode at 85,
step selection at 132, file pick at 169, audio pick at 187, pipeline confirm at
203).

**Sequence Phase 10 before this one.** Converting those six prompts to Spectre
`SelectionPrompt`/`ConfirmationPrompt` dissolves most of the body on its own.
Split whatever remains into `SelectRefinementMode` / `SelectInputFiles` /
`SelectAudioFile` / `LaunchAsync` — but measure after Phase 10 rather than
planning the cut now, because the shape will have changed.

## Vertex

Same treatment the AI Studio twins already had in Phase 4/4.5:

* `ProcessPreparedVideoAsync` (248) → mirror AI Studio's split into
  `ComputeBaseName` / `ResolveRefinementClientAndConfigureParams` /
  `TranscribeSegmentsAsync` / `FinalizeVideoOutputAsync` with a
  `VideoProcessingState` class. The AI Studio version is the reference
  implementation — copy its shape rather than inventing a second one.
* `ReplLoopAsync` (183) → the one-method-per-command flattening from
  `AiStudioAutoExtractionSession.Repl.cs`, ideally as a
  `VertexAutoExtractionSession.Repl.cs` partial.
* `SendHistoryHandshakeAsync` (167) and `RunDiagnosticChatTurnAsync` (157).
* Guard-clause flattening for the 223 lines at ≥5 indent levels — the worst in
  the repo by a factor of two.

Compile- and diff-verified only; this code cannot run. Do it **after** the AI
Studio side of Phase 11, so the twin is always the proven reference.

## Ordering within Phase 11

```
1. RefinementOptions + the mutual-exclusion invariant   (small, compile-checked)
2. LatexRefinementSession partial split                 (mechanical, verbatim moves)
3. AttachmentUploader branch split                      (unblocks Phase 12)
4. RefinementUiHelper                                   (after Phase 10)
5. Vertex catch-up                                      (last, unverifiable)
```

Each step: build 0/0, 85+ tests green, UI-string diff empty (Phase 11 changes no
output — if the diff is non-empty, something moved that shouldn't have), and
`[AI Context]`/`[Human]` comment counts unchanged.
