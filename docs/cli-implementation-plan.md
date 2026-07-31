# Implementation Plan — a headless CLI for `lec-extraction-prog`

**Goal:** every stage of the pipeline runnable as a single non-interactive command, so an AI agent
(or a script, or `cron`) can drive it — while the interactive menu keeps working untouched.

**Status:** **C0 done** (2026-07-31). C1–C10 open.

---

## 0. The starting point, measured

* `Program.Main()` took **no `string[] args`** — there was no CLI surface at all.
* A headless run died on the *first* menu: `NotSupportedException: Cannot show selection prompt
  since the current terminal isn't interactive`. Piping stdin does not help; Spectre's
  `SelectionPrompt` needs a real terminal.
* Every decision in the pipeline is a prompt (`Ui.Select/Confirm/ConfirmOrBack/Ask`,
  `SetupQuestionPrompt.Ask`) — no code path supplies those answers from anywhere but a keyboard.
* `ConfigLoader<T>.Load` reads `AppDomain.BaseDirectory`; `Save` writes **both** that path *and*
  `Directory.GetCurrentDirectory()`. That asymmetry is why running via `dotnet run` from the repo
  root dirties the tracked `*Config.json`.

Those facts set the phase order: the prompt seam and the config seam must exist **before** any
stage command, or each command grows its own bypass.

---

## 1. Decisions taken

| Decision | Choice | Why |
|---|---|---|
| Parser | **`System.CommandLine` 2.0.10** | `Spectre.Console.Cli` has **no stable release compatible with the pinned `Spectre.Console` 0.57.2** (stable tops out ~0.55.0, then jumps to `1.0.0-alpha`). `System.CommandLine` is stable, Microsoft-supported, and touches nothing in the Spectre version pin. Spectre stays what it is — the *output* layer |
| Invocation | `Main(string[] args)`: **empty args → today's `MainMenu`**, otherwise the CLI | Zero regression risk for the interactive user |
| Binary name | Assembly name **unchanged**; `lecx.cmd` shim at the repo root | Renaming the assembly would break `.vscode/launch.json` and six documented paths in `Documentation.md` for a cosmetic gain |
| Stage boundaries | Where the pipeline **already writes to disk** | Every command resumes from the previous one's output; nothing new has to be persisted |
| CLI language | **English** (help, errors, JSON keys); ASCII in help text | The German `Ui` strings stay for the interactive app. CLI mode also forces `Console.OutputEncoding = UTF8`, because the default Windows OEM code page drops `→` and `—` |

---

## 2. What the pipeline already does (verified, not assumed)

Three requirements turned out to be mostly satisfied already:

* **End-to-end already works.** `FinalizeVideoOutputAsync` constructs a `LatexRefinementSession`
  and awaits `StartAsync()`, which runs steps 1–3 then step 4 (PDF), gated by
  `GoIntoLatexRefinement` (**default `true`**). The CLI needs a *name* for it and a way to stop
  early, not a new orchestrator.
* **Multiple files already work**, sequentially — `ProcessFilesAsync` takes `string[]` and loops,
  FFmpeg running one video ahead through a capacity-1 bounded channel.
* **Resume already works** — `TranscribeSegmentsAsync` skips any part whose `.tex` is on disk, but
  only if younger than **2 hours** (`VideoProcessingState.CacheDuration`, hardcoded, no config
  key). So there is no `resume` command; there are `--resume-window` and `--force` flags. That
  invisible 2-hour cliff is the thing to expose: an agent retrying later silently re-pays.

Two contracts already exist and should be reused rather than reinvented:

* **`PreparedVideo`** (`src/Extraction/Model/PreparedVideo.cs`) — the FFmpeg→Gemini channel payload,
  already a `sealed record`. This is the `media segment` → `run` hand-off, serialised.
* **`RefinementOptions.ForFile`** and **`SetupContextAndProcessAsync(files)`** — the parameterised,
  non-interactive entry points the CLI needs. The latter is `private` only because `StartAsync`
  wraps it in the mode menu.

---

## 3. Parallelism: multiple processes, not threads

Forced by process-global mutable state:

| State | Why it breaks in-process parallelism |
|---|---|
| `InteractiveDelay.LastGenerationCompletionTimeUtc` (static) | **Decisive.** One process-wide rate-limit clock, written at 5 sites, read at `LatexRefinementSession.Generation.cs:226` and `Pdf.cs:391`. Two concurrent sessions would read each other's timestamp — destroying the independent pacing that separate API keys exist to provide |
| `AttachmentUploader.HasJustUploaded` (static) | Written and read across chat, extraction *and* refinement |
| `SessionCostLedger` (static counters) | One ledger per process; per-run attribution becomes meaningless |
| `ConfigLoader<T>.Save` | Fixed paths; two runs saving different models fight over one file |
| `Ui` / `AnsiConsole` | One console; live regions corrupt each other |

`Documentation.md` §4.6 already documents multi-instance runs, but the recipe is to copy
`bin/Debug/net10.0/` per instance so each gets its own config file. **`--config-dir` plus
`--model` / `--profile` / read-only config makes that copying obsolete.**

