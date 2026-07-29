namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] The chat REPL's command vocabulary. <see cref="None"/> means "this input is a
/// prompt for the model", which is the common case.
/// [Human] Die Befehle der Chat-Schleife; None heisst "normale Eingabe an das Modell".
/// </summary>
public enum ChatCommandKind {
    None,
    Help,
    Clear,
    Exit,
    SetTemperature,
    SetMaxTokens,
    SetThinkingBudget,
    SetThinkingLevel,
    SetGrounding,
    SetModel,
    ChangeApiKeyProfile,
    Attach
}

/// <summary>
/// [AI Context] One parsed chat command: what the user asked for, the validated argument, and - when
/// the argument was rejected - the message to show them.
///
/// <para>Parsing is separated from applying because the two were fused: every handler recognised its
/// command, validated the argument, mutated session state and printed, in one method. That made the
/// command surface untestable without constructing a live chat session, which is how
/// <c>set thinking-budget</c> and <c>set thinking-level</c> shipped permanently broken - see
/// <see cref="ChatCommandParser"/>.</para>
/// [Human] Ein geparster Befehl mit geprüftem Argument. Das Anwenden bleibt beim Aufrufer.
/// </summary>
public sealed record ChatCommand(ChatCommandKind Kind) {
    /// <summary>German error message when the command was recognised but its argument was invalid.</summary>
    public string? Error { get; init; }

    /// <summary>Argument of <see cref="ChatCommandKind.SetTemperature"/>.</summary>
    public float Number { get; init; }

    /// <summary>Argument of SetMaxTokens, SetThinkingBudget and ChangeApiKeyProfile.</summary>
    public int Integer { get; init; }

    /// <summary>Argument of <see cref="ChatCommandKind.SetGrounding"/>.</summary>
    public bool Flag { get; init; }

    /// <summary>Argument of SetThinkingLevel, SetModel and Attach (the raw remainder).</summary>
    public string Text { get; init; } = "";

    /// <summary>True when the command was recognised and its argument accepted.</summary>
    public bool IsValid => Error == null;

    public static readonly ChatCommand NotACommand = new(ChatCommandKind.None);
}
