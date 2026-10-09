namespace LectureExtraction.Extraction.Model;

/// <summary>
/// [AI Context] Replaces the four parallel loose `int` locals (input/output/cached/fresh) that
/// both extraction sessions tracked separately per video part and per whole file. `Fresh` is
/// derived rather than stored so it can never drift out of sync with `Input`/`Cached`, and `+`
/// lets a running total simply be `total += partUsage` instead of three separate `+=` lines.
///
/// <para>`Output` is Gemini's candidate count - the visible answer only. `Thinking` is reported
/// separately by the API and billed as output on top of it, so the two are kept apart rather than
/// one being labelled as including the other. `Requests` counts the requests summed in, so a header
/// can say how many continue requests a part or step needed.</para>
/// [Human] Fasst die einzeln mitgezählten Token-Werte (Input/Output/Gecacht/Thinking) in einem Typ
/// zusammen. "Fresh" (frisch verbrauchte, nicht gecachte Tokens) wird berechnet statt gespeichert.
/// </summary>
public readonly record struct TokenUsage(int Input, int Output, int Cached, int Thinking = 0, int Requests = 0) {
    public int Fresh => System.Math.Max(0, Input - Cached);

    public static TokenUsage operator +(TokenUsage left, TokenUsage right) =>
        new(left.Input + right.Input, left.Output + right.Output, left.Cached + right.Cached,
            left.Thinking + right.Thinking, left.Requests + right.Requests);
}
