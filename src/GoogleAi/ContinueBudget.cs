namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Bounds the "continue" requests of one generation loop (extraction part, refinement
/// step, PDF fix). Each continue re-sends the whole history - for extraction that includes the
/// video - so an unbounded loop is an unbounded bill.
///
/// <para>Two limits. A hard total, so even a model that keeps producing text stops eventually. And a
/// limit on requests in a row that made no progress: an empty response, or one whose tail repeats
/// the previous response's tail (a model stuck re-emitting the same block until MAX_TOKENS). A
/// plain "reset the counter whenever there was text" has neither, which is the bug this replaces.</para>
/// [Human] Begrenzt die Continue-Anfragen einer Generierung: harte Obergrenze plus Abbruch, wenn
/// mehrere Antworten hintereinander leer sind oder sich wiederholen.
/// </summary>
public sealed class ContinueBudget {
    private const int TailLength = 300;

    private readonly int _maxWithoutProgress;
    private string _previousTail = "";

    public ContinueBudget(int maxTotal, int maxWithoutProgress = 3) {
        MaxTotal = Math.Max(1, maxTotal);
        _maxWithoutProgress = Math.Max(1, maxWithoutProgress);
    }

    /// <summary>The 1-based number of the request currently being sent.</summary>
    public int RequestNumber { get; private set; } = 1;

    public int MaxTotal { get; }

    /// <summary>Responses in a row that were empty or repeated the previous tail.</summary>
    public int RequestsWithoutProgress { get; private set; }

    /// <summary>
    /// Records the response of the request just sent. Returns true if another continue request may
    /// follow, otherwise false with a German, user-facing <paramref name="stopReason"/>.
    /// </summary>
    public bool TryContinue(string response, out string stopReason) {
        string tail = response.Length > TailLength ? response[^TailLength..] : response;
        bool madeProgress = !string.IsNullOrWhiteSpace(response) && tail != _previousTail;
        _previousTail = tail;
        RequestsWithoutProgress = madeProgress ? 0 : RequestsWithoutProgress + 1;

        if (RequestNumber >= MaxTotal) {
            stopReason = $"Maximale Anzahl an Requests ({MaxTotal}) erreicht.";
            return false;
        }
        if (RequestsWithoutProgress >= _maxWithoutProgress) {
            stopReason = $"{RequestsWithoutProgress} Antworten in Folge ohne Fortschritt (leer oder wiederholt).";
            return false;
        }

        RequestNumber++;
        stopReason = "";
        return true;
    }
}
