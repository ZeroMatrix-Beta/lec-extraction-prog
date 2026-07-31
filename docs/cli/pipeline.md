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
folder and the *same* part names. Processing both means each reads the other's output as
its own cache. `lecx plan` reports this in `warnings`.

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

## Caching and resume — two different windows

| Layer | Window | Where |
|---|---|---|
| FFmpeg segments (`.mp4`) | **48 hours** | `VideoSegmentProducer` |
| Transcribed parts (`.tex`) | **2 hours** | the extraction session |

The 2-hour one is the expensive one: past it, every part is re-requested and re-billed.
It was a hardcoded constant with no way to reach it; `--resume-window` and `--force` now
expose it. The FFmpeg cache is additionally validated — an incomplete set, a part smaller
than 1 KB, or a count that disagrees with `--parts` is discarded and re-cut.

## Backends

Everything above describes **AI Studio**, which is what the CLI drives. Vertex AI exists
in the codebase behind `AppConfig.IsVertexAiEnabled` and is not exposed by the CLI.
