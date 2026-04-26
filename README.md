# AI Extraction & Processing Tool 🤖🎥

Dieses Projekt ist ein spezialisiertes C#-Konsolenwerkzeug, das entwickelt wurde, um Vorlesungsvideos (insbesondere im mathematisch-akademischen Bereich) effizient vorzuverarbeiten und durch multimodale KI-Modelle (Google Gemini) in hochwertige LaTeX-Skripte und Transkripte zu übersetzen.

Es kombiniert leistungsstarke lokale **FFmpeg-Verarbeitung** zur Token-Optimierung mit einer interaktiven **REPL-Chat-Umgebung** (Read-Eval-Print Loop), die sowohl Google AI Studio als auch Google Cloud Vertex AI (Enterprise) unterstützt.

---

## ✨ Hauptfeatures

- **Zwei KI-Modi:** Unterstützt kostenlose/Developer Endpunkte (Google AI Studio File API) sowie Enterprise Endpunkte (Vertex AI mit Google Cloud Storage).
- **FFmpeg Video-Optimierung:** Schneidet und komprimiert stundenlange Vorlesungen blitzschnell. Reduziert Videos auf 1 FPS (perfekt für Tafeln!) und mischt Audio zu Mono ab, um massiv KI-Tokens, Kosten und Upload-Zeit zu sparen.
- **Multimodaler Chat & Datei-Upload:** Lade Bilder, PDFs, Code-Dateien und riesige Videos über den `/attach` Befehl direkt in den Kontext der KI.
- **Automatisches Session-Logging:** Jeder Chat-Verlauf wird als Markdown (`chat_log.md`) gespeichert. Alle KI-Antworten werden fortlaufend nummeriert als fertige `.tex` Dateien gesichert.
- **Dynamische KI-Parameter:** Passe `Temperature` und `MaxOutputTokens` dynamisch während des Chats an, um die Kreativität oder Präzision (z.B. für strikten LaTeX-Code) der Antworten zu steuern.

---

## 📂 Projektstruktur & Architektur

Das Projekt ist in verschiedene logische Namespaces unterteilt, um das Prinzip der *Single Responsibility* (SRP) zu wahren.

### 🌐 1. Namespace: `AiInteraction`
Das Herzstück der Konversation. Hier wird der Chatbot gesteuert, Dateien verwaltet und die Ausgaben geloggt.

- **`AiChatSession`**
  Der Haupt-REPL-Manager für den **Google AI Studio** Modus. Verwaltet den Chatverlauf, sendet Prompts an Gemini, streamt die Antworten in die Konsole und kümmert sich um den State (Speicher) der KI.

- **`VertexAiChatSession`**
  Das Enterprise-Pendant zur `AiChatSession`. Komplett isoliert, um mit **Google Cloud Vertex AI** zu kommunizieren. Handhabt zusätzlich das Bereinigen (`ForcePurgeGcsBucketAsync`) der GCS-Buckets, um Cloud-Kosten zu minimieren.

- **`AttachmentHandler`**
  Ein intelligentes "Trüffelschwein" für Dateien. Löst lokale Dateipfade auf (sucht in Arbeitsverzeichnissen, Upload-Ordnern etc.) und orchestriert den Upload. Textdateien werden direkt in den Prompt eingebettet, Medien (Video/Audio/Bilder) werden über die Google File API oder GCS hochgeladen.

- **`SessionLogger`**
  Kümmert sich um die Persistenz. Erstellt für jede gestartete Sitzung einen eigenen, mit Zeitstempel versehenen Ordner (`folder-X-month-day-year`) und speichert dort Markdown-Logs sowie `.tex`-Outputs.

- **`IUserInterface` & `ConsoleUserInterface`**
  Eine Abstraktionsschicht für die Benutzeroberfläche. Ermöglicht es dem Code, Eingaben zu lesen und Ausgaben zu schreiben, ohne fest an die Windows-Konsole gebunden zu sein (erleichtert zukünftige GUI-Updates oder automatisiertes Testing).

### 🎬 2. Namespace: `FfmpegUtilities`
Verantwortlich für die lokale Audio- und Videoverarbeitung vor dem KI-Upload.

- **`FfmpegToolkit`**
  Das Herzstück der Videoverarbeitung (Headless). Baut komplexe FFmpeg-Befehle zusammen. 
  *Highlights:* `ProcessSplitVideoAsync` (Schneidet Videos in überlappende Segmente, damit die KI keine Sätze abschneidet) und `ProcessGeneralVideoAsync` (1 FPS Video, Mono Audio, Speedups).

