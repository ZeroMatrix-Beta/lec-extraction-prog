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

        // The mode question and the list that follows it form a two-step loop: backing out of the
        // list returns here rather than cancelling the whole batch.
        while (true) {
            var mode = Ui.Select($"Was soll verarbeitet werden? ({files.Length} Video(s))", [
                ("Alle Videos verarbeiten", BatchMode.All),
                ("Ab einem bestimmten Video starten", BatchMode.FromVideo),
                ("Einzelne Videos auswählen", BatchMode.Individual)
            ]);

            if (!mode.IsValue) return [];

            var selected = mode.Value switch {
                BatchMode.FromVideo => SelectFromStartingVideo(files),
                BatchMode.Individual => SelectIndividualVideos(files),
                _ => PromptResult.FromValue(files)
            };

            if (selected.IsBack) continue;
            if (!selected.IsValue) return [];
            return selected.Value!;
        }
    }

    private enum BatchMode { All, FromVideo, Individual }

    private static PromptResult<string[]> SelectFromStartingVideo(string[] files) {
        var choices = BuildLabels(files).Select((label, index) => (label, index));

        var selected = Ui.Select("Bei welchem Video soll gestartet werden?", choices,
            pageSize: 15, moreChoicesText: "(Pfeiltasten für weitere Videos)");

        if (!selected.IsValue) return new PromptResult<string[]>(selected.Outcome, null);

        int startIndex = selected.Value;
        Ui.Info($"Starte Batch-Verarbeitung ab Video {startIndex + 1}: {Path.GetFileName(files[startIndex])}");
        return PromptResult.FromValue<string[]>([.. files.Skip(startIndex)]);
    }

    private static PromptResult<string[]> SelectIndividualVideos(string[] files) {
        // Spectre has no multi-select with a "back" entry, so an empty confirmation is read as
        // "back": ticking nothing and pressing Enter is what a user does when they want out.
        var labels = BuildLabels(files).Select(Markup.Escape).ToArray();

        var selected = AnsiConsole.Prompt(
            new MultiSelectionPrompt<string>()
                .Title("[bold]Welche Videos sollen verarbeitet werden?[/]")
                .PageSize(15)
                .NotRequired()
                .MoreChoicesText("[grey](Pfeiltasten für weitere Videos)[/]")
                .InstructionsText("[grey](Leertaste zum Auswählen, Enter zum Bestätigen - nichts auswählen führt zurück)[/]")
                .AddChoices(labels));

        if (selected.Count == 0) {
            Ui.Warn("Keine Videos ausgewählt.");
            return PromptResult.Back<string[]>();
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
        return PromptResult.FromValue(chosen);
    }

    /// <summary>
    /// [AI Context] Numbered labels carrying the parsed lecture date, so the list can be scanned by
    /// date rather than by long, near-identical filenames. The label doubles as the prompt's
    /// identity, so it must stay unique - the leading index guarantees that even if two videos
    /// parse to the same date.
    ///
    /// <para>Labels are returned <em>unescaped</em>: <see cref="Ui.Select{T}"/> escapes in its
    /// converter, so escaping here too would render the brackets doubled. The one caller that talks
    /// to Spectre directly - the multi-select above - escapes them itself. That still matters: the
    /// date context is wrapped in square brackets and a filename may contain them too, and an
    /// unescaped "[" either throws or silently swallows the rest of the line.</para>
    /// [Human] Beschriftung mit Nummer und erkanntem Vorlesungsdatum, unescaped - das Escaping
    /// passiert beim Anzeigen.
    /// </summary>
    private static string[] BuildLabels(string[] files) {
        var labels = new List<string>(files.Length);
        for (int i = 0; i < files.Length; i++) {
            string context = VideoDateParser.Parse(files[i]).GetFormattedContext();
            string name = Path.GetFileName(files[i]);
            labels.Add(string.IsNullOrWhiteSpace(context) ? $"{i + 1}) {name}" : $"{i + 1}) {name}  [{context}]");
        }
        return [.. labels];
    }
}
