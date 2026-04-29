# AI Coding Instructions & Guidelines

Dieses Dokument enthält verbindliche Regeln für alle KI-Programmierassistenten (wie Gemini, Copilot, Cursor), die an diesem C#-Projekt (`lec-extraction-prog`) arbeiten. Bitte lies diese Regeln vor jeder Code-Generierung sorgfältig durch.

## 1. Exception Handling & Konsolen-Ausgaben
**Regel:** *Niemals* Exceptions stillschweigend abfangen. Jede gefangene Exception muss sichtbare Spuren in der Konsole hinterlassen!

Klar, eine "Exception thrown"-Meldung im Terminal ist oft unschön, aber für das Debuggen von Datei-Uploads, API-Limits und Cloud-Berechtigungen in diesem Tool ist es absolut essenziell, genau zu wissen, was schiefgelaufen ist.

Wenn eine Exception in einem `catch`-Block gefangen wird, **muss** immer der genaue Fehlertext (`ex.Message`) und idealerweise auch die Art der Exception (`ex.GetType().Name`) per `Console.WriteLine` ausgegeben werden.

**Negativ-Beispiel (VERBOTEN):**
```csharp
catch (Exception ex) {
    Console.WriteLine("Hoppla, es gab einen Fehler beim Upload."); 
}
```

**Positiv-Beispiel (ZUKÜNFTIGER STANDARD):**
```csharp
catch (Exception ex) {
    Console.WriteLine($"\n[Exception gefangen] Art der Exception: {ex.GetType().Name}");
    Console.WriteLine($"Originaler Fehlertext: {ex.Message}");
}
```
*Hinweis: Dies gilt auch für GCS-Bereinigungen, FFmpeg-Prozesse und API-Aufrufe.*

## 2. Erhalt von Architektur-Kommentaren
Das Projekt verwendet ausgiebig `[AI Context]` und `[Human]` Tags in den C#-Summaries (`/// <summary>`). 
- Diese Kommentare dienen dazu, den Sinn und Zweck von Klassen für KIs und Menschen sofort greifbar zu machen. 
- Bei Refactorings dürfen diese Kommentare **unter keinen Umständen** gelöscht werden. 
- Wenn du neue komplexe Architekturen (wie Pipelines oder Manager-Klassen) erstellst, statte sie ebenfalls mit `[AI Context]`-Kommentaren aus.
