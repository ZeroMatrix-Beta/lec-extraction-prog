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
Um C#-Compiler- und Analyzer-Warnungen (insbesondere `CA1860`, `SYSLIB1045`, `IDE0028`, `IDE0090`, `IDE0060`) dauerhaft zu verhindern, sind folgende Regeln von jeder KI strikt und proaktiv einzuhalten:

1. **Collections prüfen (`CA1860` - `Count > 0` statt `Any()`):**
   Sowohl aus Gründen der Klarheit als auch der Leistung ist bei Listen, Collections, Dictionaries und Arrays der direkte Vergleich `.Count > 0` bzw. `.Length > 0` der LINQ-Methode `.Any()` vorzuziehen. LINQ `.Any()` erzeugt unnötigen Enumerator-Overhead.
2. **Target-typed `new()` (`IDE0028` / `IDE0090`):**
   Vereinfache Instanziierungen konsequent zu `new()` oder Collection Expressions `[]`, sofern der Zieldatentyp links eindeutig definiert ist (z.B. `List<string> list = [];` oder `new Part { Text = ... }` -> `new() { Text = ... }`).
3. **Kompilierzeit-Regex (`SYSLIB1045` - `[GeneratedRegex]`):**
   Verwende niemals dynamische `new Regex(...)`, `Regex.Replace(...)` oder `Regex.IsMatch(...)` Aufrufe zur Laufzeit. Nutze stattdessen konsequent Source Generatoren (`[GeneratedRegex("...")]` an partiellen Methoden in partiellen Klassen).
4. **Keine toten Parameter (`IDE0060`):**
   Methodensignaturen müssen sauber von ungenutzten Parametern befreit werden. Wenn Helper-Methoden umgeschrieben werden, sind alte Parameter sofort zu löschen.
5. **Verpflichtender Build-Check vor Abschluss der Aufgabe:**
   Bevor eine KI ihre Arbeit an den Benutzer übergibt, **muss** zwingend `dotnet build` ausgeführt werden. Eine Aufgabe gilt erst dann als erledigt, wenn der Build exakt `0 Warnung(en)` und `0 Fehler` ausgibt. Jede auftretende Warnung ist sofort zu beheben.

## 4. Zugriff auf externe Verzeichnisse
**Regel:** Die KI benötigt keinen Lese- oder Schreibzugriff auf das externe Verzeichnis `C:\Users\miche\latex\prompt-engineering` oder dessen Unterordner. Die dortigen Prompt-Vorlagen werden außerhalb dieses Projekts verwaltet. Fordere niemals Berechtigungen für diesen Pfad an.