`lecx batch` is therefore a *process supervisor*: it shards the video list (no two workers touch
the same video, so no `tmp` or output collision), spawns `lecx run` children, tees each child's
output to its own log, and aggregates their JSON. It contains **no pipeline logic**.

---

## 4. The command tree

```
lecx run            (--video <mp4> | --folder <dir>) [--from <name>]
                    [--stop-after extract|refine|pdf] [--model <id>] [--profile <n>]
                    [--backend aistudio|vertex] [--out <dir>] [--prepared <manifest>]
                    [--resume-window <h>] [--force] [--dry-run]
lecx batch          --folder <dir> --workers <spec> [--dry-run]

lecx plan           (--folder <dir> | --video <mp4>)        # no API call, no cost
lecx media          probe | segment | audio  --input <mp4> [--out <dir>]
lecx extract run    (as run, but never chains into refinement)
lecx refine run     --tex <file> [--step 1|2|3] [--through-end] [--audio <m4a>]
lecx pdf compile    --tex <file> [--fix-loop <n>]           # paid unless --fix-loop 0
lecx ask            (--prompt <text> | --prompt-file <f>) [--attach <f>]...
lecx config         list | get | models | profiles | folders        # set: lands with C2
```

YouTube (the third extraction mode) is **out of scope for v1**.

Global on every command: `--json`, `--dry-run`, `--yes`, `--save-config`, `--config-dir`, `--quiet`.
Config writeback is **off unless `--save-config`** is passed.

Exit codes: `0` ok · `1` unexpected · `2` usage · `3` unattended prompt had no answer ·
`4` config/credential · `5` API exhausted retries · `6` partial success.

---

## 5. Phases

Each ends green: build 0/0, tests passing, UI-string drift reviewed. One commit per phase.

| # | Phase | Size | Notes |
|---|---|---|---|
| **C0** | ✅ **done** — `Main(string[] args)`, command tree, read-only `config` commands | small | 26 new tests. `config set` deferred to C2 on purpose: writing today would go through the double-writing `Save` |
| **C1** | `IPromptSource` seam | medium | **load-bearing.** `PresetPromptSource` *throws* `UnattendedPromptException(promptTitle)` naming the missing switch rather than defaulting into a wrong model. `PromptResult<T>` unchanged, so no call site moves. Scope: the ~20 CLI-reachable sites — **not** the 13 in `FfmpegInteractiveSession` (bypassed by `media`), nor `InteractiveDelay`, `AttachmentUploader:275`, `ResponseStreamPrinter:73`, which already guard on `Console.IsInputRedirected` |
| **C2** | Config seam + `config set` | small | read-only by default; `--config-dir`; must cover **both** `Save` targets |
| **C3** | `media probe/segment/audio` | medium | free to test; settles the `--json` shape. `segment` emits `PreparedVideo` |
| **C4** | `plan` + `--dry-run` | medium | a pure `ExtractionPlan` record: folder → files → `VideoDateParser` → segments → request count |
| **C5** | `run` / `extract run` | large | split `StartAsync` into `StartInteractiveAsync()` and public `RunAsync(files)`. `ProcessFilesAsync` must **return** its `anyVideoFailed` flag (today it computes it and returns `void`) or exit code 6 is unreachable. **One real paid run to verify** |
| **C6** | `refine run`, `pdf compile` | medium | `CompilePdfAsync` is *private*, is an instance method needing a `Client`, and its repair path calls the model — it needs a public entry and is not free |
| **C7** | `--json` + exit codes + stderr routing | small | five writers bypass `Ui`: `VideoBatchSelector.cs:85`, `InteractiveDelay`, and four `Console.Write` in `YouTubeTaskPrompt` (17, 24, 30, 62) |
| **C8** | `batch` | medium | supervisor only |
| **C9** | `ask` | small | one-shot, not a REPL |
| **C10** | Docs | medium | `docs/cli/`: `README.md`, `commands.md`, `pipeline.md`, `parallel.md` (supersedes `Documentation.md` §4.6), `agents.md`. Then `.agents/rules/AGENTS.md` rule 9, then the root `README.md` last |

C0–C4 cost nothing to develop or verify. The first euro is spent in C5, behind `--dry-run`.

---

## 6. Verification

* **Per phase:** `dotnet build` 0/0, `dotnet test` green, `tools/dump-ui-strings.sh` drift read
  line by line before regenerating.
* **Headless:** every command run with redirected stdin — the condition that used to throw.
* **C2:** `git status` clean after a `lecx` run is the concrete test that read-only config works.
* **C5:** `--dry-run` first, then the same command for real; compare the `.tex` against the
  interactive path's output for the same video.
* **C8:** two workers on two profiles over four videos; no two workers touch the same file, and
  the aggregate JSON accounts for all four.

---

## 7. Known defect found while building C0

`ModelSelection.Available` grows on every launch — 45 stored entries with 5 distinct in the live
`AiStudioAutoExtractionConfig.json`. Same defect commit `4cc6b2b` fixed for `PredefinedSourceFolders`
and the key env names; that fix's `DeduplicateSetLikeArrays` never covered this array. `config
models` reports distinct values plus the duplicate count so the anomaly stays visible. Fixing it is
tracked separately — note that `CurrentModelIndex` indexes into that array, so deduplicating shifts
indices.
