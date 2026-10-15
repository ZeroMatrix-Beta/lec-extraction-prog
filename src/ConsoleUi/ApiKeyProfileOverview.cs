using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;
using LectureExtraction.Configuration;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] Renders a detailed Spectre.Console overview of configured Google AI Studio API-key profiles,
/// their active session mappings, resolution status, and daily Free-Tier quota status per model.
/// [Human] Erstellt eine übersichtliche Tabelle aller API-Key Profile mit aktuellen Kontingenten (gemini-3.8-flash, gemini-3.7-flash)
/// und zeigt an, welcher Key in welcher Sitzung (Extraktion, Refinement, Chat) aktiv ist.
/// </summary>
public static class ApiKeyProfileOverview {
    public record ProfileOverviewItem {
        public int ProfileIndex { get; init; }
        public string EnvName { get; init; } = "";
        public bool IsKeyPresent { get; init; }
        public List<string> ActiveInSessions { get; init; } = [];
        public string Purpose { get; init; } = "";
        public string StatusModel38 { get; init; } = "";
        public string StatusModel37 { get; init; } = "";
        public string? Note { get; init; }
    }

    public static List<ProfileOverviewItem> GetProfilesData() {
        var autoExtConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
        var chatConfig = ConfigLoader<DirectAiChatSessionAiStudioConfig>.Load();
        var latexConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();

        var envNames = autoExtConfig.AiStudioApiKeyEnvNames;
        int maxProfiles = Math.Max(envNames.Length, 5);
        var items = new List<ProfileOverviewItem>();

        // Each session has its own key list, so its profile number means a row of that list, not of
        // this one; comparing numbers marked profile 0 as the refinement key while the refinement
        // actually used API_KEY-latex-refinement. Compare the key each session resolves instead.
        string extractionKey = ApiKeyProfileResolver.Resolve(autoExtConfig.ActiveApiProfile, envNames);
        string chatKey = ApiKeyProfileResolver.Resolve(chatConfig.ActiveApiProfile, chatConfig.AiStudioApiKeyEnvNames);
        string refinementKey = Extraction.RefinementUiHelper.ResolveRefinementKeyEnvName(latexConfig);

        for (int i = 0; i < maxProfiles; i++) {
            string envName = ApiKeyProfileResolver.Resolve(i, envNames);
            bool isPresent = GoogleAiClientBuilder.IsApiKeyPresent(envName);

            var activeIn = new List<string>();
            if (extractionKey == envName) activeIn.Add("🚀 Auto-Extraktion");
            if (chatKey == envName) activeIn.Add("🌐 Direct Chat");
            if (refinementKey == envName) activeIn.Add("✍️ LaTeX Refinement");

            string purpose = i switch {
                0 => "Dedizierter Extraktions-Key",
                1 => "Testprojekt 1 (Transkription)",
                2 => "Testprojekt 2 (Refinement)",
                3 => "Testprojekt 3 (Transkription)",
                4 => "Dedizierter LaTeX Refinement Key",
                _ => $"Profil {i}"
            };

            var (used38, limit38, exhausted38, note38) = ApiKeyQuotaTracker.GetStatus(envName, "gemini-3.8-flash");
            var (used37, limit37, exhausted37, note37) = ApiKeyQuotaTracker.GetStatus(envName, "gemini-3.7-flash");

            string status38 = exhausted38
                ? $"[red]🔴 Erschöpft ({used38}/{limit38})[/]"
                : (used38 > 0 ? $"[yellow]🟡 Aktiv ({used38}/{limit38})[/]" : "[green]🟢 Bereit (20/20)[/]");

            string status37 = exhausted37
                ? $"[red]🔴 Erschöpft ({used37}/{limit37})[/]"
                : (used37 > 0 ? $"[yellow]🟡 Aktiv ({used37}/{limit37})[/]" : "[green]🟢 Bereit (20/20)[/]");

            string? note = note38 ?? note37;

            items.Add(new() {
                ProfileIndex = i,
                EnvName = envName,
                IsKeyPresent = isPresent,
                ActiveInSessions = activeIn,
                Purpose = purpose,
                StatusModel38 = status38,
                StatusModel37 = status37,
                Note = note
            });
        }

        return items;
    }

    public static void Render() {
        var items = GetProfilesData();
        string timeUntilReset = ApiKeyQuotaTracker.FormatTimeUntilReset();

        var table = new Table().Border(Ui.IsTerminalOutput ? TableBorder.Rounded : TableBorder.Ascii);
        table.Title($"[bold cyan]Google AI Studio — API-Key Profile & Quota-Übersicht[/]\n[dim]Free Tier: 20 Anfragen / Tag pro Projekt pro Modell | Reset in {timeUntilReset} (02:00 Uhr MEZ / 00:00 UTC)[/]");

        table.AddColumn(new TableColumn("[bold]Profil[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Umgebungsvariable[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Key-Status[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Aktiv in Konfiguration[/]"));
        table.AddColumn(new TableColumn("[bold]gemini-3.8-flash[/]").Centered());
        table.AddColumn(new TableColumn("[bold]gemini-3.7-flash[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Zweck / Notiz[/]"));

        foreach (var item in items) {
            string profileCell = $"[bold]{item.ProfileIndex}[/]";
            string envCell = $"[cyan]{Markup.Escape(item.EnvName)}[/]";
            string keyStatusCell = item.IsKeyPresent ? "[green]✔ Vorhanden[/]" : "[red]✘ Fehlt[/]";

            string activeInCell = item.ActiveInSessions.Count > 0
                ? string.Join(", ", item.ActiveInSessions)
                : "[dim]—[/]";

            string noteCell = !string.IsNullOrWhiteSpace(item.Note)
                ? $"{item.Purpose} [dim]({Markup.Escape(item.Note)})[/]"
                : item.Purpose;

            table.AddRow(
                profileCell,
                envCell,
                keyStatusCell,
                activeInCell,
                item.StatusModel38,
                item.StatusModel37,
                noteCell
            );
        }

        AnsiConsole.Write(table);

        var rule = new Rule("[dim]💡 Free-Tier Hinweis: Quotas gelten strikt PRO MODELL. Ist 3.8 erschöpft, sofort mit gemini-3.7-flash weiterarbeiten![/]");
        rule.LeftJustified();
        AnsiConsole.Write(rule);
        Ui.Blank();
    }
}
