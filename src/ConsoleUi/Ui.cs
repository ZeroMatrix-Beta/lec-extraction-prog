using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Unified Spectre.Console UI abstraction ensuring markup escaping, canonical severity tags,
/// and unstyled raw output for model/LaTeX streaming.
/// [Human] Zentrale Console-UI-Klasse für gerahmte Ausgaben, Spectre-Prompts und Markup-sicheres Logging.
/// </summary>
public static class Ui {
    private static volatile bool _isLineOpen = false;

    /// <summary>
    /// [AI Context] True when Spectre writes to a console window rather than a file or pipe. Frames
    /// fall back to ASCII otherwise: the output is UTF-8 either way, but a log is often read back as
    /// Windows-1252, where every "─" turns into "â”€" and a rule fills a whole line with it.
    /// [Human] Ob die Ausgabe in einem Konsolenfenster landet (sonst Rahmen nur aus ASCII).
    /// </summary>
    public static bool IsTerminalOutput => AnsiConsole.Profile.Out.IsTerminal;

    /// <summary>
    /// [AI Context] True when Spectre can redraw a line in place, which its live displays (Status)
    /// need. Spectre calls a console non-interactive as soon as any of stdin, stdout or stderr is
    /// redirected - even if the display itself is a real window - and then prints every update of a
    /// live display as a new line.
    /// [Human] Ob eine Zeile an Ort und Stelle aktualisiert werden kann (Countdown statt Zeilenflut).
    /// </summary>
    public static bool CanRedrawInPlace => AnsiConsole.Profile.Capabilities is { Interactive: true, Ansi: true };

    private static void EnsureNewLine() {
        if (_isLineOpen) {
            _isLineOpen = false;
            AnsiConsole.WriteLine();
        }
    }

    // Severity — canonical German tag + colour, markup-escaped
    public static void Info(string msg, string? scope = null) {
        EnsureNewLine();
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[blue][[INFO]][/] {text}");
    }

    public static void Warn(string msg, string? scope = null) {
        EnsureNewLine();
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[yellow][[WARNUNG]][/] {text}");
    }

    public static void Error(string msg, string? scope = null) {
        EnsureNewLine();
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[red][[FEHLER]][/] {text}");
    }

    public static void Success(string msg, string? scope = null) {
        EnsureNewLine();
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[green][[OK]][/] {text}");
    }

