# The pipeline, stage by stage

Four stages. Each writes to disk, and the next reads what the previous one wrote — which
is why every stage is separately runnable and why a run can resume partway through.

```
   video.mp4
       │
   ┌───▼──────────────────────────────────────┐
   │ 1. Media (FFmpeg)          local, free   │  lecx media segment
   │    compress → 1 FPS, mono                │  lecx media audio
   │    slice into overlapping segments       │
   └───┬──────────────────────────────────────┘
       │  <target>/<name>/tmp/<name>-partN.mp4
   ┌───▼──────────────────────────────────────┐
   │ 2. Extraction (Gemini)            PAID   │  lecx extract run
   │    one request per segment               │
   └───┬──────────────────────────────────────┘
       │  <target>/<name>/step1-<name>-partN.tex
   ┌───▼──────────────────────────────────────┐
   │ 3. Refinement, steps 1-3          PAID   │  lecx refine run
   │    merge + timestamps → speech → polish  │
   └───┬──────────────────────────────────────┘
       │  <target>/<name>/*.tex
   ┌───▼──────────────────────────────────────┐
   │ 4. PDF (LaTeX + optional AI repair)      │  lecx pdf compile
   └───┬──────────────────────────────────────┘
       │
     .pdf
```

`lecx run` performs all four, because the pipeline chains internally: a successful
extraction constructs the refinement session and awaits it, and that session compiles the
PDF as its step 4. `--stop-after extract` turns the chaining off rather than
reimplementing the sequence.

---

## Naming: the folder and the file stem differ

This trips people up, so state it plainly.

| Thing | Derived how | Example |
|---|---|---|
| Output folder | filename minus `-speed-N-compressed` / `-compressed` | `02-19-2026-thursday-week1-Analysis_II` |
| `.tex` stem | the same, plus a `step1-` prefix | `step1-02-19-2026-thursday-week1-Analysis_II` |

So a part file lands at:

```
<target>/<folder-name>/step1-<folder-name>-part2.tex
```

Both come from `ExtractionHelpers.ComputeOutputFolderName` and `ComputeTexBaseName`, which
the planner and the pipeline share — so a plan cannot predict a path the run will not
write.

**Consequence:** `lecture.mp4` and `lecture-speed-1-compressed.mp4` produce the *same*
folder and the *same* part names, which is why the program treats them as one lecture and
uses the compressed file (see "A raw video and its `-speed-N-compressed` sibling" in
[agents.md](agents.md)). Only a script that passes both paths itself can make them collide;
`lecx plan` reports that in `warnings`.

## The hand-off record

Stage 1 → stage 2 is a `PreparedVideo`, which already exists as the payload the FFmpeg
producer sends to the Gemini consumer over a bounded channel. `media segment --json`
emits exactly that record, so nothing about the boundary is CLI-specific:

```json
{
  "sourceVideoPath": "...",
  "outputFolder": "...",
  "tempFolder": "...",
  "segments": [ { "filePath": "...", "startTimeSeconds": 0 },
                { "filePath": "...", "startTimeSeconds": 2398 } ],
  "cameFromCache": false,
  "sourceDurationSeconds": 5036
}
```

## Segment geometry

With `N` parts and `V` seconds of video, each segment is
`(V + (N-1) × overlap) / N` seconds long and part *i* starts at
`i × (segmentLength − overlap)`. The overlap exists so a sentence spanning a cut is not
lost; it is why part 2 of an 84-minute lecture starts at 2398 s rather than 2638 s.

`lecx media probe` reports the resulting geometry before anything is cut.

`N` is `NumberOfParts`, or with `--parts auto` the video length divided by 22.5 minutes,
rounded: a 45-minute lecture gets 2 parts, a 90-minute double lecture 4, so every segment
stays around 25 minutes whatever the lecture's length.

## Caching and resume — two different windows

| Layer | Window | Where |
|---|---|---|
| FFmpeg segments (`.mp4`) | **48 hours** | `VideoSegmentProducer` |
| Transcribed parts (`.tex`) | **2 hours** | the extraction session |

