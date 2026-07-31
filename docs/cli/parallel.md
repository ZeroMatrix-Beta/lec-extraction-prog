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

## Watch for output-folder collisions

Sharding guarantees two workers never get the same *file*, but two different files can
still resolve to the same *output folder*: `lecture.mp4` and
`lecture-speed-1-compressed.mp4` both strip to `lecture`. `plan` and `batch` both report
this in their warnings. Select one variant before running a batch over such a folder.
