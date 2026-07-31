# `lecx` — the headless CLI

Drive the whole lecture pipeline — video → segments → LaTeX → PDF — from the command
line, without touching a menu. Built so an AI agent or a script can run each stage
independently.

**Running the program with no arguments still opens the interactive menu, unchanged.**
Arguments are what select the CLI.

## The four documents

| File | Read it when |
|---|---|
| **[agents.md](agents.md)** | You are an AI agent or writing automation. **Start here** |
| [commands.md](commands.md) | You need the full flag reference and JSON shapes |
| [pipeline.md](pipeline.md) | You need to know what each stage writes and where |
| [parallel.md](parallel.md) | You want several videos running at once |

## 60-second start

```bash
dotnet build
```

The binary lands at `bin/Debug/net10.0/lec-extraction-prog.exe`; `lecx.cmd` in the
repository root forwards to it.

```bash
# What is configured right now? Does the API key resolve?
lecx config profiles

# What would a run do? No API call, no cost.
lecx plan --folder "D:/lecture-videos/analysis2"

# Rehearse one video without spending anything.
lecx run --video "D:/lecture-videos/.../02-19-2026-thursday-week1-....mp4" --dry-run

# Do it.
lecx run --video "D:/lecture-videos/.../02-19-2026-thursday-week1-....mp4"
```

Output lands in `<source>/extracted_output/<lecture-name>/` unless `--out` says
otherwise.

## The three things worth knowing immediately

**`plan` costs nothing and prevents expensive mistakes.** It reports the videos matched,
the requests still pending, the model and key profile, and whether that key resolves.
A wrong `--folder` shows up here for free.

**Config is read-only by default.** The app rewrites its own `*Config.json` during normal
runs; in CLI mode that writeback is off, so an unattended run cannot change the settings
you come back to. `--save-config` opts back in; `config set` writes deliberately.

**Finished parts are only reused for two hours.** After that a retry re-requests — and
re-pays for — every segment. `--resume-window <hours>` widens it, `--force` ignores it.

## Free vs. paid

| Free | Paid |
|---|---|
| `plan`, `config`, `media probe/segment/audio`, any `--dry-run` | `run`, `extract run`, `refine run`, `batch`, `pdf compile` (unless `--fix-loop 0`) |

## Exit codes

`0` success · `1` crash · `2` usage · `3` unattended prompt unanswered ·
`4` config/credentials · `5` API gave up · **`6` partial success**

`6` deserves attention: some videos produced output and others did not.
