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



And these objects that are the partial derivatives. So, here in, in one equation you have a bit the goal and the syllabus of the course. How will, how will we proceed? So, first we will start looking at the space. In Analysis 1, you did everything in $\mathbb{R}$. \textit{[Writes $\mathbb{R}$ on the board]} Now we will need to introduce $\mathbb{R}^3$, but we are mathematicians, so we will... well, some of us. Um, some of you are physicists or are proto-physicists, would like to become physicists. So, we will use this \textit{[writes $\mathbb{R}^n$]} because we don't want to spend time proving the same theorem in, in the plane $\mathbb{R}^2$, then in $\mathbb{R}^3$ in the space, and then when you go to do general relativity you need $\mathbb{R}^4$, right? We do everything in $\mathbb{R}^n$. $n$ is the dimension. 
\end{spoken-clean}

\begin{math-stroke}[The Space \texorpdfstring{$\mathbb{R}^n$}{Rn}]
We generalize our study to $

AI Studio User: 
gemini-3-flash-preview (Drücke Strg+C zum Abbrechen): 
2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

[Exception gefangen] Art der Exception: ClientError
Originaler Fehlertext: You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit. 
* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_input_token_count, limit: 250000, model: gemini-3-flash
Please retry in 24.999439575s.
Details: {"@type":"type.googleapis.com/google.rpc.Help","links":[{"description":"Learn more about Gemini API quotas","url":"https://ai.google.dev/gemini-api/docs/rate-limits"}]}
{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_input_token_count","quotaId":"GenerateContentInputTokensPerModelPerMinute-FreeTier","quotaDimensions":{"location":"global","model":"gemini-3-flash"},"quotaValue":"250000"}]}
{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"24s"}

[Rate Limit] API schlägt Wartezeit vor. Warte 26 Sekunden... (Versuch 1/5)

gemini-3-flash-preview (Drücke Strg+C zum Abbrechen): 
2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

[Exception gefangen] Art der Exception: ClientError
Originaler Fehlertext: You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit. 
* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_input_token_count, limit: 250000, model: gemini-3-flash
Please retry in 56.528867482s.
Details: {"@type":"type.googleapis.com/google.rpc.Help","links":[{"description":"Learn more about Gemini API quotas","url":"https://ai.google.dev/gemini-api/docs/rate-limits"}]}
{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_input_token_count","quotaId":"GenerateContentInputTokensPerModelPerMinute-FreeTier","quotaDimensions":{"model":"gemini-3-flash","location":"global"},"quotaValue":"250000"}]}
{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"56s"}

[Rate Limit] API schlägt Wartezeit vor. Warte 58 Sekunden... (Versuch 2/5)




\begin{spoken-clean}[00:12:31 - 00:13:14]
And these objects that are the partial derivatives. So, here in, in one equation you have a bit the goal and the syllabus of the course. How will, how will we proceed? So, first we will start looking at the space. In Analysis 1, you did everything in $\mathbb{R}$. \textit{[Writes $\mathbb{R}$ on the board]} Now we will need to introduce $\mathbb{R}^3$, but we are mathematicians, so we will... well, some of us. Um, some of you are physicists or are proto-physicists, would like to become physicists. So, we will use this \textit{[writes $\mathbb{R}^n$]} because we don't want to spend time proving the same theorem in, in the plane $\mathbb{R}^2$, then in $\mathbb{R}^3$ in the space, and then when you go to do general relativity you need $\mathbb{R}^4$, right? We do everything in $\mathbb{R}^n$. $n$ is the dimension. 
\end{spoken-clean}

\begin{math-stroke}[The Space \texorpdfstring{$\mathbb{R}^n$}{Rn}]
We generalize our study to $

AI Studio User: 
gemini-3-flash-preview (Drücke Strg+C zum Abbrechen): 
2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

[Exception gefangen] Art der Exception: ClientError
Originaler Fehlertext: You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit. 
* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_input_token_count, limit: 250000, model: gemini-3-flash
Please retry in 24.999439575s.
Details: {"@type":"type.googleapis.com/google.rpc.Help","links":[{"description":"Learn more about Gemini API quotas","url":"https://ai.google.dev/gemini-api/docs/rate-limits"}]}
{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_input_token_count","quotaId":"GenerateContentInputTokensPerModelPerMinute-FreeTier","quotaDimensions":{"location":"global","model":"gemini-3-flash"},"quotaValue":"250000"}]}
{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"24s"}

[Rate Limit] API schlägt Wartezeit vor. Warte 26 Sekunden... (Versuch 1/5)

gemini-3-flash-preview (Drücke Strg+C zum Abbrechen): 
2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

2
Exception thrown: 'Google.GenAI.ClientError' in System.Private.CoreLib.dll

[Exception gefangen] Art der Exception: ClientError
Originaler Fehlertext: You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit. 
* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_input_token_count, limit: 250000, model: gemini-3-flash
Please retry in 56.528867482s.
Details: {"@type":"type.googleapis.com/google.rpc.Help","links":[{"description":"Learn more about Gemini API quotas","url":"https://ai.google.dev/gemini-api/docs/rate-limits"}]}
{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaMetric":"generativelanguage.googleapis.com/generate_content_free_tier_input_token_count","quotaId":"GenerateContentInputTokensPerModelPerMinute-FreeTier","quotaDimensions":{"model":"gemini-3-flash","location":"global"},"quotaValue":"250000"}]}
{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"56s"}

[Rate Limit] API schlägt Wartezeit vor. Warte 58 Sekunden... (Versuch 2/5)
The program '[31436] csharp.exe' has exited with code -1 (0xffffffff).

