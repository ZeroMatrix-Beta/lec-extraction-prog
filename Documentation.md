# 📚 Detailed System Documentation

This document provides a deep dive into the architecture, configuration quirks, and API constraints of the AI Lecture Extraction & Processing Pipeline. It is intended for developers and advanced users who want to understand the inner workings of the system.

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
- **Storage Cost Management:** Because processing hundreds of overlapping video chunks can rack up storage costs, the application utilizes a `CleanupBucketAsync()` routine. After a chunk is successfully processed (or if an exception occurs), the application aggressively deletes the temporary files from the GCS bucket. 
*Note: Un-deleted files in a bucket do not consume prompt tokens in future requests, as the API only processes the exact `gs://` URIs sent in the specific request payload.*

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

---

## 5. Pipeline Mechanics: FFmpeg & Overlapping Segments

To transcribe a 90-minute lecture, sending the entire video at once often leads to context-window exhaustion or skipped details. The pipeline solves this via:

1. **FFmpeg Slicing:** The video is chopped into strictly defined chunks (e.g., 3 minutes each) with an overlap (e.g., 180 seconds, meaning the last X seconds of chunk 1 are the first X seconds of chunk 2).
2. **Token Optimization:** The video framerate is decimated to `1 FPS`. Since blackboards and slides rarely change sub-second, this preserves visual information while drastically reducing the token payload.
3. **Audio Downmixing:** Audio is compressed and downmixed to mono (`-ac 1`).
4. **Latex Refinement:** Because of the overlaps, the extracted LaTeX chunks will contain duplicated sentences or formulas at the boundaries. The `LatexRefinementSession` passes these chunks to a deterministic AI (Temperature 0.0) with instructions to seamlessly merge them into a single, continuous LaTeX document.

---

## 6. Architecture Breakdown: Namespaces & Core Classes

The project is heavily modularized to separate configuration, AI interactions, local video processing, and file infrastructure. Below is a comprehensive overview of the namespaces and their most critical classes.

### ⚙️ Namespace: `AutoExtraction`
Responsible for the fully automated batch-processing pipeline. It orchestrates FFmpeg processing and AI inference sequentially or concurrently.
- **`AiStudioAutoExtractionSession`**: Manages the extraction pipeline using the Google AI Studio File API. It uses a **Producer-Consumer pattern** (via `System.Threading.Channels`), allowing FFmpeg to process the next chunk while Gemini is analyzing the current one.
- **`VertexAutoExtractionSession`**: The Enterprise equivalent. It uploads files to Google Cloud Storage (GCS) and strictly deletes them after the inference is done (`CleanupBucketAsync`) to prevent storage cost blowouts.
- **`ExtractionHelpers`**: Utility methods specifically for the extraction loop (e.g., printing progress, calculating estimated time remaining).
- **`RefinementUiHelper`**: Console UI elements specific to visualizing extraction progress.
- **`VideoDateParser`**: Helper to extract date metadata from lecture video filenames.

### 💬 Namespace: `DirectChatAiInteraction`
Handles the interactive REPL (Read-Eval-Print Loop) for manual debugging, as well as deterministic Post-Processing that behaves like a chat session.
- **`LatexRefinementSession`**: The post-processing engine. It feeds the overlapping `.tex` chunks generated by `AutoExtraction` into Gemini, instructing it to resolve duplicates and merge them into one continuous document.
- **`DirectAiChatSessionVertex` / `DirectAiChatSessionAiStudio`**: The core REPL classes. They handle user terminal input, allow dynamic parameter changes (e.g. `/set temp 0.5`), process `/attach` commands, and maintain conversation history.

### 🔧 Namespace: `Config`
Centralized configuration management.
- **`ConfigLoader<T>`**: A generic loader implementing the `Microsoft.Extensions.Configuration` binder. It merges defaults from `AppConfig.cs`, global overrides from `appsettings.json`, and specific settings from `{Session}Config.json`.
- **`AppConfig`**: The single source of truth for global, hardcoded fallback parameters (e.g., fallback paths, `DefaultThinkingBudget`, `VertexProjectId`).

