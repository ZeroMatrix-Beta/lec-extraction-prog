using System;
using System.Collections.Generic;
using Spectre.Console;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// The keyboard. This is the behaviour the app has always had, moved behind
/// <see cref="IPromptSource"/> unchanged - every escaping rule and prompt shape below is the one
/// that shipped, so switching sources cannot alter what the interactive user sees.
/// </summary>
public sealed class InteractivePromptSource : IPromptSource {
    public int SelectIndex(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText) {
        // Indices are carried as the choice value so that duplicate labels stay distinguishable -
        // the reason the menu primitive stopped being a prompt over bare strings in the first place.
        var indices = new List<int>(labels.Count);
        for (int i = 0; i < labels.Count; i++) {
            indices.Add(i);
        }

        var prompt = new SelectionPrompt<int>()
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .PageSize(Math.Max(3, pageSize))
            .UseConverter(index => Markup.Escape(labels[index]))
            .AddChoices(indices);

        if (moreChoicesText != null) {
            prompt.MoreChoicesText($"[grey]{Markup.Escape(moreChoicesText)}[/]");
        }

        return AnsiConsole.Prompt(prompt);
    }

    public IReadOnlyList<int> SelectManyIndices(string title, IReadOnlyList<string> labels, int pageSize, string? moreChoicesText, string? instructionsText) {
        var indices = new List<int>(labels.Count);
        for (int i = 0; i < labels.Count; i++) {
            indices.Add(i);
        }

        var prompt = new MultiSelectionPrompt<int>()
            .Title($"[bold]{Markup.Escape(title)}[/]")
            .PageSize(Math.Max(3, pageSize))
            .NotRequired()
            .UseConverter(index => Markup.Escape(labels[index]))
            .AddChoices(indices);

        if (moreChoicesText != null) {
            prompt.MoreChoicesText($"[grey]{Markup.Escape(moreChoicesText)}[/]");
        }
        if (instructionsText != null) {
            prompt.InstructionsText($"[grey]{Markup.Escape(instructionsText)}[/]");
        }

        // Spectre returns them in tick order; ascending index order is what callers mean by
        // "keep the source order", so it is normalised here rather than at each call site.
        var selected = AnsiConsole.Prompt(prompt);
        selected.Sort();
        return selected;
    }

    public bool Confirm(string question, bool defaultYes) {
        const string yes = "Ja";
        const string no = "Nein";

        var prompt = new SelectionPrompt<string>()
            .Title($"[bold]{Markup.Escape(question)}[/]")
            .AddChoices(defaultYes ? [yes, no] : [no, yes]);

        return AnsiConsole.Prompt(prompt) == yes;
    }

    public string AskText(string question, string? defaultValue) {
        var prompt = new TextPrompt<string>($"[bold]{Markup.Escape(question)}[/]");
        if (defaultValue != null) {
            prompt.DefaultValue(defaultValue);
        }
        return AnsiConsole.Prompt(prompt);
    }

    public T AskValue<T>(string question, T defaultValue) {
        var prompt = new TextPrompt<T>($"[bold]{Markup.Escape(question)}[/]").DefaultValue(defaultValue);
        return AnsiConsole.Prompt(prompt);
    }
}
