# 📚 Detailed System Documentation / Detail-Dokumentation

*(See below for the German version / Deutsche Version unten)*

## 🇬🇧 English

This document provides a deep dive into the architecture, configuration quirks, and API constraints of the AI Lecture Extraction & Processing Pipeline. It is intended for developers and advanced users who want to understand the inner workings of the system.

---

## Codebase Philosophy: Dual-Commenting Paradigm

The entire codebase strictly follows a dual-commenting paradigm to ensure seamless human-AI collaboration:
- **`[AI Context]` (English):** Targeted at LLMs. Explains the *why* - including architectural decisions, prompt constraints, API mechanics, and token-saving strategies.
- **`[Human]` (German):** Targeted at human developers. Explains the *how* - focusing on business logic, basic flow, and UI instructions.
When modifying the code, developers and AI agents must maintain this separation of concerns.

---

## 1. Configuration Hierarchy & The "Array Merge" Quirk

The application uses the `Microsoft.Extensions.Configuration` binder to load settings from multiple sources. The hierarchy is as follows (last loaded wins):
1. `AppConfig.cs` (Hardcoded C# defaults)
2. `appsettings.json` (Global overrides)
3. Specific session configs (e.g., `VertexAutoExtractionConfig.json`)

### The Array Merging Gotcha
A critical behavior of the .NET Configuration Binder is how it handles arrays (like `HistoryPreloadPaths` or `SystemInstructionPaths`).
If your base configuration (`appsettings.json`) defines an array with 2 items, and your specific session config (`VertexAutoExtractionConfig.json`) defines an array with 1 item, **the binder does not replace the array; it merges them by index**.
- Index 0 from the base config is overwritten by Index 0 from the session config.
- Index 1 from the base config remains intact and is loaded into the final array.

**Solution:** To prevent unwanted files from being loaded, we ensure that base arrays in `AppConfig.cs` and `appsettings.json` are strictly empty (`[]`). You must explicitly define all required paths in your specific session `.json` file.

---

## 2. Gemini API: Models & Tokenization

### Tokenization of PDFs and Images
When attaching a PDF (e.g., an academic script) to the context, Gemini treats **each page as an image**. Token costs can vary drastically depending on the model version:
- **gemini-3.5-flash / gemini-1.5-flash:** Highly optimized for efficiency. A single PDF page might cost only a base token amount (e.g., ~258 tokens).
- **gemini-2.5-pro:** Geared towards deep reasoning and high accuracy (especially for mathematical formulas). It often processes images and PDF pages at a much higher resolution, breaking a single page into multiple tiles (e.g., 2 to 4 tiles). Consequently, a single page can cost 516 to 1000+ tokens. 
*Note: This is why uploading a 400-page PDF might cost ~114k tokens on Flash, but ~228k tokens on Pro.*

---

## 3. Advanced Reasoning: Thinking Budgets & Levels

Google's Gemini models support advanced internal reasoning ("Thinking") before emitting an output. This is strictly managed via the `ThinkingConfig` object in the API payload.

### Thinking Budget
The `ThinkingBudget` determines the maximum number of output tokens the model is allowed to spend "thinking" internally before generating the final response.
- **Vertex AI Constraints:** As of mid-2026, the Vertex AI endpoint enforces a strict integer limit for the thinking budget. Supported values range from **128 to 32768 tokens**. 
- **Code implementation:** The application intercepts any budget configured above 32768 and strictly clamps it to `32768` right before dispatching the request to Vertex AI, preventing `ClientError: thinking_budget is out of range` exceptions.

### Thinking Level
The `ThinkingLevel` is an `enum` (`MINIMAL`, `LOW`, `MEDIUM`, `HIGH`) that dictates the depth of reasoning.
- **Model Compatibility:** `ThinkingLevel` is fully supported by the newer **Gemini 3.x** frontier models. However, models like **Gemini 2.5 Pro** do not support the `thinkingLevel` parameter and will reject the request if it is included.
- **Code implementation:** The `requestConfig` builder checks the model name (`_config.Model`). If the model is a `gemini-3` variant, both `ThinkingLevel` and `ThinkingBudget` are applied. If the model is a `gemini-2.5` variant, the application silently strips the `ThinkingLevel` and relies entirely on the `ThinkingBudget` to avoid `thinking_level is not supported` errors.

---

## 4. Google AI Studio vs. Google Cloud Vertex AI

The pipeline supports both environments, but they handle payloads very differently under the hood:

### Google AI Studio (Developer Tier)
- Uses the **Google File API**.
- When processing video files or PDFs, files are uploaded directly to the generative AI file storage.
- Rate limits (RPM/TPM) are usually stricter for free-tier users, but it is excellent for rapid prototyping.

### Google Cloud Vertex AI (Enterprise Tier)
- Uses **Google Cloud Storage (GCS) Buckets**.
- The application automatically uploads local files into your specified `GcsBucketName` and attaches the `gs://...` URI to the Gemini prompt.
- **Storage Cost Management:** Because processing hundreds of overlapping video chunks can rack up storage costs, the application utilizes a `GcsWorkspace.PurgeAsync()` routine. After a chunk is successfully processed (or if an exception occurs), the application aggressively deletes the temporary files from the GCS bucket. 
*Note: Un-deleted files in a bucket do not consume prompt tokens in future requests, as the API only processes the exact `gs://` URIs sent in the specific request payload.*

---

## 4.2 Modular System Prompt & Attention Map Priming

### Modular File Injection
Instead of relying on monolithic markdown blobs, the pipeline resolves multiple distinct instruction files (`transcription.md`, `hard-specs.md`, `environments.md`, etc.) via `SystemInstructionPaths`. Before injecting each file into the prompt payload, the application wraps it with clear delimiter headers (`******\n------\n******\nHere is the file 'filename.md':\n`). This explicit separation prevents LLM attention bleeding between distinct instructional domains.

### Attention Map Priming
To maximize orientation and logical hierarchy adherence, the application generates an ASCII folder tree structure of the loaded system instructions and injects it at the absolute beginning of the system prompt. By observing this structural "map" first, Gemini primes its internal attention mechanism on codebase orientation and logical category separation before parsing specific syntax rules.

### Strict Multipart Separation (Vertex AI)
Vertex AI enforces strict schema purity on the `SystemInstruction` field: it exclusively accepts pure `text` parts. If binary data (such as image attachments from `Training History`) is passed inside `SystemInstruction`, Vertex AI rejects the request with a `ClientError`. The pipeline intercepts binary history attachments and dynamically routes them to the user prompt `contents` payload, guaranteeing cross-tier compatibility.

### Prompt Assembly & Dump Logging
To verify exact prompt construction and ensure `Training History` metadata is captured, `ExtractionHelpers.LogSystemInstructionDumpAsync` executes strictly after all text instructions and binary attachments have been assembled. It writes the complete compiled prompt into a timestamped `system_instruction_dump.md` file inside the active log directory.

```
+---------------------------------------------------------------------------------+
|                         PROMPT PAYLOAD ASSEMBLY MATRIX                          |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  [SystemInstruction Payload (Strict Pure Text)]                                 |
|  ├── 1. Attention Map Priming (ASCII Folder Tree of Instruction Hierarchy)      |
|  ├── 2. transcription.md (Framed with delimiter headers)                        |
|  ├── 3. hard-specs.md (Framed with delimiter headers)                           |
|  └── 4. environments.md (Framed with delimiter headers)                         |
|                                                                                 |
|  [User Content Payload (Multimodal)]                                            |
|  ├── Binary Attachments (e.g., Training History Images / Handwriting Samples)   |
|  └── Video Segment Chunk (gs://... or File API URI)                             |
+---------------------------------------------------------------------------------+
```

---


## 4.3 Intelligent Context Caching & MD5 Auto-Reload

To optimize processing times and eliminate redundant prompt upload costs when transcribing sequential batches of lecture videos, `ContextCacheStateManager` implements automated remote caching across both AI Studio and Vertex AI.

### MD5 Checksum Invalidation
Whenever an auto-extraction session boots, the application computes a unified MD5 checksum across the combined text of all configured system instruction files and preloaded history attachments.
- **Cache Match:** If a valid remote cache exists (`vertex_cache_state.json` / `aistudio_cache_state.json`), hasn't expired on Google's servers, and its stored MD5 checksum matches the newly computed checksum, the application reuses the remote cache instantly (`cachedContent/...`).
- **Cache Mismatch (Auto-Reload):** If a developer edits even a single word inside any markdown instruction file (e.g., tweaking `hard-specs.md`), the newly computed MD5 checksum diverges from the saved state. The application detects this mismatch, issues a remote `Delete` command for the stale Google Cloud Cache, and generates a fresh remote cache for the subsequent video batch automatically.

```
+---------------------------------------------------------------------------------+
|                         REMOTE CONTEXT CACHE LIFECYCLE                          |
+---------------------------------------------------------------------------------+
|  Start Auto-Extraction Session                                                  |
|         │                                                                       |
|         ▼                                                                       |
|  Compute Local MD5 Hash (All System Instructions + Preloaded History)           |
|         │                                                                       |
|         ├─────────────────────────────────────────┐                             |
|         ▼                                         ▼                             |
|  [Cache File Exists & Active?]             [No Cache / Expired / Hash Mismatch] |
|         │                                         │                             |
|  (MD5 Matches Exactly)                            ▼                             |
|         │                                  Purge Stale Remote Cache (if any)    |
|         ▼                                         │                             |
|  ✅ Reuse cachedContent/URI                       ▼                             |
|     (0 Prompt Upload Cost)                 Upload New Context & Save MD5        |
+---------------------------------------------------------------------------------+
```

---


## 4.4 Implicit Prefix Cache Warm-Up (`PrimePrefixCacheAsync`)

Google AI Studio uses a **server-side implicit prefix cache**: if consecutive API requests share an identical prefix (System Instruction + early user-turn text), Google reuses the tokenized representation instead of re-processing it. This can cut prompt-processing cost and latency significantly for each video part in a multi-part lecture.

### How the Warm-Up Works
Before the first video is uploaded, `AiStudioAutoExtractionSession.WarmUpWithBatchedHistoryAsync` sends one or more lightweight "handshake" requests that contain only the System Instruction (and history batches, if `LoadHistoryIntoSystemInstruction = true`). This forces Google to tokenize the full instruction set and place it into the implicit cache while FFmpeg is still cutting the video in the background.

### Batch Handshake Strategy
When history is split across multiple batches (`HistoryBatchCount > 1`), the warm-up sends one handshake per batch, each time extending the System Instruction with the next batch:
- **Intermediate batches** have an incomplete System Instruction → they can never produce a cache hit for Part 1 (different prefix). These handshakes still pre-exercise Google's tokenization pipeline for those partial prefixes.
- **Last batch** has the complete System Instruction, identical to what Part 1 will use → only this handshake can produce an actual cache hit.

### Dummy File (`dummy-part0.tex`) & `PrefixCacheAnchor`
Part 1 always includes a `<reference_context file="part0.tex">` block (a synthetic LaTeX stub) in the user turn. To make the warm-up prefix bit-identical to Part 1, the last handshake also includes this block. `PrefixCacheAnchor.LoadPrefixCacheAnchorText()` loads `dummy-part0.tex` from disk once and caches it in memory for all subsequent calls.

**`SendDummyFileWithEachWarmUpRound`** (config flag): when set to `true`, the dummy block is sent with *every* handshake (not just the last). With `InlinePrecedingLecTexParts: false` the user-turn text is otherwise identical across all parts, so this setting maximizes the chance that even intermediate batches match a future request. Default: `false` (token-saving mode — only the last batch gets the dummy).

### ThinkingConfig Consistency
The `GenerateContentConfig` sent during warm-up uses the **same `ThinkingConfig`** as the actual Part-1 request (same `ThinkingBudget` / `ThinkingLevel` / model-specific routing). A mismatch would create a different cache bucket key, causing a cache miss. Use `DisableThinkingDuringWarmUp: true` to force `ThinkingBudget = 0` for the handshake only (saves thinking-tokens during warm-up at the cost of a guaranteed cache miss for that round).

---


## 4.5 Authentication Setup

Depending on which environment you are targeting, the application requires different authentication setups.

### Google AI Studio (API Keys)
The application dynamically resolves API keys from Windows Environment Variables. To set your keys permanently, run the following commands in PowerShell or Command Prompt:

```powershell
# Set your primary API key for the general sessions
setx API_KEY-ai-studio-test-project-1 "YOUR_GEMINI_API_KEY"

# Set a dedicated API key specifically for the LatexRefinementSession
setx API_KEY_LatexRefinement "YOUR_GEMINI_API_KEY"
```
*(Note: You must restart your terminal and any open IDEs for `setx` changes to take effect.)*

### Google Cloud Vertex AI (IAM Authentication)
Vertex AI uses Google Cloud IAM (Identity and Access Management) instead of static API keys. You must have the Google Cloud SDK (`gcloud` CLI) installed.

1. **Login and set your default application credentials:**
   ```powershell
   gcloud auth application-default login
   ```
2. **Set your active project** (Must match the `VertexProjectId` in your config):
   ```powershell
   gcloud config set project your-vertex-project-id
   ```

## 4.6 Running the Program (Local Console & Parallel Execution)

You can run the program directly from your own Windows Command Prompt (cmd) or PowerShell terminal while you are still working on it in Antigravity or Visual Studio Code.

### How to Navigate and Start the Program
1. **Open your Terminal:** Open Windows Command Prompt (`cmd`) or PowerShell.
2. **Navigate to the Project Directory:**
   Run the following change directory command:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
3. **Compile and Run the Program:**
   To build and start the program from the current directory, run:
   ```powershell
   dotnet run
   ```
   *Note: If the application is already running or the executable is locked, the build might fail to overwrite the executable. See below.*

### Running Multiple Instances Concurrently
You can execute multiple instances of the program in parallel to process different videos at the same time:
1. **Build the binary once:**
   ```powershell
   dotnet build
   ```
2. **Launch multiple instances via direct executable:**
   Open separate terminal windows, navigate to the project root directory:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
   And run the executable directly using its relative path:
   ```powershell
   .\bin\Debug\net10.0\lec-extraction-prog.exe
   ```
   Using the direct `.exe` avoids concurrent build compilation conflicts.
3. **Isolate Configurations (Optional but Recommended):**
   To let each instance have its own permanent configuration:
   - Copy the `bin/Debug/net10.0/` folder to separate directories (e.g., `bin/Debug/instance1/` and `bin/Debug/instance2/`).
   - In your terminal windows, navigate directly into those folders:
     - Window 1:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance1"
       .\lec-extraction-prog.exe
       ```
     - Window 2:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance2"
       .\lec-extraction-prog.exe
       ```
     Each instance will load and save its settings independently within its own folder.

---

## 5. Pipeline Mechanics: FFmpeg & Overlapping Segments

To transcribe a 90-minute lecture, sending the entire video at once often leads to context-window exhaustion or skipped details. The pipeline solves this via:

1. **FFmpeg Slicing:** The video is chopped into strictly defined chunks (e.g., 3 minutes each) with an overlap (e.g., 180 seconds, meaning the last X seconds of chunk 1 are the first X seconds of chunk 2).
2. **Token Optimization:** The video framerate is decimated to `1 FPS`. Since blackboards and slides rarely change sub-second, this preserves visual information while drastically reducing the token payload.
3. **Audio Downmixing:** Audio is compressed and downmixed to mono (`-ac 1`).
4. **Latex Refinement:** Because of the overlaps, the extracted LaTeX chunks will contain duplicated sentences or formulas at the boundaries. The `LatexRefinementSession` passes these chunks to a deterministic AI (Temperature 0.35) with instructions to seamlessly merge them into a single, continuous LaTeX document.

```
+---------------------------------------------------------------------------------+
|                       SLIDING WINDOW CHUNK SCRIPT MERGER                        |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  Lecture Timeline:  0m ---------- 3m ---------- 6m ---------- 9m                |
|                                                                                 |
|  Chunk 1 [0m - 3m]:   [======= Transcribed Text A =======]                      |
|                                    ▲                                            |
|                               Overlapping Boundary (e.g. 180s overlap)          |
|                                    ▼                                            |
|  Chunk 2 [1.5m - 4.5m]:            [======= Transcribed Text B =======]         |
|                                                                                 |
|  LatexRefinementSession (Temp = 0.0):                                           |
|  Fuses boundary duplicate formulas -> Outputs clean unified LaTeX stream        |
+---------------------------------------------------------------------------------+
```

### 🎬 FFmpeg Interactive Manager Dashboard
In addition to automated extraction pipeline preprocessing, option **3** in the main menu starts the **FFmpeg Interactive Preprocessor Dashboard**. It provides full interactive control over manual preprocessing:
- **Interactive Folder Browser:** Browse subfolders, parent folders (`..`), and switch Windows drives (e.g. `d:`, `c:`) to find files.
- **Conversion Dashboard:** Displays selected files and active settings in one visual overview.
- **Flexible Settings Customization:**
  - **Speed Multiplier:** Set video speed between `0.1x` and `10.0x`.
  - **Video Framerate (FPS):** Set custom target framerates.
  - **Audio Formats:** Toggle between downmixing to Mono (optimal for AI transcribing) or copying original Stereo.
  - **Video Scaling:** Toggle between scaling to standard `720p` resolution (recommended) or original resolution.
  - **Compression (Preset):** Choose FFmpeg x264 presets (`ultrafast` to `veryslow`) to trade off compilation time against file sizes.
  - **Time Range Cutting:** Crop a custom subsection of the video (e.g., setting a start timestamp and duration) instead of converting the entire file.
  - **Chunk Splitting:** Set chunk counts and custom overlapping boundary seconds.
- **Custom Mode:** Directly input custom raw FFmpeg args.

---


## 6. Architecture Breakdown: Namespaces & Core Classes

The project lives under `src/`, one folder per bounded context, namespaced `LectureExtraction.<Folder>`. Below is a comprehensive overview of the namespaces and their most critical classes.

### ⚙️ `LectureExtraction.Extraction`
Responsible for the fully automated batch-processing pipeline. It orchestrates FFmpeg processing and AI inference sequentially or concurrently.
- **`AiStudioAutoExtractionSession`**: Manages the extraction pipeline using the Google AI Studio File API. It uses a **Producer-Consumer pattern** (via `System.Threading.Channels`), allowing FFmpeg to process the next chunk while Gemini is analyzing the current one. Split across three files: the core pipeline (`.cs`), the interactive REPL/debug-chat (`.Repl.cs`), and the implicit prefix-cache warm-up (`.PrefixCache.cs`).
- **`VertexAutoExtractionSession`**: The Enterprise equivalent. It uploads files to Google Cloud Storage (GCS) and strictly deletes them after the inference is done (`GcsWorkspace`) to prevent storage cost blowouts. Also carries a ported prefix-cache warm-up in its own `.PrefixCache.cs`, gated behind `EnableImplicitPrefixCacheWarmup` (default off — unlike AI Studio's, this path is not yet verified against the real API).
- **`VideoSegmentProducer`**: The shared FFmpeg producer half of the pipeline (splitting, resume-from-disk caching), used by both sessions.
- **`TexDocumentWriter`**: Builds the `.tex` part/combined-file headers shared by both sessions.
- **`ExtractionHelpers`**: What's left after splitting out the pieces below — `ResolveNonClashingTexPath` and `LogSystemInstructionDumpAsync`.
- **`HistoryFileResolver`, `FileTreeRenderer`, `VideoBatchSelector`, `YouTubeTaskPrompt`, `ModelSyncService`**: Focused pieces split out of the former `ExtractionHelpers` grab-bag.
- **`RefinementUiHelper`**: Console UI for launching and configuring the LaTeX refinement step from either extraction session.
- **`AudioTrackExtractor`**: Wraps the background AAC-extraction task used for LaTeX refinement's audio input.

### 💬 `LectureExtraction.Chat` & `LectureExtraction.Refinement`
Handles the interactive REPL (Read-Eval-Print Loop) for manual debugging, as well as deterministic Post-Processing that behaves like a chat session.
- **`LatexRefinementSession`** (`Refinement`): The post-processing engine. It feeds the overlapping `.tex` chunks generated by extraction into Gemini, instructing it to resolve duplicates and merge them into one continuous document.
- **`DirectAiChatSessionVertex` / `DirectAiChatSessionAiStudio`** (`Chat`): The core REPL classes. They handle user terminal input, allow dynamic parameter changes (e.g. `/set temp 0.5`), process `/attach` commands, and maintain conversation history.

### 🔧 `LectureExtraction.Configuration`
Centralized configuration management.
- **`ConfigLoader<T>`**: A generic loader implementing the `Microsoft.Extensions.Configuration` binder. It merges defaults from `AppConfig.cs`, global overrides from `appsettings.json`, and specific settings from `{Session}Config.json`.
- **`AppConfig`**: The single source of truth for global, hardcoded fallback parameters (e.g., fallback paths, `DefaultThinkingBudget`, `VertexProjectId`) and feature flags (`IsVertexAiEnabled`, read from `appsettings.json` — replaces the old `Program.Activate_Vertex` static field, which required a recompile to change).

### 🎬 `LectureExtraction.Media`
Wraps the local `ffmpeg.exe` binary to preprocess media before sending it to the cloud.
- **`FfmpegToolkit`**: A headless command builder. Contains logic like `ProcessSplitVideoAsync` (slicing video into 3-minute chunks with a 3-minute overlap), decimating framerates to 1 FPS (`-vf fps=1`), and downmixing audio to mono (`-ac 1`).
- **`FfmpegInteractiveSession`**: A console-based UI for users to manually trigger FFmpeg compressions without running the AI pipeline.
- **`VideoDateParser`**: Helper to extract date metadata from lecture video filenames.

### 🖥️ `LectureExtraction.ConsoleUi`
Generic interactive console prompts, independent of any specific session type.
- **`ConfigurationPrompts`**: Confirm-or-change prompts for source folder (`PromptForSourceFolder`), model, and API-key profile.
- **`DirectoryTreeRenderer`, `FileSelectionPrompt`**: Folder browsing and file-tree rendering helpers.

### 🏗️ `LectureExtraction.Infrastructure`
Cross-cutting concerns like session logging and string utilities.
- **`SessionLogger`**: Automatically creates timestamped directories for every session (e.g., `folder-X-date`). It dumps raw LaTeX outputs to files and maintains a Markdown log (`chat_log.md`) of the entire interaction.
- **`StringExtensions`**: General string helpers (was `StringHelper`).

### 🌐 `LectureExtraction.GoogleAi`
- **`GoogleAiClientBuilder`**: Encapsulates the raw HTTP `HttpClient` construction and API-key/credential resolution for both AI Studio and Vertex.
- **`AttachmentUploader`**: A sophisticated file discovery system (was `AttachmentHandler`). When a user types `/attach file.pdf`, this class searches through a list of fallback directories (the `HistoryPreloadPaths` and `IncludePaths`), finds the file, and prepares it for upload.
- **`ApiRetryPolicy`**: Contains logic to gracefully handle HTTP transient errors, such as rate limits (HTTP 429) or temporary server errors (HTTP 503). Was `ApiResilience`.
- **`ApiKeyProfileResolver`**: Resolves an API-key profile index (0-3) to its environment-variable name.
- **`PrefixCacheAnchor`**: Loads and caches `dummy-part0.tex`, the shared implicit-prefix-cache anchor used by both extraction sessions.
- **`GcsWorkspace`**: The shared Vertex GCS bucket cleanup routine.
- **`ModelCapabilities`**: The single `SupportsThinking` check, shared across every session type.

### 📄 `LectureExtraction.Latex`
- **`LatexToolkit`**: Helper methods to parse raw markdown outputs from Gemini, strip out ` ```latex ` code blocks, and validate the resulting strings.
- **`LatexTimestampAdjuster`**: Tools for manipulating and adjusting embedded timestamps within the LaTeX document. Was `LatexTimestampHelper`.
- **`LatexResponseCleaner`**: Strips model chatter/formatting artifacts from a raw Gemini response before it's written to disk.

### 🚪 `LectureExtraction.App`
- **`Program`**: The entry point — just `Main()` and top-level exception handling.
- **`MainMenu`**: The top-level interactive loop.
- **`SessionFactory`**: Config-load → client-build → session-construct wiring for each of the five session types.
- **`SourceFolderMenu` / `ApiKeyProfileMenu`**: The two configuration sub-menus.

---

## 7. Known API Limitations & Troubleshooting

### Cache Priming vs. Roundtrips (Why not send everything at once?)
To save an API roundtrip, one might be tempted to send the `training-history` and the 30-minute lecture video in a single prompt. However, this is highly counterproductive:
1. **Cache Priming & Focus:** By sending the massive history first and demanding an "Acknowledgment", the model is forced to parse and build an internal context cache (Context Caching) purely for the rules. When the actual video arrives in the next prompt, the model focuses 100% on execution rather than splitting its attention.
2. **Context Overload:** Shoving thousands of lines of history and a large video into a single prompt often overwhelms the model, leading to hallucinated outputs or `500 Internal Server Errors`.

### HTTP 500 Internal Error (Google Server Crash)
If the application gets stuck in a retry-loop with an `Internal error encountered (HTTP 500)`, it means the Google backend crashed while processing the request. This is rarely a network issue, but usually a prompt/payload issue. Common culprits include:
- **Overloaded System Instruction:** If `LoadHistoryIntoSystemInstruction` is set to `true`, the pipeline injects the history into the `SystemInstruction` (using XML framing `<file path="...">` and embedding images inline via `InlineData`). Note: While inline bytes prevent external URI resolution issues, extremely massive history sets can still exceed context or payload limits. If instability occurs, set it to `false` to send history via explicit user prompt acknowledgment (`SendHistoryHandshakeAsync`).
- **Thinking with Flash Models:** Enabling high `ThinkingBudget` or `ThinkingLevel: HIGH` on "Flash" models (e.g., `gemini-3.5-flash`) while processing massive video files is highly unstable and frequently causes 500 errors. **Fix:** Either disable "Thinking" for flash models or switch to a "Pro" model (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), which handles reasoning natively.
- **Corrupted Video Chunks:** Occasionally, FFmpeg generates a chunk with a corrupted frame header that crashes the Gemini Vision encoder. **Fix:** Delete the video's `tmp` folder to force FFmpeg to recut the video.

---

## 8. Error Handling & ApiRetryPolicy

The `ApiRetryPolicy` class wraps all calls to the Google API and handles transient errors gracefully:
- **Rate Limits (HTTP 429):** If the API returns a `retryDelay`, the application parses it and waits exactly that long plus a 20-second buffer.
- **High Demand (HTTP 503):** If the server is overloaded ("high demand"), the application initiates a hard 3-minute backoff.
- **Linear Backoff:** For general 500 errors, the application uses a linear backoff (e.g., 45s, 75s, 105s) up to 8 times.
- **Interactive Skip:** During any waiting period, the user can press `Enter` to force an immediate retry, or `Ctrl+C` to cancel the delay. Canceling the delay aborts the current video chunk and safely moves the batch processor to the next file without crashing the application.

<br>

---

## 🇩🇪 Deutsch

Dieses Dokument bietet einen tiefen Einblick in die Architektur, Konfigurations-Besonderheiten und API-Einschränkungen der AI Lecture Extraction & Processing Pipeline. Es richtet sich an Entwickler und fortgeschrittene Benutzer, die die inneren Abläufe des Systems verstehen möchten.

---

## Codebasis-Philosophie: Dual-Commenting Paradigma

Die gesamte Codebasis folgt strikt einem Dual-Commenting-Paradigma, um die nahtlose Zusammenarbeit zwischen Mensch und KI zu gewährleisten:
- **`[AI Context]` (Englisch):** Richtet sich an LLMs. Erklärt das *Warum* - einschließlich Architekturentscheidungen, Prompt-Einschränkungen, API-Mechaniken und Strategien zur Token-Einsparung.
- **`[Human]` (Deutsch):** Richtet sich an menschliche Entwickler. Erklärt das *Wie* - fokussiert auf Geschäftslogik, grundlegenden Ablauf und UI-Anweisungen.
Bei Änderungen am Code müssen Entwickler und KI-Agenten diese Trennung strikt beibehalten.

---

## 1. Konfigurations-Hierarchie & das "Array Merge" Problem

Die Anwendung nutzt den `Microsoft.Extensions.Configuration` Binder, um Einstellungen aus mehreren Quellen zu laden. Die Hierarchie ist wie folgt (die zuletzt geladene überschreibt vorherige):
1. `AppConfig.cs` (Fest codierte C#-Standardwerte)
2. `appsettings.json` (Globale Überschreibungen)
3. Spezifische Session-Configs (z. B. `VertexAutoExtractionConfig.json`)

### Die Array-Merging Falle
Ein kritisches Verhalten des .NET Configuration Binders ist der Umgang mit Arrays (wie `HistoryPreloadPaths` oder `SystemInstructionPaths`).
Wenn die Basiskonfiguration (`appsettings.json`) ein Array mit 2 Elementen definiert und die spezifische Session-Config ein Array mit 1 Element, **ersetzt der Binder das Array nicht; er führt sie nach Index zusammen**.
- Index 0 aus der Basis-Config wird durch Index 0 aus der Session-Config überschrieben.
- Index 1 aus der Basis-Config bleibt erhalten und wird in das endgültige Array geladen.

**Lösung:** Um zu verhindern, dass unerwünschte Dateien geladen werden, stellen wir sicher, dass Basis-Arrays in `AppConfig.cs` und `appsettings.json` strikt leer sind (`[]`). Du musst alle erforderlichen Pfade explizit in deiner spezifischen Session-`.json`-Datei definieren.

---

## 2. Gemini API: Modelle & Tokenisierung

### Tokenisierung von PDFs und Bildern
Wenn ein PDF (z. B. ein akademisches Skript) an den Kontext angehängt wird, behandelt Gemini **jede Seite als Bild**. Die Token-Kosten können je nach Modellversion drastisch variieren:
- **gemini-3.5-flash / gemini-1.5-flash:** Hochgradig auf Effizienz optimiert. Eine einzelne PDF-Seite kostet möglicherweise nur einen Basis-Token-Betrag (z. B. ~258 Token).
- **gemini-2.5-pro:** Ausgerichtet auf tiefes logisches Denken und hohe Genauigkeit (insbesondere bei mathematischen Formeln). Es verarbeitet Bilder und PDF-Seiten oft in viel höherer Auflösung und zerlegt eine einzelne Seite in mehrere Kacheln (z. B. 2 bis 4 Kacheln). Folglich kann eine einzelne Seite 516 bis 1000+ Token kosten.
*Hinweis: Aus diesem Grund kann der Upload eines 400-seitigen PDFs bei Flash ~114k Token kosten, bei Pro jedoch ~228k Token.*

---

## 3. Erweitertes logisches Denken (Advanced Reasoning): Thinking Budgets & Levels

Googles Gemini-Modelle unterstützen ein erweitertes internes Nachdenken ("Thinking"), bevor sie eine Ausgabe generieren. Dies wird strikt über das `ThinkingConfig`-Objekt im API-Payload gesteuert.

### Thinking Budget
Das `ThinkingBudget` bestimmt die maximale Anzahl von Ausgabe-Token, die das Modell intern zum "Nachdenken" ausgeben darf, bevor die endgültige Antwort generiert wird.
- **Vertex AI Einschränkungen:** Stand Mitte 2026 erzwingt der Vertex AI-Endpunkt ein striktes Integer-Limit für das Thinking Budget. Unterstützte Werte liegen zwischen **128 und 32768 Token**.
- **Code-Implementierung:** Die Anwendung fängt jedes Budget über 32768 ab und klemmt es strikt auf `32768` fest, kurz bevor die Anfrage an Vertex AI gesendet wird, um `ClientError: thinking_budget is out of range` Ausnahmen zu vermeiden.

### Thinking Level
Das `ThinkingLevel` ist ein `enum` (`MINIMAL`, `LOW`, `MEDIUM`, `HIGH`), das die Tiefe des Nachdenkens vorschreibt.
- **Modell-Kompatibilität:** `ThinkingLevel` wird von den neueren **Gemini 3.x** Frontier-Modellen voll unterstützt. Modelle wie **Gemini 2.5 Pro** unterstützen den Parameter `thinkingLevel` jedoch nicht und lehnen die Anfrage ab, wenn er enthalten ist.
- **Code-Implementierung:** Der `requestConfig` Builder prüft den Modellnamen (`_config.Model`). Wenn das Modell eine `gemini-3`-Variante ist, werden sowohl `ThinkingLevel` als auch `ThinkingBudget` angewendet. Wenn das Modell eine `gemini-2.5`-Variante ist, entfernt die Anwendung stillschweigend das `ThinkingLevel` und verlässt sich vollständig auf das `ThinkingBudget`, um `thinking_level is not supported` Fehler zu vermeiden.

---

## 4. Google AI Studio vs. Google Cloud Vertex AI

Die Pipeline unterstützt beide Umgebungen, aber sie behandeln Payloads intern sehr unterschiedlich:

### Google AI Studio (Developer Tier)
- Nutzt die **Google File API**.
- Bei der Verarbeitung von Videodateien oder PDFs werden Dateien direkt in den generativen AI-Dateispeicher hochgeladen.
- Ratenlimits (RPM/TPM) sind für Free-Tier-Nutzer in der Regel strenger, aber es eignet sich hervorragend für schnelles Prototyping.

### Google Cloud Vertex AI (Enterprise Tier)
- Nutzt **Google Cloud Storage (GCS) Buckets**.
- Die Anwendung lädt lokale Dateien automatisch in deinen angegebenen `GcsBucketName` hoch und hängt die `gs://...` URI an den Gemini-Prompt an.
- **Speicherkosten-Management:** Da die Verarbeitung von hunderten überlappenden Video-Chunks die Speicherkosten in die Höhe treiben kann, nutzt die Anwendung eine `GcsWorkspace.PurgeAsync()`-Routine. Nachdem ein Chunk erfolgreich verarbeitet wurde (oder wenn eine Ausnahme auftritt), löscht die Anwendung die temporären Dateien aggressiv aus dem GCS-Bucket.
*Hinweis: Nicht gelöschte Dateien in einem Bucket verbrauchen bei zukünftigen Anfragen keine Prompt-Token, da die API nur die exakten `gs://`-URIs verarbeitet, die im jeweiligen Request-Payload gesendet werden.*

---

## 4.2 Modulares System-Prompt & Attention Map Priming

### Modulare Dateiinjektion
Statt monolithische Markdown-Blöcke zu nutzen, löst die Pipeline über `SystemInstructionPaths` mehrere getrennte Anweisungsdateien (`transcription.md`, `hard-specs.md`, `environments.md` etc.) auf. Vor der Injektion jeder Datei in den Prompt wird sie mit klaren Trenn-Headern versehen (`******\n------\n******\nHere is the file 'dateiname.md':\n`). Diese explizite Trennung verhindert Aufmerksamkeitssprünge (Attention Bleeding) der KI zwischen verschiedenen Domänen.

### Attention Map Priming
Um die Orientierung und Einhaltung logischer Hierarchien zu maximieren, erzeugt die Anwendung eine ASCII-Ordnerstruktur der geladenen Systemanweisungen und setzt diese an den absoluten Anfang des System-Prompts. Indem Gemini diese strukturelle "Landkarte" als Erstes sieht, wird der interne Attention-Mechanismus auf Orientierung und logische Kategorisierung getrimmt, bevor spezifische Syntaxregeln verarbeitet werden.

### Strikte Multipart-Trennung (Vertex AI)
Vertex AI erzwingt strikte Schemareinheit im `SystemInstruction`-Feld: Es akzeptiert ausschließlich reine `text`-Parts. Werden binäre Daten (wie Bildanhänge aus der `Training History`) in `SystemInstruction` übergeben, lehnt Vertex AI die Anfrage mit einem `ClientError` ab. Die Pipeline fängt binäre History-Anhänge ab und leitet sie dynamisch in den `contents`-Körper des User-Prompts weiter, was die Kompatibilität zwischen AI Studio und Vertex garantiert.

### Prompt Assembly & Dump Logging
Um den exakten Prompt-Aufbau zu verifizieren und sicherzustellen, dass Anhänge der `Training History` miterfasst werden, wird `ExtractionHelpers.LogSystemInstructionDumpAsync` erst ausgeführt, nachdem alle Texte und Bildanhänge verknüpft wurden. Der komplette Dump wird in eine mit Zeitstempel versehene `system_instruction_dump.md` im Log-Verzeichnis geschrieben.

```
+---------------------------------------------------------------------------------+
|                         PROMPT PAYLOAD ASSEMBLY MATRIX                          |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  [SystemInstruction Payload (Strikt reiner Text)]                               |
|  ├── 1. Attention Map Priming (ASCII-Ordnerbaum der Instruktionshierarchie)     |
|  ├── 2. transcription.md (Eingefasst mit Trenn-Headern)                         |
|  ├── 3. hard-specs.md (Eingefasst mit Trenn-Headern)                            |
|  └── 4. environments.md (Eingefasst mit Trenn-Headern)                          |
|                                                                                 |
|  [User Content Payload (Multimodal)]                                            |
|  ├── Binäre Anhänge (z. B. Training History Bilder / Handschrift-Proben)        |
|  └── Videosegment-Chunk (gs://... oder File API URI)                            |
+---------------------------------------------------------------------------------+
```

---


## 4.3 Intelligentes Context Caching & MD5 Auto-Reload

Um die Verarbeitungszeiten zu minimieren und redundante Upload-Kosten beim sequenziellen Batch-Verarbeiten von Vorlesungsvideos zu eliminieren, implementiert `ContextCacheStateManager` automatisiertes Remote Caching für AI Studio und Vertex AI.

### MD5-Checksummen-Invalidierung
Wann immer eine Auto-Extraktions-Session startet, berechnet die Anwendung eine MD5-Checksumme über den gesamten Text aller konfigurierten Systemanweisungen und vorverladenen History-Anhänge.
- **Cache Match:** Existiert ein gültiger Remote-Cache (`vertex_cache_state.json` / `aistudio_cache_state.json`), ist er auf den Google-Servern nicht abgelaufen und stimmt seine MD5-Checksumme mit der frisch berechneten Checksumme überein, nutzt die App diesen Cache sofort wieder (`cachedContent/...`).
- **Cache Mismatch (Auto-Reload):** Ändert ein Entwickler auch nur ein einziges Wort in einer Markdown-Datei (z. B. in `hard-specs.md`), weicht die neue MD5-Checksumme vom gespeicherten Zustand ab. Die Anwendung erkennt diese Abweichung, sendet einen `Delete`-Befehl für den alten Google Cloud Cache und erstellt vollautomatisch einen frischen Cache für den nächsten Video-Batch.

```
+---------------------------------------------------------------------------------+
|                         REMOTE CONTEXT CACHE LEBENSZYKLUS                       |
+---------------------------------------------------------------------------------+
|  Start Auto-Extraction Session                                                  |
|         │                                                                       |
|         ▼                                                                       |
|  Berechnung lokaler MD5-Hash (Alle System-Instruktionen + Vorverladene History) |
|         │                                                                       |
|         ├─────────────────────────────────────────┐                             |
|         ▼                                         ▼                             |
|  [Cache-Datei existiert & aktiv?]          [Kein Cache / Abgelaufen / Mismatch] |
|         │                                         │                             |
|  (MD5 stimmt exakt überein)                       ▼                             |
|         │                                  Löschen alter Remote Cache           |
|         ▼                                         │                             |
|  ✅ Wiederverwendung cachedContent/URI            ▼                             |
|     (0 Prompt Upload Kosten)               Upload neuer Kontext & MD5 speichern |
+---------------------------------------------------------------------------------+
```

---


## 4.4 Impliziter Prefix-Cache Warm-Up (`PrimePrefixCacheAsync`)

Google AI Studio nutzt einen **serverseitigen impliziten Prefix-Cache**: Wenn aufeinanderfolgende API-Anfragen denselben Präfix teilen (System Instruction + früher User-Turn-Text), verarbeitet Google die tokenisierte Darstellung nicht neu. Das spart bei jedem Video-Part erheblich Prompt-Verarbeitungskosten und Latenz.

### Wie der Warm-Up funktioniert
Bevor das erste Video hochgeladen wird, sendet `AiStudioAutoExtractionSession.WarmUpWithBatchedHistoryAsync` einen oder mehrere leichte "Handshake"-Requests, die nur die System Instruction (und ggf. History-Batches) enthalten. Das zwingt Google, den vollständigen Instruktionssatz zu tokenisieren und in den impliziten Cache zu legen, während FFmpeg das Video noch schneidet.

### Batch-Handshake-Strategie
Wenn die History auf mehrere Batches aufgeteilt ist (`HistoryBatchCount > 1`), sendet der Warm-Up pro Batch einen Handshake, der die System Instruction jeweils um den nächsten Batch erweitert:
- **Intermediäre Batches** haben eine unvollständige System Instruction → sie können keinen Cache-Hit für Part 1 produzieren (anderer Präfix). Diese Handshakes trainieren jedoch trotzdem die Google-Tokenisierungs-Pipeline für diese Teilzustände.
- **Letzter Batch** hat die vollständige System Instruction, identisch zu Part 1 → nur dieser Handshake kann einen echten Cache-Hit erzeugen.

### Dummy-Datei (`dummy-part0.tex`) & `PrefixCacheAnchor`
Part 1 enthält immer einen `<reference_context file="part0.tex">`-Block (ein synthetischer LaTeX-Stub) im User-Turn. Damit der Warm-Up-Präfix bit-identisch zu Part 1 ist, enthält der letzte Handshake denselben Block. `PrefixCacheAnchor.LoadPrefixCacheAnchorText()` lädt `dummy-part0.tex` einmalig vom Disk und hält ihn im Speicher für alle weiteren Aufrufe.

**`SendDummyFileWithEachWarmUpRound`** (Config-Flag): Bei `true` wird der Dummy-Block mit *jedem* Handshake gesendet (nicht nur dem letzten). Mit `InlinePrecedingLecTexParts: false` ist der User-Turn-Text für alle Parts identisch strukturiert, daher maximiert dieses Flag die Cache-Hit-Wahrscheinlichkeit auch für intermediäre Batches. Default: `false` (Token-Sparmodus — nur der letzte Batch erhält den Dummy).

### ThinkingConfig-Konsistenz
Die `GenerateContentConfig` im Warm-Up verwendet dieselbe **`ThinkingConfig`** wie die echte Part-1-Anfrage (gleiches `ThinkingBudget` / `ThinkingLevel` / modellspezifisches Routing). Ein Unterschied würde einen anderen Cache-Bucket-Key erzeugen und einen Cache-Miss verursachen. Mit `DisableThinkingDuringWarmUp: true` wird für den Handshake `ThinkingBudget = 0` erzwungen (spart Denk-Tokens im Warm-Up, auf Kosten eines garantierten Cache-Misses für diese Runde).

---


## 4.5 Authentifizierungs-Setup

Je nachdem, welche Umgebung du anvisierst, erfordert die Anwendung unterschiedliche Authentifizierungs-Setups.

### Google AI Studio (API Keys)
Die Anwendung löst API-Keys dynamisch aus Windows-Umgebungsvariablen auf. Um deine Keys dauerhaft zu setzen, führe die folgenden Befehle in PowerShell oder der Eingabeaufforderung aus:

```powershell
# Setze deinen primären API-Key für die allgemeinen Sessions
setx API_KEY-ai-studio-test-project-1 "DEIN_GEMINI_API_KEY"

# Setze einen dedizierten API-Key speziell für die LatexRefinementSession
setx API_KEY_LatexRefinement "DEIN_GEMINI_API_KEY"
```
*(Hinweis: Du musst dein Terminal und geöffnete IDEs neu starten, damit `setx`-Änderungen wirksam werden.)*

### Google Cloud Vertex AI (IAM Authentifizierung)
Vertex AI verwendet Google Cloud IAM (Identity and Access Management) anstelle von statischen API-Keys. Du musst das Google Cloud SDK (`gcloud` CLI) installiert haben.

1. **Login und Standard-Anmeldeinformationen der Anwendung setzen:**
   ```powershell
   gcloud auth application-default login
   ```
2. **Dein aktives Projekt setzen** (Muss mit der `VertexProjectId` in deiner Config übereinstimmen):
   ```powershell
   gcloud config set project deine-vertex-projekt-id
   ```

## 4.6 Programm ausführen (Lokale Konsole & Parallele Ausführung)

Du kannst das Programm direkt in der Windows Eingabeaufforderung (`cmd`) oder der PowerShell starten, während du gleichzeitig in Antigravity oder Visual Studio Code daran arbeitest.

### Navigieren und Starten des Programms
1. **Terminal öffnen:** Öffne die Windows Eingabeaufforderung (`cmd`) oder die PowerShell.
2. **In das Projektverzeichnis wechseln:**
   Führe den folgenden Befehl aus:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
3. **Kompilieren und Starten:**
   Um das Projekt zu bauen und direkt auszuführen, verwende:
   ```powershell
   dotnet run
   ```
   *Hinweis: Wenn das Programm bereits läuft oder die Executable blockiert ist, kann der Kopiervorgang fehlschlagen. Verwende in diesem Fall die unten stehende Methode.*

### Ausführen mehrerer Instanzen (Parallel)

> **Veraltet.** Das Kopieren des Build-Ordners ist nicht mehr nötig: im CLI-Modus wird die
> Konfiguration standardmäßig nicht zurückgeschrieben, und `--config-dir` isoliert sie bei
> Bedarf pro Prozess. Siehe **[docs/cli/parallel.md](docs/cli/parallel.md)** und
> `lecx batch`. Der folgende Abschnitt bleibt für den rein interaktiven Betrieb gültig.

Du kannst mehrere Instanzen parallel laufen lassen, um verschiedene Videos zeitgleich zu verarbeiten:
1. **Projekt einmalig bauen:**
   ```powershell
   dotnet build
   ```
2. **Mehrere Instanzen per Direktstart ausführen:**
   Öffne separate Terminal-Fenster, navigiere in das Projekt-Hauptverzeichnis:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
   Und starte direkt die ausführbare Datei über ihren relativen Pfad:
   ```powershell
   .\bin\Debug\net10.0\lec-extraction-prog.exe
   ```
   Der direkte Start der `.exe` verhindert Build-Konflikte bei gleichzeitigem Start.
3. **Konfigurationen isolieren (Empfohlen):**
   Damit jede Instanz ihre eigenen dauerhaften Einstellungen verwenden kann:
   - Kopiere den Ordner `bin/Debug/net10.0/` in separate Verzeichnisse (z. B. `bin/Debug/instance1/` und `bin/Debug/instance2/`).
   - Navigiere in deinen Terminal-Fenstern direkt in diese Unterordner:
     - Fenster 1:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance1"
       .\lec-extraction-prog.exe
       ```
     - Fenster 2:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance2"
       .\lec-extraction-prog.exe
       ```
     Jede Instanz lädt und speichert ihre Konfiguration nun absolut unabhängig in ihrem jeweiligen Ordner.

---

## 5. Pipeline-Mechanik: FFmpeg & Überlappende Segmente

Um eine 90-minütige Vorlesung zu transkribieren, führt das Senden des gesamten Videos auf einmal oft zur Erschöpfung des Kontextfensters oder zu übersprungenen Details. Die Pipeline löst dies durch:

1. **FFmpeg Slicing:** Das Video wird in streng definierte Chunks (z. B. jeweils 3 Minuten) mit einer Überlappung (z. B. 180 Sekunden, d.h. die letzten X Sekunden von Chunk 1 sind die ersten X Sekunden von Chunk 2) zerschnitten.
2. **Token-Optimierung:** Die Video-Framerate wird auf `1 FPS` dezimiert. Da sich Tafeln und Folien selten im Sub-Sekunden-Bereich ändern, bleibt die visuelle Information erhalten, während der Token-Payload drastisch reduziert wird.
3. **Audio-Downmixing:** Audio wird komprimiert und auf Mono heruntergemischt (`-ac 1`).
4. **Latex Refinement:** Aufgrund der Überlappungen enthalten die extrahierten LaTeX-Chunks duplizierte Sätze oder Formeln an den Rändern. Die `LatexRefinementSession` übergibt diese Chunks an eine deterministische KI (Temperature 0.35) mit der Anweisung, sie nahtlos zu einem einzigen, kontinuierlichen LaTeX-Dokument zusammenzuführen.

```
+---------------------------------------------------------------------------------+
|                       SLIDING WINDOW CHUNK SCRIPT MERGER                        |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  Vorlesungs-Zeitachse: 0m ---------- 3m ---------- 6m ---------- 9m             |
|                                                                                 |
|  Chunk 1 [0m - 3m]:      [======= Transkribierter Text A =======]               |
|                                       ▲                                         |
|                                  Überlappungszone (z.B. 180s)                   |
|                                       ▼                                         |
|  Chunk 2 [1.5m - 4.5m]:               [======= Transkribierter Text B =======]  |
|                                                                                 |
|  LatexRefinementSession (Temp = 0.0):                                           |
|  Verschmilzt Formel-Duplikate -> Gibt nahtlosen LaTeX-Stream aus                |
+---------------------------------------------------------------------------------+
```

### 🎬 FFmpeg Interactive Manager Dashboard
Zusätzlich zur automatisierten Vorverarbeitung bietet Option **3** im Hauptmenü das **FFmpeg Interactive Preprocessor Dashboard**. Es bietet volle interaktive Kontrolle über die manuelle Vorverarbeitung von Videos:
- **Interaktiver Ordner-Browser:** Navigiere durch Unterordner, übergeordnete Ordner (`..`) und wechsle Windows-Laufwerke (z. B. `d:`, `c:`), um Dateien zu finden.
- **Konvertierungs-Dashboard:** Zeigt die ausgewählten Dateien und die aktiven Einstellungen auf einen Blick.
- **Flexible Anpassung aller Parameter:**
  - **Geschwindigkeit (Speed):** Setze die Geschwindigkeit des Videos zwischen `0.1x` und `10.0x`.
  - **Bilder pro Sekunde (FPS):** Bestimme eine benutzerdefinierte Framerate.
  - **Audio-Downmix:** Wähle zwischen Mono-Downmix (optimal für KI-Transkription) oder Beibehalten der originalen Stereo-Spur.
  - **Auflösung (Skalierung):** Schalte zwischen standardmäßigem `720p` (empfohlen) oder der Originalauflösung um.
  - **Kompression (Preset):** Wähle FFmpeg x264 Presets (von `ultrafast` bis `veryslow`), um Konvertierungsgeschwindigkeit gegen Dateigröße abzuwägen.
  - **Zeitbereichs-Beschnitt (Time Range):** Schneide einen bestimmten Video-Ausschnitt aus (Startzeit und Dauer eingeben), anstatt das gesamte Video zu konvertieren.
  - **Splitting / Teilen:** Teile das Video in mehrere Stücke auf und bestimme eine eigene Überlappungszeit.
- **Custom Mode:** Ermöglicht die freie und direkte Eingabe von FFmpeg-Befehlen.

---


## 6. Architektur-Übersicht: Namespaces & Kernklassen

Der gesamte Code liegt unter `src/`, ein Ordner pro Bounded Context, mit dem Namespace `LectureExtraction.<Ordner>`. Im Folgenden findest du eine umfassende Übersicht der Namespaces und ihrer wichtigsten Klassen.

### ⚙️ `LectureExtraction.Extraction`
Verantwortlich für die vollautomatisierte Batch-Verarbeitungs-Pipeline. Orchestriert FFmpeg-Verarbeitung und KI-Inferenz sequenziell oder nebenläufig.
- **`AiStudioAutoExtractionSession`**: Verwaltet die Extraktions-Pipeline über die Google AI Studio File API. Nutzt ein **Producer-Consumer-Muster** (via `System.Threading.Channels`), was es FFmpeg ermöglicht, den nächsten Chunk zu verarbeiten, während Gemini den aktuellen analysiert. Aufgeteilt auf drei Dateien: die Kern-Pipeline (`.cs`), die interaktive REPL/den Debug-Chat (`.Repl.cs`) und den impliziten Prefix-Cache-Warmup (`.PrefixCache.cs`).
- **`VertexAutoExtractionSession`**: Das Enterprise-Äquivalent. Lädt Dateien in den Google Cloud Storage (GCS) hoch und löscht sie nach Abschluss der Inferenz strikt (`GcsWorkspace`), um explodierende Speicherkosten zu verhindern. Trägt ebenfalls einen portierten Prefix-Cache-Warmup in einer eigenen `.PrefixCache.cs`, gesteuert über `EnableImplicitPrefixCacheWarmup` (standardmäßig aus — anders als bei AI Studio ist dieser Pfad noch nicht gegen die echte API verifiziert).
- **`VideoSegmentProducer`**: Die gemeinsame FFmpeg-Producer-Hälfte der Pipeline (Splitting, Resume-from-Disk-Caching), genutzt von beiden Sessions.
- **`TexDocumentWriter`**: Baut die `.tex`-Teil-/Gesamtdatei-Header, gemeinsam genutzt von beiden Sessions.
- **`ExtractionHelpers`**: Was übrig blieb, nachdem die untenstehenden Teile herausgelöst wurden — `ResolveNonClashingTexPath` und `LogSystemInstructionDumpAsync`.
- **`HistoryFileResolver`, `FileTreeRenderer`, `VideoBatchSelector`, `YouTubeTaskPrompt`, `ModelSyncService`**: Fokussierte Einzelteile, herausgelöst aus dem ehemaligen `ExtractionHelpers`-Sammelsurium.
- **`RefinementUiHelper`**: Konsolen-UI zum Starten und Konfigurieren des LaTeX-Refinement-Schritts aus beiden Extraktions-Sessions heraus.
- **`AudioTrackExtractor`**: Kapselt den Hintergrund-Task zur AAC-Extraktion, der als Audio-Input für das LaTeX-Refinement dient.

### 💬 `LectureExtraction.Chat` & `LectureExtraction.Refinement`
Verarbeitet die interaktive REPL (Read-Eval-Print Loop) für manuelles Debugging sowie deterministisches Post-Processing, das sich wie eine Chat-Session verhält.
- **`LatexRefinementSession`** (`Refinement`): Die Post-Processing-Engine. Füttert die überlappenden `.tex`-Chunks, die von der Extraktion generiert wurden, in Gemini ein und weist es an, Duplikate aufzulösen und sie zu einem kontinuierlichen Dokument zusammenzuführen.
- **`DirectAiChatSessionVertex` / `DirectAiChatSessionAiStudio`** (`Chat`): Die primären REPL-Klassen. Verarbeiten Benutzereingaben im Terminal, erlauben dynamische Parameteränderungen (z.B. `/set temp 0.5`), verarbeiten `/attach`-Befehle und pflegen den Gesprächsverlauf.

### 🔧 `LectureExtraction.Configuration`
Zentralisiertes Konfigurations-Management.
- **`ConfigLoader<T>`**: Ein generischer Loader, der den `Microsoft.Extensions.Configuration` Binder implementiert. Führt Standards aus `AppConfig.cs`, globale Überschreibungen aus `appsettings.json` und spezifische Einstellungen aus `{Session}Config.json` zusammen.
- **`AppConfig`**: Die einzige Quelle der Wahrheit (Single Source of Truth) für globale, fest codierte Fallback-Parameter (z. B. Fallback-Pfade, `DefaultThinkingBudget`, `VertexProjectId`) sowie Feature-Flags (`IsVertexAiEnabled`, aus `appsettings.json` gelesen — ersetzt das alte statische Feld `Program.Activate_Vertex`, das einen Neu-Kompilierungslauf zum Ändern erforderte).

### 🎬 `LectureExtraction.Media`
Wickelt die lokale `ffmpeg.exe` Binary ab, um Medien vorzuverarbeiten, bevor sie in die Cloud gesendet werden.
- **`FfmpegToolkit`**: Ein Headless-Befehls-Builder. Enthält Logik wie `ProcessSplitVideoAsync` (Zerschneiden von Videos in 3-Minuten-Chunks mit 3 Minuten Überlappung), Dezimieren der Framerate auf 1 FPS (`-vf fps=1`) und Heruntermischen von Audio auf Mono (`-ac 1`).
- **`FfmpegInteractiveSession`**: Eine konsolenbasierte Benutzeroberfläche für Benutzer, um FFmpeg-Kompressionen manuell auszulösen, ohne die KI-Pipeline auszuführen.
- **`VideoDateParser`**: Hilfsprogramm, um Datums-Metadaten aus Dateinamen von Vorlesungsvideos zu extrahieren.

### 🖥️ `LectureExtraction.ConsoleUi`
Generische interaktive Konsolen-Prompts, unabhängig von einem konkreten Sitzungstyp.
- **`ConfigurationPrompts`**: Bestätigen-oder-Ändern-Prompts für Quellordner (`PromptForSourceFolder`), Modell und API-Key-Profil.
- **`DirectoryTreeRenderer`, `FileSelectionPrompt`**: Helfer für Ordner-Browsing und Dateibaum-Darstellung.

### 🏗️ `LectureExtraction.Infrastructure`
Querschnittsbelange wie Session-Logging und String-Hilfsfunktionen.
- **`SessionLogger`**: Erstellt automatisch Ordner mit Zeitstempeln für jede Session (z.B. `folder-X-date`). Legt rohe LaTeX-Ausgaben in Dateien ab und pflegt ein Markdown-Log (`chat_log.md`) der gesamten Interaktion.
- **`StringExtensions`**: Allgemeine String-Hilfsfunktionen (vormals `StringHelper`).

### 🌐 `LectureExtraction.GoogleAi`
- **`GoogleAiClientBuilder`**: Kapselt die rohe HTTP `HttpClient`-Erstellung sowie die API-Key-/Credential-Auflösung für AI Studio und Vertex.
- **`AttachmentUploader`**: Ein hochentwickeltes Dateisuchsystem (vormals `AttachmentHandler`). Wenn ein Benutzer `/attach file.pdf` eingibt, durchsucht diese Klasse eine Liste von Fallback-Ordnern, findet die Datei und bereitet sie für den Upload vor.
- **`ApiRetryPolicy`**: Enthält Logik, um temporäre HTTP-Fehler anmutig zu behandeln, wie z.B. Ratenlimits (HTTP 429) oder temporäre Serverfehler (HTTP 503). Vormals `ApiResilience`.
- **`ApiKeyProfileResolver`**: Löst ein API-Key-Profil (0-3) zum Namen der zugehörigen Umgebungsvariable auf.
- **`PrefixCacheAnchor`**: Lädt und cached `dummy-part0.tex`, den gemeinsamen impliziten-Prefix-Cache-Anker, genutzt von beiden Extraktions-Sessions.
- **`GcsWorkspace`**: Die gemeinsame Bereinigungsroutine für den Vertex-GCS-Bucket.
- **`ModelCapabilities`**: Der einzige `SupportsThinking`-Check, gemeinsam genutzt von allen Sitzungstypen.

### 📄 `LectureExtraction.Latex`
- **`LatexToolkit`**: Hilfsmethoden, um rohe Markdown-Ausgaben von Gemini zu parsen, ` ```latex ` Codeblöcke zu entfernen und die resultierenden Strings zu validieren.
- **`LatexTimestampAdjuster`**: Werkzeuge zur Manipulation und Anpassung eingebetteter Zeitstempel im LaTeX-Dokument. Vormals `LatexTimestampHelper`.
- **`LatexResponseCleaner`**: Entfernt Modell-Geplauder/Formatierungsartefakte aus einer rohen Gemini-Antwort, bevor sie auf die Festplatte geschrieben wird.

### 🚪 `LectureExtraction.App`
- **`Program`**: Der Einstiegspunkt — nur `Main()` und die oberste Fehlerbehandlung.
- **`MainMenu`**: Die oberste interaktive Schleife.
- **`SessionFactory`**: Config-Laden → Client-Bauen → Session-Erstellen für jeden der fünf Sitzungstypen.
- **`SourceFolderMenu` / `ApiKeyProfileMenu`**: Die beiden Konfigurations-Untermenüs.

---

## 7. Bekannte API-Einschränkungen & Fehlerbehebung

### Cache Priming vs. Roundtrips (Warum nicht alles auf einmal senden?)
Um einen API-Roundtrip zu sparen, könnte man versucht sein, die `training-history` und das 30-minütige Vorlesungsvideo in einem einzigen Prompt zu senden. Dies ist jedoch höchst kontraproduktiv:
1. **Cache Priming & Fokus:** Indem man zuerst die massive Historie sendet und eine "Bestätigung" verlangt, wird das Modell gezwungen, zu parsen und einen internen Kontext-Cache aufzubauen, der rein für die Regeln zuständig ist. Wenn das eigentliche Video im nächsten Prompt eintrifft, konzentriert sich das Modell zu 100 % auf die Ausführung, anstatt seine Aufmerksamkeit zu teilen.
2. **Kontext-Überlastung:** Tausende Zeilen Historie und ein großes Video in einen einzigen Prompt zu packen, überfordert das Modell oft, was zu halluzinierten Ausgaben oder `500 Internal Server Errors` führt.

### HTTP 500 Internal Error (Google Server Absturz)
Wenn die Anwendung in einer Retry-Schleife mit einem `Internal error encountered (HTTP 500)` feststeckt, bedeutet dies, dass das Google Backend beim Verarbeiten der Anfrage abgestürzt ist. Dies ist selten ein Netzwerkproblem, sondern meist ein Prompt-/Payload-Problem. Häufige Ursachen sind:
- **Überladene System Instruction:** Wenn `LoadHistoryIntoSystemInstruction` auf `true` gesetzt ist, injiziert die Pipeline die Historie in die `SystemInstruction` (mittels XML-Framing `<file path="...">` und direktem Inlining von Bildern als `InlineData`). Hinweis: Obwohl Inline-Bytes Probleme mit externen Datei-URIs umgehen, können extrem große Historien weiterhin Nutzlastgrenzen überschreiten. Bei Instabilitäten setze es auf `false`, um die Historie über einen separaten User-Prompt (`SendHistoryHandshakeAsync`) zu senden.
- **Thinking bei Flash-Modellen:** Die Aktivierung eines hohen `ThinkingBudget` oder `ThinkingLevel: HIGH` bei "Flash"-Modellen (z. B. `gemini-3.5-flash`) während der Verarbeitung massiver Videodateien ist sehr instabil und verursacht häufig 500er Fehler. **Lösung:** Deaktiviere "Thinking" für Flash-Modelle oder wechsle zu einem "Pro"-Modell (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), welches Logik nativ verarbeitet.
- **Beschädigte Video-Chunks:** Gelegentlich generiert FFmpeg einen Chunk mit einem beschädigten Frame-Header, der den Gemini Vision Encoder zum Absturz bringt. **Lösung:** Lösche den `tmp`-Ordner des Videos, um FFmpeg zu zwingen, das Video neu zu schneiden.

---

## 8. Fehlerbehandlung & ApiRetryPolicy

Die Klasse `ApiRetryPolicy` kapselt alle Aufrufe an die Google API und behandelt transiente Fehler anmutig:
- **Ratenlimits (HTTP 429):** Wenn die API ein `retryDelay` zurückgibt, parst die Anwendung dies und wartet genau so lange plus einen Puffer von 20 Sekunden.
- **Hohe Auslastung (HTTP 503):** Wenn der Server überlastet ist ("high demand"), initiiert die Anwendung einen harten 3-Minuten-Backoff.
- **Linearer Backoff:** Für allgemeine 500er Fehler verwendet die Anwendung einen linearen Backoff (z. B. 45s, 75s, 105s) bis zu 8 Mal.
- **Interaktiver Skip:** Während jeder Wartezeit kann der Benutzer `Enter` drücken, um einen sofortigen Retry zu erzwingen, oder `Ctrl+C`, um die Verzögerung abzubrechen. Das Abbrechen der Verzögerung bricht den aktuellen Video-Chunk ab und bewegt den Batch-Prozessor sicher zur nächsten Datei, ohne dass die Anwendung abstürzt.

---

## 9. Offene Messungen (Einstellungen, die noch nicht empirisch belegt sind)

Zwei Schalter sind implementiert und getestet, aber ihr Default beruht auf
Überlegung, nicht auf Messung. Beide lassen sich nur in einem echten
(kostenpflichtigen) Extraktionslauf beantworten:

- **`DisableThinkingDuringWarmUp`** — Beim Warm-up-Handshake die `Denk-Tokens`
  in der Ausgabe ablesen und danach entscheiden, ob Thinking dort abgeschaltet
  gehört. *Aktueller Default: `false` (Thinking wie bei Part 1).*

- **`SendDummyFileWithEachWarmUpRound`** — Mit `InlinePrecedingLecTexParts: false`
  ist der User-Turn-Text aller Parts strukturell identisch, daher könnten auch
  intermediäre Batch-Handshakes von einem Cache-Slot profitieren. Ob der
  Mehrverbrauch an Tokens sich durch die höhere Cache-Trefferrate amortisiert,
  ist nicht gemessen. *Aktueller Default: `false` (Dummy nur beim letzten Batch).*

- **`InlinePrecedingLecTexParts`** — Ein kurzes Video je einmal mit `true`
  (Inline) und `false` (Upload) laufen lassen und den Default aus dem
  gemessenen Token-/Zeitverbrauch setzen.
  *Aktueller Config-Wert: `false` (Upload-Modus).*
