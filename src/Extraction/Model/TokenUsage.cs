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
///
/// <para>IMPORTANT: `Cached` is Google's reported CachedContentTokenCount and is not to be believed,
/// neither as a hit nor as a miss (see UsageReport.CachedTokens and Documentation.md §4.4). So `Fresh`,
/// derived from it, is "fresh according to Google", not the tokens that really counted against a
/// rate limit or a bill.</para>
/// [Human] Fasst die einzeln mitgezählten Token-Werte (Input/Output/Gecacht/Thinking) in einem Typ
/// zusammen. "Fresh" (frisch verbrauchte, nicht gecachte Tokens) wird berechnet statt gespeichert.
/// WICHTIG: "Gecacht" und damit "Frisch" sind Googles Angaben und nicht verlässlich.
/// </summary>
public readonly record struct TokenUsage(int Input, int Output, int Cached, int Thinking = 0, int Requests = 0) {
    /// <summary>IMPORTANT: Input minus Google's unreliable cache figure - fresh according to Google only.</summary>
    public int Fresh => System.Math.Max(0, Input - Cached);

    public static TokenUsage operator +(TokenUsage left, TokenUsage right) =>
        new(left.Input + right.Input, left.Output + right.Output, left.Cached + right.Cached,
            left.Thinking + right.Thinking, left.Requests + right.Requests);
}
