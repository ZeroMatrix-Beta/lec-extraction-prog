# Running several videos at once

**Supersedes `Documentation.md` §4.6.** That section tells you to copy
`bin/Debug/net10.0/` into `instance1/`, `instance2/`, … so each instance gets its own
config file. You no longer need to: `--config-dir` and the per-run flags give the same
isolation without copying anything.

---

## Why processes and not threads

This is not a preference. Four pieces of process-global mutable state make in-process
parallelism actively wrong:

| State | What breaks |
|---|---|
| `InteractiveDelay.LastGenerationCompletionTimeUtc` | **The decisive one.** A single process-wide rate-limit clock, written after every generation and read to decide how long to wait next. Two sessions in one process would each read the *other's* timestamp — destroying exactly the independent pacing that separate API keys exist to provide |
| `AttachmentUploader.HasJustUploaded` | A single flag written and read across chat, extraction and refinement; concurrent uploads flip each other's |
| `SessionCostLedger` | One set of counters per process, so per-run cost attribution becomes meaningless |
| `Ui` / `AnsiConsole` | One console. Interleaved output, and live status regions corrupt each other |

Separate processes get separate clocks, separate ledgers and separate rate-limit budgets
for free. Making the pipeline thread-safe would be a large refactor of code that already
works correctly for one run at a time.

## `lecx batch`

```bash
lecx batch --folder "D:/lecture-videos/analysis2" \
           --workers "profile=1:model=gemini-3.5-flash,profile=2:model=gemini-3.6-flash"
```

The supervisor contains **no pipeline logic**. It:

1. resolves the video list exactly as `plan` and `run` do,
2. deals videos round-robin across workers — whole videos only, so no two workers ever
   touch the same output folder or `tmp` directory,
3. spawns one `lecx run` child per video, with that worker's `--profile` and `--model`,
4. tees each worker's output to `<log-folder>/worker-N.log`,
5. aggregates exit codes: `0` if all succeeded, `6` if some did, `1` if none did.

Add `--dry-run` to see the assignment without running anything:

```
── 2 Worker ───────────────────────────────────────────────
  Worker 0: Profil 1, Modell gemini-3.5-flash — 7 Video(s)
  Worker 1: Profil 2, Modell (konfiguriert) — 7 Video(s)
  Logs: D:\...\extracted_output\batch-logs
```

## One profile per worker

Two workers sharing a profile share a rate-limit budget, so they finish **slower** than a
single worker would — the opposite of the point. The spec parser refuses it:

```
[batch] [FEHLER] Profile 1 used by more than one worker; they would share one
rate-limit budget. Give each worker its own profile.
```

Parsing is strict for the same reason: a typo that silently fell back to the default
profile would double the load on one key.

`lecx config profiles` lists the profiles and shows which resolve in this environment.

## Doing it by hand

`batch` is a convenience. Two shells work just as well, because the isolation comes from
the flags, not the supervisor:

```bash
# shell 1
lecx run --folder D:/lecture-videos/analysis2 --from 02-16 --profile 1

# shell 2
lecx run --folder D:/lecture-videos/analysis2 --from 03-30 --profile 2
```

Neither writes configuration (writeback is off unless `--save-config`), so they cannot
fight over a `*Config.json`. If you *do* want each to persist its own settings, give each
its own directory instead of copying the build output:

```bash
lecx run --config-dir ./cfg-worker1 --profile 1 --save-config ...
```

## What `--profile` switches

One `--profile N` per worker covers the **whole** pipeline, not just extraction: `run`
also points the refinement session (merge, speech, polish, PDF repair) at index `N` of the
refinement config's key list (`RunCommand.BuildSession`). So four workers on profiles 0-3
use four independent keys end to end. Profile 4 (`API_KEY-latex-refinement`) is only used if
you pass `--profile 4` yourself.

## Logs are written per video, at the end

`batch` collects each child's output with `ReadToEnd` and writes it to `worker-N.log` only
once that video's process exits, so the log does not grow while a lecture is running (that
takes roughly an hour). Live evidence of progress is the output folder (new
`step1-…-partN.tex` files) and `lecx status`, and the per-process session folders under
`C:\Users\miche\gemini-logs\folder-*` (`chat_log.md`).

## Running a hand-picked set of videos

`batch` can only select with `--folder`, `--from` and `--limit`, so it cannot run an
arbitrary, non-contiguous list (for example "exactly the lectures that already have an
output folder"). For that, run `lecx run --video <file> --profile N --parts 4 --out <dir>`
yourself, one loop per key, with a shared work queue so a free key takes the next video
instead of a fixed round-robin. Each `lecx run` is one video, so it keeps the same
guarantees as `batch` (separate process, clock and key).

## Watch for output-folder collisions

Sharding guarantees two workers never get the same *file*. A raw video and its
`-speed-N-compressed` sibling are collapsed to the compressed one before sharding, so they
land on one worker. Only paths you assemble by hand can still share an output folder; `plan`
and `batch` warn about it.