### 🎬 Namespace: `FfmpegUtilities`
Wraps the local `ffmpeg.exe` binary to preprocess media before sending it to the cloud.
- **`FfmpegToolkit`**: A headless command builder. Contains logic like `ProcessSplitVideoAsync` (slicing video into 3-minute chunks with a 3-minute overlap), decimating framerates to 1 FPS (`-vf fps=1`), and downmixing audio to mono (`-ac 1`).
- **`FfmpegInteractiveMenu`**: A console-based UI for users to manually trigger FFmpeg compressions without running the AI pipeline.
- **`ConsoleUiHelper`**: Helpers for rendering progress bars and terminal UI elements during video processing.

### 🏗️ Namespace: `Infrastructure`
Cross-cutting concerns like file management, logging, and robustness.
- **`SessionLogger`**: Automatically creates timestamped directories for every session (e.g., `folder-X-date`). It dumps raw LaTeX outputs to files and maintains a Markdown log (`chat_log.md`) of the entire interaction.
- **`AttachmentHandler`**: A sophisticated file discovery system. When a user types `/attach file.pdf`, this class searches through a list of fallback directories (the `HistoryPreloadPaths` and `IncludePaths`), finds the file, and prepares it for upload.
- **`ApiResilience`**: Contains logic to gracefully handle HTTP transient errors, such as rate limits (HTTP 429) or temporary server errors (HTTP 503).

### 🌐 Namespace: `GoogleGenAi`
- **`GeminiClientBuilder`**: Encapsulates the raw HTTP `HttpClient` construction. It maps our C# configuration objects (like `ThinkingConfig`, `SystemInstruction`, `Tools`) into the exact JSON payload expected by Google's REST APIs.

### 📄 Namespace: `DocumentUtilities`
- **`LatexToolkit`**: Helper methods to parse raw markdown outputs from Gemini, strip out ` ```latex ` code blocks, and validate the resulting strings.
- **`LatexTimestampHelper`**: Tools for manipulating and adjusting embedded timestamps within the LaTeX document.

---

## 7. Known API Limitations & Troubleshooting

### Cache Priming vs. Roundtrips (Why not send everything at once?)
To save an API roundtrip, one might be tempted to send the `training-history` and the 30-minute lecture video in a single prompt. However, this is highly counterproductive:
1. **Cache Priming & Focus:** By sending the massive history first and demanding an "Acknowledgment", the model is forced to parse and build an internal context cache (Context Caching) purely for the rules. When the actual video arrives in the next prompt, the model focuses 100% on execution rather than splitting its attention.
2. **Context Overload:** Shoving thousands of lines of history and a large video into a single prompt often overwhelms the model, leading to hallucinated outputs or `500 Internal Server Errors`.

### HTTP 500 Internal Error (Google Server Crash)
If the application gets stuck in a retry-loop with an `Internal error encountered (HTTP 500)`, it means the Google backend crashed while processing the request. This is rarely a network issue, but usually a prompt/payload issue. Common culprits include:
- **Overloaded System Instruction:** If `LoadHistoryIntoSystemInstruction` is set to `true`, the pipeline injects the entire history into the `SystemInstruction`. Google's backend often crashes when the system instruction is too large or contains complex attachments. **Fix:** Set it to `false` so the history is sent as a normal, much safer User Prompt (`AcknowledgeHistoryAsync`).
- **Thinking with Flash Models:** Enabling high `ThinkingBudget` or `ThinkingLevel: HIGH` on "Flash" models (e.g., `gemini-3.5-flash`) while processing massive video files is highly unstable and frequently causes 500 errors. **Fix:** Either disable "Thinking" for flash models or switch to a "Pro" model (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), which handles reasoning natively.
- **Corrupted Video Chunks:** Occasionally, FFmpeg generates a chunk with a corrupted frame header that crashes the Gemini Vision encoder. **Fix:** Delete the video's `tmp` folder to force FFmpeg to recut the video.

---

## 8. Error Handling & ApiResilience

The `ApiResilience` class wraps all calls to the Google API and handles transient errors gracefully:
- **Rate Limits (HTTP 429):** If the API returns a `retryDelay`, the application parses it and waits exactly that long plus a 20-second buffer.
- **High Demand (HTTP 503):** If the server is overloaded ("high demand"), the application initiates a hard 3-minute backoff.
- **Linear Backoff:** For general 500 errors, the application uses a linear backoff (e.g., 45s, 75s, 105s) up to 8 times.
- **Interactive Skip:** During any waiting period, the user can press `Enter` to force an immediate retry, or `Ctrl+C` to cancel the delay. Canceling the delay aborts the current video chunk and safely moves the batch processor to the next file without crashing the application.
