# AI Coding Instructions & Guidelines

Dieses Dokument enthält verbindliche Regeln für alle KI-Programmierassistenten (wie Gemini, Copilot, Cursor), die an diesem C#-Projekt (`lec-extraction-prog`) arbeiten. 

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

## 3. IDE-Warnungen & C#-Performance Best Practices (VERBINDLICH)
Um C#-Compiler- und Analyzer-Warnungen (z.B. Roslyn / Sonar / IDE0028 / SYSLIB1045) dauerhaft zu verhindern, sind folgende Regeln strikt einzuhalten:
1. **Collections prüfen (`Count > 0` statt `Any()`):**
   Sowohl aus Gründen der Klarheit als auch der Leistung ist bei Listen, Collections und Arrays der direkte Vergleich `.Count > 0` bzw. `.Length > 0` der LINQ-Methode `.Any()` vorzuziehen.
2. **Target-typed `new()`:**
   Vereinfache Instanziierungen konsequent zu `new()`, sofern der Zieldatentyp links eindeutig definiert ist (z.B. `List<string> list = [];` oder `new Part { Text = ... }` -> `new() { Text = ... }`).
3. **Kompilierzeit-Regex (`[GeneratedRegex]`):**
   Verwende keine dynamischen `Regex.Replace(...)` oder `Regex.IsMatch(...)` Aufrufe in Schleifen oder asynchronen Pfaden. Nutze stattdessen Source Generatoren (`[GeneratedRegex("...")]` an partiellen Methoden).
4. **Keine toten Parameter:**
   Methodensignaturen müssen sauber von ungenutzten Parametern befreit werden.
