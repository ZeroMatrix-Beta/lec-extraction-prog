# Console UI & Logging Improvements — Vorschläge & Roadmap

Dieses Dokument fasst den aktuellen Stand des Konsolen- und Logging-Systems in `lec-extraction-prog` zusammen und dokumentiert konkrete Verbesserungsvorschläge für die Interaktion durch Menschen und automatisierte Agenten.

---

## 1. Aktueller Status (Was bereits gut funktioniert)

- **Saubere Exception-Aufbereitung**:
  - Rohe Google RPC- und QuotaFailure-JSON-Strukturen sowie redundante Dokumentations-URLs werden durch `ExceptionExtensions.CleanMessage()` vollständig gefiltert.
  - Fehler wie Quota-Überschreitungen werden kompakt als `QUOTA_EXCEEDED (429): Limit: ... / Retry in ...s` dargestellt.
- **Headless CLI & Robustheit**:
  - `lecx` nutzt `PresetPromptSource`: In nicht-interaktiven Umgebungen blockiert das Programm niemals an Auswahldialogen, sondern bricht bei fehlenden Pflichtangaben mit klaren Fehlermeldungen ab.
- **Sicheres Encoding in Logs & Pipes**:
  - Wenn `stdout` in eine Datei oder Pipe umgeleitet wird (`IsTerminalOutput = false`), schaltet `Ui.cs` automatisch auf ASCII-Rahmen um. Dadurch werden typische Windows-1252-Codepage-Kollisionen (`â”€`) vermieden.
  - Der Retry- und Refill-Countdown (`InteractiveDelay.SmartDelayAsync`) flutet bei nicht-interaktiven Ausgaben nicht mehr zeilenweise den Log, sondern gibt eine kompakte Infozeile aus (`Warte 64s: ...`).
- **Abschluss-Bilanzen**:
  - Jede Sitzung schließt mit einer sauberen Tabelle zu Token-Verbrauch (Prompt, Output, Caching-Hits) und Wartezeiten ab.

---

## 2. Konkrete Verbesserungsvorschläge

### 2.1 Quiet- / Meilenstein-Modus (`--quiet` oder `--milestones-only`)
- **Problem**:
  Während der Transkription wird der erzeugte LaTeX-Code in Echtzeit Token für Token bzw. Block für Block in die Konsole gestreamt (`Ui.RawLine`). Bei einer 90-Minuten-Vorlesung mit 4 Parts erzeugt dies über 2.000 Zeilen LaTeX-Ausgabe im Terminal bzw. Logfile (100+ KB reiner Text). Für Hintergrund-Tasks und parallele Läufe erschwert dieser "Textteppich" das Erkennen von Warnungen, Retries und Fehlern.
- **Lösungsvorschlag**:
  - Einführung eines CLI-Flags `--quiet` (oder `--log-stream=false`).
  - Im Quiet-Modus wird das rohe LaTeX nicht auf die Konsole geschrieben, sondern ausschließlich in die Zieldateien (`step1-...-partN.tex`).
  - Auf der Konsole erscheinen nur noch Meilensteine (z. B. `[Part 2/4] Upload abgeschlossen`, `[Part 2/4] Generierung läuft (1.200 Wörter)`, `[Part 2/4] Gespeichert unter ...`).

### 2.2 Headless-Hinweise bereinigen (Enter-Aufforderung ausblenden)
- **Problem**:
  Bei einem API-Rate-Limit oder hoher Serverlast gibt das System aktuell folgende Meldung aus:
  ```text
  [Hohe Auslastung] [WARNUNG] Das Modell ist stark nachgefragt. Warte 64 Sekunden... (Versuch 3/8) (Oder drücke Enter für sofortigen Retry)
  ```
  In einer nicht-interaktiven Shell (Headless CLI, Hintergrund-Task, Agent) kann der Benutzer kein Enter drücken. Der Zusatz erweckt fälschlicherweise den Eindruck, das Tool warte auf Eingabe.
- **Lösungsvorschlag**:
  - In `ApiRetryPolicy.cs` den Text `(Oder drücke Enter für sofortigen Retry)` nur dann anhängen, wenn `Ui.PromptSource.IsInteractive == true` ist (analog zu `InteractiveDelay.cs`).

### 2.3 Heartbeat- / Lebendigkeitssignal bei langen Generierungen
- **Problem**:
  Große Vorlesungs-Abschnitte (insbesondere bei komplexen mathematischen Tafelanschrieben) können 5 bis 12 Minuten für die Antwortgenerierung beanspruchen. Wenn das Streaming unterbrochen ist oder auf den ersten Token gewartet wird, herrscht im Log minutenlange Stille.
- **Lösungsvorschlag**:
  - Ein periodischer Heartbeat (z. B. alle 30 oder 60 Sekunden), der eine dezente Statuszeile ausgibt:
    `[Heartbeat] Generierung aktiv (Dauer: 2m 15s | 1.840 Tokens empfangen)`

### 2.4 Maschinenlesbare Fortschrittsdatei (`progress.json`)
- **Problem**:
  Externe Skripte, Monitoring-Tools oder AI-Agenten müssen aktuell das rohe Konsolen-Log parsen, um festzustellen, bei welchem Part ein Lauf steht oder ob er sich im Backoff befindet.
- **Lösungsvorschlag**:
  - Optionales Schreiben einer kleinen `progress.json` im jeweiligen Ausgabeverzeichnis (`extracted_output/<video-name>/`):
    ```json
    {
      "video": "2026-09-22-tuesday-week2-algebra-1.mp4",
      "stage": "extract",
      "currentPart": 3,
      "totalParts": 4,
      "status": "RUNNING",
      "model": "gemini-3.8-flash",
      "retries": 0,
      "lastUpdateUtc": "2026-10-10T09:40:00Z"
    }
    ```

### 2.5 Erkennung von Dateisperren (`dotnet build`)
- **Problem**:
  Wenn Hintergrundprozesse (`lec-extraction-prog.exe`) aktiv laufen, schlägt ein gleichzeitiges `dotnet build` mit Fehler `MSB3026 / MSB3027` fehl, weil Windows die laufende `.exe` sperrt.
- **Lösungsvorschlag**:
  - Dokumentation oder Prüfung: Vor Builds prüfen, ob Instanzen von `lec-extraction-prog` laufen, oder im Entwicklungszyklus alternative Ausgabeordner nutzen.
