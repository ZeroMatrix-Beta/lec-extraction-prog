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
- **Overloaded System Instruction:** If `LoadHistoryIntoSystemInstruction` is set to `true`, the pipeline injects the history into the `SystemInstruction` (using XML framing `<file path="...">` and embedding images inline via `InlineData`). Note: While inline bytes prevent external URI resolution issues, extremely massive history sets can still exceed context or payload limits. If instability occurs, set it to `false` to send history via explicit user prompt acknowledgment (`AcknowledgeHistoryAsync`).
- **Thinking with Flash Models:** Enabling high `ThinkingBudget` or `ThinkingLevel: HIGH` on "Flash" models (e.g., `gemini-3.5-flash`) while processing massive video files is highly unstable and frequently causes 500 errors. **Fix:** Either disable "Thinking" for flash models or switch to a "Pro" model (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), which handles reasoning natively.
- **Corrupted Video Chunks:** Occasionally, FFmpeg generates a chunk with a corrupted frame header that crashes the Gemini Vision encoder. **Fix:** Delete the video's `tmp` folder to force FFmpeg to recut the video.

---

## 8. Error Handling & ApiResilience

The `ApiResilience` class wraps all calls to the Google API and handles transient errors gracefully:
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
- **Speicherkosten-Management:** Da die Verarbeitung von hunderten überlappenden Video-Chunks die Speicherkosten in die Höhe treiben kann, nutzt die Anwendung eine `CleanupBucketAsync()`-Routine. Nachdem ein Chunk erfolgreich verarbeitet wurde (oder wenn eine Ausnahme auftritt), löscht die Anwendung die temporären Dateien aggressiv aus dem GCS-Bucket.
*Hinweis: Nicht gelöschte Dateien in einem Bucket verbrauchen bei zukünftigen Anfragen keine Prompt-Token, da die API nur die exakten `gs://`-URIs verarbeitet, die im jeweiligen Request-Payload gesendet werden.*

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

---

## 5. Pipeline-Mechanik: FFmpeg & Überlappende Segmente

Um eine 90-minütige Vorlesung zu transkribieren, führt das Senden des gesamten Videos auf einmal oft zur Erschöpfung des Kontextfensters oder zu übersprungenen Details. Die Pipeline löst dies durch:

1. **FFmpeg Slicing:** Das Video wird in streng definierte Chunks (z. B. jeweils 3 Minuten) mit einer Überlappung (z. B. 180 Sekunden, d.h. die letzten X Sekunden von Chunk 1 sind die ersten X Sekunden von Chunk 2) zerschnitten.
2. **Token-Optimierung:** Die Video-Framerate wird auf `1 FPS` dezimiert. Da sich Tafeln und Folien selten im Sub-Sekunden-Bereich ändern, bleibt die visuelle Information erhalten, während der Token-Payload drastisch reduziert wird.
3. **Audio-Downmixing:** Audio wird komprimiert und auf Mono heruntergemischt (`-ac 1`).
4. **Latex Refinement:** Aufgrund der Überlappungen enthalten die extrahierten LaTeX-Chunks duplizierte Sätze oder Formeln an den Rändern. Die `LatexRefinementSession` übergibt diese Chunks an eine deterministische KI (Temperature 0.0) mit der Anweisung, sie nahtlos zu einem einzigen, kontinuierlichen LaTeX-Dokument zusammenzuführen.

---

## 6. Architektur-Übersicht: Namespaces & Kernklassen

Das Projekt ist stark modularisiert, um Konfiguration, KI-Interaktionen, lokale Videoverarbeitung und Datei-Infrastruktur zu trennen. Im Folgenden findest du eine umfassende Übersicht der Namespaces und ihrer wichtigsten Klassen.

### ⚙️ Namespace: `AutoExtraction`
Verantwortlich für die vollautomatisierte Batch-Verarbeitungs-Pipeline. Orchestriert FFmpeg-Verarbeitung und KI-Inferenz sequenziell oder nebenläufig.
- **`AiStudioAutoExtractionSession`**: Verwaltet die Extraktions-Pipeline über die Google AI Studio File API. Nutzt ein **Producer-Consumer-Muster** (via `System.Threading.Channels`), was es FFmpeg ermöglicht, den nächsten Chunk zu verarbeiten, während Gemini den aktuellen analysiert.
- **`VertexAutoExtractionSession`**: Das Enterprise-Äquivalent. Lädt Dateien in den Google Cloud Storage (GCS) hoch und löscht sie nach Abschluss der Inferenz strikt (`CleanupBucketAsync`), um explodierende Speicherkosten zu verhindern.
- **`ExtractionHelpers`**: Hilfsmethoden speziell für die Extraktions-Schleife (z. B. Fortschritt drucken, verbleibende Zeit schätzen).
- **`RefinementUiHelper`**: Konsolen-UI-Elemente speziell zur Visualisierung des Extraktionsfortschritts.
- **`VideoDateParser`**: Hilfsprogramm, um Datums-Metadaten aus Dateinamen von Vorlesungsvideos zu extrahieren.