The 2-hour one is the expensive one: past it, every part is re-requested and re-billed.
It was a hardcoded constant with no way to reach it; `--resume-window` and `--force` now
expose it. The FFmpeg cache is additionally validated — an incomplete set, a part smaller
than 1 KB, or a count that disagrees with `--parts` is discarded and re-cut.

**The compressed intermediate is not validated.** For a raw video the producer first writes
`tmp/<name>-speed-1-compressed.mp4` and then cuts the segments from it. On the next run that
file is reused as soon as it is **larger than 1 KB and younger than 48 hours**; its length is
never checked. A compression that was killed (Ctrl+C, Task Manager, a crashed run) leaves a
truncated file that passes, and the run then transcribes only the first part of the lecture,
without any error. After killing FFmpeg, delete that file or replace it with a complete copy.
A copy needs a fresh modification time (`cp`, not `cp -p`), or it counts as older than 48
hours and is recompressed anyway.

## The header stack at the top of every `.tex`

Every file a stage writes opens with one comment block per step that produced it, newest
on top — the configuration, date and token usage of the whole chain, like the rings of an
onion. The first line of each block names its step, numbered like the file prefixes:

```
% ==========================================
% Step 3/4 - Speech refinement (refinement step 2)
% Output: step3-<name>-offset-speech_refined.tex
% Input: step2-<name>-offset-merged.tex
% Model: ...  Temperature: ...  Processed on: ...
% ------------------------------------------
% Token Usage (1 Request(s)):
%   ... Output Tokens / Thinking Tokens listed separately ...
% ==========================================
% ==========================================
% Step 2/4 - Merge & timestamp alignment (refinement step 1)
% ...
% ==========================================
% ==========================================
% Step 1/4 - Transcription, 4 part(s) combined
% ...
%   - Part 1: <part>.mp4 | start 00:00:00 | model ... | processed ... | prompt ... | output ... | thinking ...
% ==========================================
```

The program stacks the blocks, not the model: a step reads the blocks of its input, puts its
own on top, and drops any blocks the model echoed back. Inside a combined document each
`% --- TEIL n` separator is followed by that part's own block, and every refinement step
restores those blocks under each separator the model kept. The final refinement usually
drops the separators; the one-line-per-part summary in the Step 1 block keeps the per-part
facts after that.

Token counts: *Output* is the visible answer only, *Thinking* is billed on top of it, and a
step that needed continue requests reports the sum over all of them.

## Continue requests ("please continue")

A response that stops before the model's completion marker (cut off at `MaxOutputTokens`,
thinking tokens included) is followed by an automatic **continue request**, which re-sends
the whole history. Each one is a full extra paid request.

| Stage | Continue prompt | Limit per part/step |
|---|---|---|
| Extraction (per part) | `Continue` if the model reached its segment-limit marker, otherwise `[IMPORTANT] Your response was cut short … Please "continue" exactly where you left off` | `MaxContinueRequests` (15) |
| Refinement steps 1-3 | always the `[IMPORTANT] Your response was cut short … Please "continue"` form | per-step `MaxContinueRequests` (10) |

Both stop early after 3 responses in a row that are empty or repeat the previous tail
(`ContinueBudget`).

**How to count them after a run.** For each part or step, `continues = Requests − 1`, where
`Requests` is the number in its header, `Token Usage (N Request(s))`. The Step 1 block
lists every part separately; steps 2-4 carry their own header. Extraction continues are
also logged in `C:\Users\miche\gemini-logs\folder-*\chat_log.md` as
`[Continue Prompt für Part N]`. Add the PDF-repair rounds if any ran
(`step4-compile-log.txt`, `compile-log-step5-last_tryN.txt`). `lecx run --json` reports the
total as `generationRequests` (all paid requests of that process; subtract the planned
`requestsPlanned` for the continues).

## Backends

Everything above describes **AI Studio**, which is what the CLI drives. Vertex AI exists
in the codebase behind `AppConfig.IsVertexAiEnabled` and is not exposed by the CLI.