- **`FfmpegInteractiveMenu`**
  Die textbasierte Konsolenoberfläche (Frontend) für das `FfmpegToolkit`. Führt den Benutzer durch verschiedene Voreinstellungen (Presets) und Batch-Verarbeitungsmodi.

- **`ConsoleUiHelper`**
  Eine Hilfsklasse zum Zeichnen von sauberen Auswahlmenüs (z.B. Dateilisten) in der Konsole, hält die Logik aus dem Hauptmenü heraus.

- **`FfmpegProcessor`**
  *Legacy (Veraltet).* Eine ältere Iteration der FFmpeg-Implementierung, die für Abwärtskompatibilität noch im Projekt liegt, aber weitgehend vom Toolkit abgelöst wurde.

### 🔑 3. Namespace: `GoogleGenAi`
Alles rund um die Authentifizierung und Verbindungsaufbau zu den Google Servern.

- **`GoogleAiClientBuilder`**
  Sucht sicher nach API-Keys in den Windows-Umgebungsvariablen (`API_KEY-ai-studio-test-project-X`) und instanziiert den offiziellen `Google.GenAI.Client`. Entscheidet anhand der Konfiguration, ob ein AI Studio- oder ein Vertex AI-Client gebaut wird.

### ⚙️ 4. Namespace: `Config`
Zentrale Steuerung aller Parameter des Programms.

- **`AppConfig` (Statische Klasse)**
  Der "Single Point of Truth" für das gesamte Programm. Beinhaltet absolute Pfade zu Arbeitsverzeichnissen, Upload-Ordnern, History-Ordnern und System-Prompts (`gemini.md`). Definiert außerdem die Standard-Modellparameter (`Temperature`, `MaxOutputTokens`, etc.).

- **`ChatConfig`, `VertexAiConfig`, `AIConfig` (DTOs)**
  Datenklassen, die zur Strukturierung der Konfiguration innerhalb von `AppConfig` genutzt werden. Erlauben es, Sitzungen Parameter als sauberes Objekt zu übergeben.

### 🚀 Einstiegspunkt (Kein expliziter Namespace)
- **`Program`**
  Die Hauptklasse (`Main`-Methode). Begrüßt den Benutzer mit dem Startmenü, fragt den gewünschten Betriebsmodus ab (AI Studio, Vertex AI oder lokales FFmpeg) und orchestriert die Instanziierung der entsprechenden Klassen. Hier sind auch die KI-Modelle (z.B. `gemini-2.5-pro`, `gemini-3.1-flash-lite-preview`) samt Preisinformationen hinterlegt.

---

## 💻 Bedienung & Befehle im Chat

Sobald du eine AI-Sitzung gestartet hast, kannst du normal mit der KI chatten. Zusätzlich gibt es spezielle "Built-in Commands":

- **`attach datei1.mp4, code.tex | Deine Frage`**
  Sucht die angegebenen Dateien, lädt sie hoch (GCS oder File API) und hängt sie unsichtbar an deine Nachricht an. Das `|` Zeichen trennt die Dateinamen von deinem tatsächlichen Prompt.

- **`clear` / `reset`**
  Löscht das Kurzzeitgedächtnis (den Chat-Verlauf) der KI und setzt sie auf den Zustand unmittelbar nach dem Laden der System-Instruction / History zurück.

- **`set temp 0.1`**
  Ändert die Kreativität der KI dynamisch für die nächsten Anfragen (0.0 = extrem präzise/deterministisch, gut für Code; 1.0+ = sehr kreativ).

- **`set tokens 65535`**
  Erhöht oder verringert das maximale Token-Limit für KI-Antworten.

- **`exit` / `quit`**
  Beendet die Chat-Sitzung ordnungsgemäß und bereinigt Cloud-Speicher (löscht temporäre GCS-Uploads).

---

## 🛠️ Vorbedingungen & Setup

1. **FFmpeg:** Muss auf dem System installiert und als globale Umgebungsvariable (PATH) erreichbar sein.
2. **Google AI Studio:** Lege eine Umgebungsvariable namens `API_KEY-ai-studio-test-project-1` an und füge dort deinen Gemini API-Key ein.
3. **Google Cloud Vertex AI:** Setzt voraus, dass du die Google Cloud CLI (`gcloud`) installiert hast und über `gcloud auth application-default login` authentifiziert bist. Das verknüpfte Projekt muss über ein aktives Rechnungskonto (Billing Account) verfügen.