### 💬 Namespace: `DirectChatAiInteraction`
Verarbeitet die interaktive REPL (Read-Eval-Print Loop) für manuelles Debugging sowie deterministisches Post-Processing, das sich wie eine Chat-Session verhält.
- **`LatexRefinementSession`**: Die Post-Processing-Engine. Füttert die überlappenden `.tex`-Chunks, die von der `AutoExtraction` generiert wurden, in Gemini ein und weist es an, Duplikate aufzulösen und sie zu einem kontinuierlichen Dokument zusammenzuführen.
- **`DirectAiChatSessionVertex` / `DirectAiChatSessionAiStudio`**: Die primären REPL-Klassen. Verarbeiten Benutzereingaben im Terminal, erlauben dynamische Parameteränderungen (z.B. `/set temp 0.5`), verarbeiten `/attach`-Befehle und pflegen den Gesprächsverlauf.

### 🔧 Namespace: `Config`
Zentralisiertes Konfigurations-Management.
- **`ConfigLoader<T>`**: Ein generischer Loader, der den `Microsoft.Extensions.Configuration` Binder implementiert. Führt Standards aus `AppConfig.cs`, globale Überschreibungen aus `appsettings.json` und spezifische Einstellungen aus `{Session}Config.json` zusammen.
- **`AppConfig`**: Die einzige Quelle der Wahrheit (Single Source of Truth) für globale, fest codierte Fallback-Parameter (z. B. Fallback-Pfade, `DefaultThinkingBudget`, `VertexProjectId`).

### 🎬 Namespace: `FfmpegUtilities`
Wickelt die lokale `ffmpeg.exe` Binary ab, um Medien vorzuverarbeiten, bevor sie in die Cloud gesendet werden.
- **`FfmpegToolkit`**: Ein Headless-Befehls-Builder. Enthält Logik wie `ProcessSplitVideoAsync` (Zerschneiden von Videos in 3-Minuten-Chunks mit 3 Minuten Überlappung), Dezimieren der Framerate auf 1 FPS (`-vf fps=1`) und Heruntermischen von Audio auf Mono (`-ac 1`).
- **`FfmpegInteractiveMenu`**: Eine konsolenbasierte Benutzeroberfläche für Benutzer, um FFmpeg-Kompressionen manuell auszulösen, ohne die KI-Pipeline auszuführen.
- **`ConsoleUiHelper`**: Helfer für das Rendern von Fortschrittsbalken und Terminal-UI-Elementen während der Videoverarbeitung.

### 🏗️ Namespace: `Infrastructure`
Querschnittsbelange wie Dateiverwaltung, Logging und Robustheit.
- **`SessionLogger`**: Erstellt automatisch Ordner mit Zeitstempeln für jede Session (z.B. `folder-X-date`). Legt rohe LaTeX-Ausgaben in Dateien ab und pflegt ein Markdown-Log (`chat_log.md`) der gesamten Interaktion.
- **`AttachmentHandler`**: Ein hochentwickeltes Dateisuchsystem. Wenn ein Benutzer `/attach file.pdf` eingibt, durchsucht diese Klasse eine Liste von Fallback-Ordnern, findet die Datei und bereitet sie für den Upload vor.
- **`ApiResilience`**: Enthält Logik, um temporäre HTTP-Fehler anmutig zu behandeln, wie z.B. Ratenlimits (HTTP 429) oder temporäre Serverfehler (HTTP 503).

### 🌐 Namespace: `GoogleGenAi`
- **`GeminiClientBuilder`**: Kapselt die rohe HTTP `HttpClient`-Erstellung. Bildet unsere C#-Konfigurationsobjekte (wie `ThinkingConfig`, `SystemInstruction`, `Tools`) auf den exakten JSON-Payload ab, der von Googles REST APIs erwartet wird.