    // Structure
    /// <summary>
    /// [AI Context] Section divider. <paramref name="scope"/> names the subsystem the step belongs
    /// to, matching the Info/Warn/Error/Success convention, so a caller can write
    /// <c>Ui.Step("Starte Reparatur-Runde 2 von 3...", "AI PDF Fix Loop")</c>.
    /// [Human] Abschnitts-Trenner mit optionaler Subsystem-Angabe.
    /// </summary>
    public static void Step(string title, string? scope = null) {
        EnsureNewLine();
        string text = Markup.Escape(title);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]][/] " : "";
        var rule = new Rule($"{prefix}[bold cyan]{text}[/]").LeftJustified();
        if (!IsTerminalOutput) {
            rule.Border = BoxBorder.Ascii;
        }
        AnsiConsole.Write(rule);
    }

    /// <summary>
    /// [AI Context] Dim, indented secondary output - the default level for the chatter that used to
    /// be spelled <c>[INFO]</c>. Takes the same optional <paramref name="scope"/> as the severity
    /// helpers.
    /// [Human] Gedimmte Detailausgabe mit optionaler Subsystem-Angabe.
    /// </summary>
    public static void Detail(string msg, string? scope = null) {
        EnsureNewLine();
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]][/] " : "";
        AnsiConsole.MarkupLine($"  {prefix}[grey]{text}[/]");
    }

    /// <summary>
    /// [AI Context] Framed banner for the top of a session or mode, replacing the old
    /// <c>====...====</c> blocks. Distinct from <see cref="Step"/>, which divides sections inside
    /// a running session.
    /// [Human] Gerahmte Überschrift für den Start einer Session oder eines Modus.
    /// </summary>
    public static void Header(string title) {
        EnsureNewLine();
        var panel = new Panel($"[bold]{Markup.Escape(title)}[/]")
            .Border(IsTerminalOutput ? BoxBorder.Rounded : BoxBorder.Ascii)
            .BorderColor(Color.Cyan1)
            .Expand();
        AnsiConsole.Write(panel);
    }

    public static void Blank() {
        _isLineOpen = false;
        AnsiConsole.WriteLine();
    }

    // Verbatim — NO markup parsing, for model output and LaTeX
    public static void Raw(string text) {
        if (!string.IsNullOrEmpty(text)) {
            _isLineOpen = !text.EndsWith('\n');
            AnsiConsole.Write(new Text(text));
        }
    }

    public static void RawLine(string text = "") {
        _isLineOpen = false;
        AnsiConsole.Write(new Text(text + "\n"));
    }

    // Input
    /// <summary>
    /// Where prompt answers come from. Defaults to the keyboard, so the interactive app behaves
    /// exactly as before; the CLI swaps in <see cref="PresetPromptSource"/> at start-up. Assigning
    /// this is the whole of "run headlessly" - no call site below knows which source is installed.
    /// </summary>
    public static IPromptSource PromptSource { get; set; } = new InteractivePromptSource();

    /// <summary>
    /// [AI Context] Yes/no as an arrow-key toggle rather than a typed character.
    /// <c>AnsiConsole.Confirm</c> renders "[y/n]" and waits for a keystroke, which inherits the
    /// exact footgun Phase 8 step 3 removed: an unexpected key is silently read as "no", and on a
    /// German keyboard "j" - the obvious answer to a German question - is not the accepted key at
    /// all. A two-choice <see cref="SelectionPrompt{T}"/> makes the current answer visible, moves
    /// with the arrow keys, and cannot be answered by accident.
    /// [Human] Ja/Nein zum Durchschalten mit den Pfeiltasten statt Tastendruck - die aktuelle
    /// Auswahl ist sichtbar und eine falsche Taste kann nichts auslösen.
    /// </summary>
    public static bool Confirm(string question, bool defaultYes = true) => PromptSource.Confirm(question, defaultYes);

    /// <summary>
    /// [AI Context] The two navigation entries every menu can carry. They are constants rather than
    /// literals at the call sites so the wording, the icon and the ordering are identical in all
    /// menus - a "back" option that is spelled differently in each menu is the failure mode this
    /// whole abstraction exists to prevent.
    /// [Human] Einheitliche Beschriftung für "zurück" und "abbrechen" in allen Menüs.
    /// </summary>
    public const string BackChoiceLabel = "↩ Zurück";
    public const string ExitChoiceLabel = "🚪 Abbrechen";

    /// <summary>
    /// [AI Context] Carries a choice's label, its payload and the navigation meaning of picking it.
    /// The prompt is built over this type rather than over <c>string</c> so that labels never have
    /// to be unique, never have to be parsed back into a value (the old
    /// <c>selection.StartsWith("2)")</c> pattern), and are escaped in exactly one place - the
    /// converter below.
    /// [Human] Interner Menüeintrag: Beschriftung, Wert und Bedeutung (Wert / zurück / abbrechen).
    /// </summary>
    private sealed class SelectItem<T>(string label, T? value, PromptOutcome outcome) {
        public string Label { get; } = label;
        public T? Value { get; } = value;
        public PromptOutcome Outcome { get; } = outcome;
    }

    /// <summary>
    /// [AI Context] The single menu primitive. Renders <paramref name="choices"/> plus, optionally,
    /// a back and/or cancel entry, and reports which of the three the user picked via
    /// <see cref="PromptResult{T}"/>.
    ///
    /// <para>Labels are passed unescaped and escaped here; do not pre-escape them, or square
    /// brackets show up doubled.</para>
    /// [Human] Zentrales Auswahlmenü: zeigt die Einträge plus optional "Zurück"/"Abbrechen" und
    /// meldet, was gewählt wurde.
    /// </summary>
    public static PromptResult<T> Select<T>(
        string title,
        IEnumerable<(string Label, T Value)> choices,
        bool allowBack = true,
        bool allowExit = false,
        string backLabel = BackChoiceLabel,
        string exitLabel = ExitChoiceLabel,
        int pageSize = 15,
        string? moreChoicesText = null) {

        var items = new List<SelectItem<T>>();
        foreach (var (label, value) in choices) {
            items.Add(new SelectItem<T>(label, value, PromptOutcome.Value));
        }
        if (allowBack) {
            items.Add(new SelectItem<T>(backLabel, default, PromptOutcome.Back));
        }
        if (allowExit) {
            items.Add(new SelectItem<T>(exitLabel, default, PromptOutcome.Exit));
        }

        int chosen = PromptSource.SelectIndex(title, [.. items.Select(item => item.Label)], pageSize, moreChoicesText);
        var selected = items[chosen];
        return new PromptResult<T>(selected.Outcome, selected.Value);
    }

    /// <summary>
    /// [AI Context] <see cref="Select{T}"/> for the common case where the label *is* the value.
    /// [Human] Auswahlmenü, bei dem die Beschriftung selbst der Wert ist.
    /// </summary>
    public static PromptResult<string> Select(
        string title,
        IEnumerable<string> choices,
        bool allowBack = true,
        bool allowExit = false,
        string backLabel = BackChoiceLabel,
        string exitLabel = ExitChoiceLabel,
        int pageSize = 15,
        string? moreChoicesText = null)
        => Select(title, choices.Select(c => (c, c)), allowBack, allowExit, backLabel, exitLabel, pageSize, moreChoicesText);

    /// <summary>
    /// Multi-select over labelled values, returning the picks in source order rather than tick
    /// order. An empty result means the user ticked nothing, which every caller reads as "back" -
    /// Spectre has no multi-select with a back entry.
    /// </summary>
    public static IReadOnlyList<T> SelectMany<T>(
        string title,
        IEnumerable<(string Label, T Value)> choices,
        int pageSize = 15,
        string? moreChoicesText = null,
        string? instructionsText = null) {

        var items = choices.ToList();
        var picked = PromptSource.SelectManyIndices(title, [.. items.Select(item => item.Label)], pageSize, moreChoicesText, instructionsText);
        return [.. picked.Select(index => items[index].Value)];
    }

    /// <summary>
    /// [AI Context] <see cref="Confirm"/> with a third way out, for yes/no questions that sit in the
    /// middle of a multi-step setup. Without it a confirm is a one-way door in an otherwise
    /// back-navigable flow.
    /// [Human] Ja/Nein-Frage mit zusätzlicher "Zurück"-Option.
    /// </summary>
    public static PromptResult<bool> ConfirmOrBack(string question, bool defaultYes = true) {
        var choices = defaultYes
            ? new[] { ("Ja", true), ("Nein", false) }
            : new[] { ("Nein", false), ("Ja", true) };

        return Select(question, choices, allowBack: true);
    }

    /// <summary>
    /// [AI Context] Free-text input. Wraps <c>AnsiConsole.Ask</c> so the question is markup-escaped
    /// at one place - an unescaped path or filename in a question would otherwise be parsed as
    /// markup and either throw or vanish.
    /// [Human] Freitext-Eingabe mit markup-sicherer Frage.
    /// </summary>
    public static string Ask(string question, string? defaultValue = null) => PromptSource.AskText(question, defaultValue);

    /// <summary>
    /// [AI Context] Typed free-text input with a default. The escaping is the point: several of
    /// these questions end in "[aktuell: 1.2x]", which <c>AnsiConsole.Ask</c> parses as a style tag
    /// and throws on - the same crash class as the invalid "text-primary" style fixed earlier.
    /// [Human] Eingabe mit Standardwert; die Frage wird escaped, damit eckige Klammern nicht als
    /// Formatierung interpretiert werden (das führte zu Abstürzen).
    /// </summary>
    public static T Ask<T>(string question, T defaultValue) => PromptSource.AskValue(question, defaultValue);

    // Data
    public static void Table(string title, IEnumerable<(string Key, string Value)> rows) {
        var table = new Table().Border(IsTerminalOutput ? TableBorder.Rounded : TableBorder.Ascii);
        table.AddColumn("[bold]Eigenschaft[/]");
        table.AddColumn("[bold]Wert[/]");
        if (!string.IsNullOrEmpty(title)) {
            table.Title(Markup.Escape(title));
        }
        foreach (var (key, value) in rows) {
            table.AddRow(Markup.Escape(key), Markup.Escape(value));
        }
        AnsiConsole.Write(table);
    }
}
