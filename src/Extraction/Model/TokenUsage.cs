namespace LectureExtraction.Extraction.Model;

/// <summary>
/// [AI Context] Replaces the four parallel loose `int` locals (input/output/cached/fresh) that
/// both extraction sessions tracked separately per video part and per whole file. `Fresh` is
/// derived rather than stored so it can never drift out of sync with `Input`/`Cached`, and `+`
/// lets a running total simply be `total += partUsage` instead of three separate `+=` lines.
/// [Human] Fasst die drei einzeln mitgezählten Token-Werte (Input/Output/Gecacht) in einem Typ
/// zusammen. "Fresh" (frisch verbrauchte, nicht gecachte Tokens) wird berechnet statt gespeichert.
/// </summary>
public readonly record struct TokenUsage(int Input, int Output, int Cached) {
    public int Fresh => System.Math.Max(0, Input - Cached);

    public static TokenUsage operator +(TokenUsage left, TokenUsage right) =>
        new(left.Input + right.Input, left.Output + right.Output, left.Cached + right.Cached);
}
