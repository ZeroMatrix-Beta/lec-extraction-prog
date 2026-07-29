namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] What a prompt's caller has to do next. Replaces the project-wide <c>"__EXIT__"</c> /
/// <c>"__CHANGED_KEY__"</c> magic strings (review finding F2) and makes a "back" option expressible
/// at every prompt without touching those call sites a second time (F11).
///
/// <para><see cref="Restart"/> is the old <c>__CHANGED_KEY__</c>: the user changed something global
/// mid-prompt (the API key profile), so the enclosing setup flow must start over from its first
/// step rather than continue with values derived from the old state.</para>
/// [Human] Sagt dem Aufrufer, was als Nächstes zu tun ist: Wert übernehmen, einen Schritt zurück,
/// abbrechen, oder den ganzen Setup-Ablauf neu starten.
/// </summary>
public enum PromptOutcome {
    /// <summary>The user chose a value; <c>PromptResult&lt;T&gt;.Value</c> is set.</summary>
    Value,

    /// <summary>The user wants to step back one level. The caller re-asks the previous prompt.</summary>
    Back,

    /// <summary>The user wants to leave the whole flow. The caller unwinds to the main menu.</summary>
    Exit,

    /// <summary>Global state changed mid-prompt; the caller restarts its setup flow from step one.</summary>
    Restart
}

/// <summary>
/// [AI Context] The single return shape for every interactive prompt in the app. Callers switch on
/// <see cref="Outcome"/> instead of comparing against sentinel strings, so "user picked something",
/// "user wants to go back" and "user wants out" are three distinct, un-confusable cases.
///
/// <para><see cref="Value"/> is only meaningful when <see cref="Outcome"/> is
/// <see cref="PromptOutcome.Value"/>; use <see cref="Or"/> when the sensible fallback is the
/// current setting.</para>
/// [Human] Einheitlicher Rückgabewert aller Menüs: entweder ein Wert, oder "zurück" / "abbrechen" /
/// "neu starten".
/// </summary>
public readonly record struct PromptResult<T>(PromptOutcome Outcome, T? Value) {
    public bool IsValue => Outcome == PromptOutcome.Value;
    public bool IsBack => Outcome == PromptOutcome.Back;
    public bool IsExit => Outcome == PromptOutcome.Exit;
    public bool IsRestart => Outcome == PromptOutcome.Restart;

    /// <summary>
    /// [AI Context] The chosen value, or <paramref name="fallback"/> for every non-value outcome.
    /// For prompts whose "back" means "keep what is configured" this collapses the switch to one
    /// expression - but only use it where back and cancel genuinely mean the same thing.
    /// [Human] Liefert den gewählten Wert oder den Rückfallwert, wenn abgebrochen wurde.
    /// </summary>
    public T Or(T fallback) => IsValue && Value is not null ? Value : fallback;
}

/// <summary>
/// [AI Context] Factories for <see cref="PromptResult{T}"/>, separate so the generic argument can be
/// inferred at the call site (<c>PromptResult.FromValue(profile)</c> rather than
/// <c>new PromptResult&lt;int&gt;(PromptOutcome.Value, profile)</c>).
/// [Human] Kurzschreibweisen zum Erzeugen eines Prompt-Ergebnisses.
/// </summary>
public static class PromptResult {
    public static PromptResult<T> FromValue<T>(T value) => new(PromptOutcome.Value, value);
    public static PromptResult<T> Back<T>() => new(PromptOutcome.Back, default);
    public static PromptResult<T> Exit<T>() => new(PromptOutcome.Exit, default);
    public static PromptResult<T> Restart<T>() => new(PromptOutcome.Restart, default);
}
