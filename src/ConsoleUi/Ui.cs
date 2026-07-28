using System;
using System.Collections.Generic;
using Spectre.Console;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Unified Spectre.Console UI abstraction ensuring markup escaping, canonical severity tags,
/// and unstyled raw output for model/LaTeX streaming.
/// [Human] Zentrale Console-UI-Klasse für gerahmte Ausgaben, Spectre-Prompts und Markup-sicheres Logging.
/// </summary>
public static class Ui {
    // Severity — canonical German tag + colour, markup-escaped
    public static void Info(string msg, string? scope = null) {
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[blue][[INFO]][/] {text}");
    }

    public static void Warn(string msg, string? scope = null) {
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[yellow][[WARNUNG]][/] {text}");
    }

    public static void Error(string msg, string? scope = null) {
        string text = Markup.Escape(msg);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]] [/]" : "";
        AnsiConsole.MarkupLine($"{prefix}[red][[FEHLER]][/] {text}");
    }

    public static void Success(string msg, string? scope = null) {
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
        string text = Markup.Escape(title);
        string prefix = scope != null ? $"[silver][[{Markup.Escape(scope)}]][/] " : "";
        AnsiConsole.Write(new Rule($"{prefix}[bold cyan]{text}[/]").LeftJustified());
    }

    /// <summary>
    /// [AI Context] Dim, indented secondary output - the default level for the chatter that used to
    /// be spelled <c>[INFO]</c>. Takes the same optional <paramref name="scope"/> as the severity
    /// helpers.
    /// [Human] Gedimmte Detailausgabe mit optionaler Subsystem-Angabe.
    /// </summary>
    public static void Detail(string msg, string? scope = null) {
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
        var panel = new Panel($"[bold]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Expand();
        AnsiConsole.Write(panel);
    }

    public static void Blank() {
        AnsiConsole.WriteLine();
    }

    // Verbatim — NO markup parsing, for model output and LaTeX
    public static void Raw(string text) {
        AnsiConsole.Write(new Text(text));
    }

    public static void RawLine(string text = "") {
        AnsiConsole.Write(new Text(text + "\n"));
    }

    // Data
    public static void Table(string title, IEnumerable<(string Key, string Value)> rows) {
        var table = new Table().Border(TableBorder.Rounded);
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
