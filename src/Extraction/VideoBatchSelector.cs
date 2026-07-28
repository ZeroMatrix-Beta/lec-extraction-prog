using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Media;
using Spectre.Console;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Finds all MP4 videos in the source folder, sorts them chronologically, and lets the
/// user choose what to process: everything, everything from a chosen video onwards, or an
/// explicitly ticked subset.
///
/// <para>The subset mode exists because the "start at video N" model only covers a suffix of the
/// batch. Re-running two failed lectures out of forty previously meant either reprocessing
/// everything after the earlier one or moving files around on disk.</para>
/// [Human] Sucht alle MP4-Dateien, sortiert sie chronologisch und lässt wählen: alle, ab einem
/// bestimmten Video, oder einzeln ausgewählte.
/// </summary>
public static class VideoBatchSelector {
    public static string[] SelectAndFilterVideosForBatch(string sourceFolder) {
        if (!Directory.Exists(sourceFolder)) {
            Ui.Error($"Der Ordner '{sourceFolder}' existiert nicht.");
            return [];
        }

        var files = Directory.GetFiles(sourceFolder, "*.mp4")
                             .OrderBy(f => VideoDateParser.Parse(f).Date)
                             .ThenBy(f => VideoDateParser.Parse(f).WeekNumber ?? int.MaxValue)
                             .ThenBy(f => f)
                             .ToArray();

        if (files.Length == 0) {
            Ui.Info($"Keine MP4-Videos im Ordner '{sourceFolder}' gefunden.");
            return [];
        }

        Ui.Info($"Es wurden {files.Length} MP4-Video(s) im Quellordner gefunden.");

        const string optAll = "Alle Videos verarbeiten";
        const string optFrom = "Ab einem bestimmten Video starten";
        const string optPick = "Einzelne Videos auswählen";

        string mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]Was soll verarbeitet werden?[/] [grey]({files.Length} Video(s))[/]")
                .AddChoices(optAll, optFrom, optPick));

        return mode switch {
            optFrom => SelectFromStartingVideo(files),
            optPick => SelectIndividualVideos(files),
            _ => files
        };
    }

    private static string[] SelectFromStartingVideo(string[] files) {
        var labels = BuildLabels(files);

        string selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]Bei welchem Video soll gestartet werden?[/]")
                .PageSize(15)
                .MoreChoicesText("[grey](Pfeiltasten für weitere Videos)[/]")
                .AddChoices(labels));

        int startIndex = Array.IndexOf(labels, selected);
        if (startIndex < 0) return files;

        Ui.Info($"Starte Batch-Verarbeitung ab Video {startIndex + 1}: {Path.GetFileName(files[startIndex])}");
        return [.. files.Skip(startIndex)];
    }

    private static string[] SelectIndividualVideos(string[] files) {
        var labels = BuildLabels(files);

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[bold]Welche Videos sollen verarbeitet werden?[/]")
                .PageSize(15)
                .NotRequired()
                .MoreChoicesText("[grey](Pfeiltasten für weitere Videos)[/]")
                .InstructionsText("[grey](Leertaste zum Auswählen, Enter zum Bestätigen)[/]")
                .AddChoices(labels));

        if (selected.Count == 0) {
            Ui.Warn("Keine Videos ausgewählt. Verarbeitung wird übersprungen.");
            return [];
        }

        // Keep the chronological order of the source list rather than the order the user ticked
        // them in - later parts reference earlier ones, so sequence matters downstream.
        var chosen = labels.Select((label, index) => (label, index))
                           .Where(x => selected.Contains(x.label))
                           .Select(x => files[x.index])
                           .ToArray();

        Ui.Info($"{chosen.Length} Video(s) ausgewählt.");
        foreach (string file in chosen) {
            Ui.Detail($"- {Path.GetFileName(file)}");
        }
        return chosen;
    }

    /// <summary>
    /// [AI Context] Numbered labels carrying the parsed lecture date, so the list can be scanned by
    /// date rather than by long, near-identical filenames. The label doubles as the prompt's
    /// identity, so it must stay unique - the leading index guarantees that even if two videos
    /// parse to the same date.
    ///
    /// <para>Everything here goes through <see cref="Markup.Escape"/>: Spectre renders prompt
    /// choices as markup, and both halves of the label are attacker-of-convenience input - the
    /// date context is wrapped in square brackets, and a filename may contain them too. An
    /// unescaped "[" either throws or silently swallows the rest of the line.</para>
    /// [Human] Beschriftung mit Nummer und erkanntem Vorlesungsdatum. Alles wird escaped, weil
    /// Spectre eckige Klammern als Markup interpretiert.
    /// </summary>
    private static string[] BuildLabels(string[] files) {
        var labels = new List<string>(files.Length);
        for (int i = 0; i < files.Length; i++) {
            string context = VideoDateParser.Parse(files[i]).GetFormattedContext();
            string name = Path.GetFileName(files[i]);
            string raw = string.IsNullOrWhiteSpace(context) ? $"{i + 1}) {name}" : $"{i + 1}) {name}  [{context}]";
            labels.Add(Markup.Escape(raw));
        }
        return [.. labels];
    }
}