### 📄 Namespace: `DocumentUtilities`
- **`LatexToolkit`**: Hilfsmethoden, um rohe Markdown-Ausgaben von Gemini zu parsen, ` ```latex ` Codeblöcke zu entfernen und die resultierenden Strings zu validieren.
- **`LatexTimestampHelper`**: Werkzeuge zur Manipulation und Anpassung eingebetteter Zeitstempel im LaTeX-Dokument.

---

## 7. Bekannte API-Einschränkungen & Fehlerbehebung

### Cache Priming vs. Roundtrips (Warum nicht alles auf einmal senden?)
Um einen API-Roundtrip zu sparen, könnte man versucht sein, die `training-history` und das 30-minütige Vorlesungsvideo in einem einzigen Prompt zu senden. Dies ist jedoch höchst kontraproduktiv:
1. **Cache Priming & Fokus:** Indem man zuerst die massive Historie sendet und eine "Bestätigung" verlangt, wird das Modell gezwungen, zu parsen und einen internen Kontext-Cache aufzubauen, der rein für die Regeln zuständig ist. Wenn das eigentliche Video im nächsten Prompt eintrifft, konzentriert sich das Modell zu 100 % auf die Ausführung, anstatt seine Aufmerksamkeit zu teilen.
2. **Kontext-Überlastung:** Tausende Zeilen Historie und ein großes Video in einen einzigen Prompt zu packen, überfordert das Modell oft, was zu halluzinierten Ausgaben oder `500 Internal Server Errors` führt.

### HTTP 500 Internal Error (Google Server Absturz)
Wenn die Anwendung in einer Retry-Schleife mit einem `Internal error encountered (HTTP 500)` feststeckt, bedeutet dies, dass das Google Backend beim Verarbeiten der Anfrage abgestürzt ist. Dies ist selten ein Netzwerkproblem, sondern meist ein Prompt-/Payload-Problem. Häufige Ursachen sind:
- **Überladene System Instruction:** Wenn `LoadHistoryIntoSystemInstruction` auf `true` gesetzt ist, injiziert die Pipeline die Historie in die `SystemInstruction` (mittels XML-Framing `<file path="...">` und direktem Inlining von Bildern als `InlineData`). Hinweis: Obwohl Inline-Bytes Probleme mit externen Datei-URIs umgehen, können extrem große Historien weiterhin Nutzlastgrenzen überschreiten. Bei Instabilitäten setze es auf `false`, um die Historie über einen separaten User-Prompt (`AcknowledgeHistoryAsync`) zu senden.
- **Thinking bei Flash-Modellen:** Die Aktivierung eines hohen `ThinkingBudget` oder `ThinkingLevel: HIGH` bei "Flash"-Modellen (z. B. `gemini-3.5-flash`) während der Verarbeitung massiver Videodateien ist sehr instabil und verursacht häufig 500er Fehler. **Lösung:** Deaktiviere "Thinking" für Flash-Modelle oder wechsle zu einem "Pro"-Modell (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), welches Logik nativ verarbeitet.
- **Beschädigte Video-Chunks:** Gelegentlich generiert FFmpeg einen Chunk mit einem beschädigten Frame-Header, der den Gemini Vision Encoder zum Absturz bringt. **Lösung:** Lösche den `tmp`-Ordner des Videos, um FFmpeg zu zwingen, das Video neu zu schneiden.

---

## 8. Fehlerbehandlung & ApiResilience

Die Klasse `ApiResilience` kapselt alle Aufrufe an die Google API und behandelt transiente Fehler anmutig:
- **Ratenlimits (HTTP 429):** Wenn die API ein `retryDelay` zurückgibt, parst die Anwendung dies und wartet genau so lange plus einen Puffer von 20 Sekunden.
- **Hohe Auslastung (HTTP 503):** Wenn der Server überlastet ist ("high demand"), initiiert die Anwendung einen harten 3-Minuten-Backoff.
- **Linearer Backoff:** Für allgemeine 500er Fehler verwendet die Anwendung einen linearen Backoff (z. B. 45s, 75s, 105s) bis zu 8 Mal.
- **Interaktiver Skip:** Während jeder Wartezeit kann der Benutzer `Enter` drücken, um einen sofortigen Retry zu erzwingen, oder `Ctrl+C`, um die Verzögerung abzubrechen. Das Abbrechen der Verzögerung bricht den aktuellen Video-Chunk ab und bewegt den Batch-Prozessor sicher zur nächsten Datei, ohne dass die Anwendung abstürzt.
