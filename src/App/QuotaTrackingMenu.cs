using System;
using Spectre.Console;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Interactive submenu for inspecting and managing API-key profiles, daily Free-Tier quotas,
/// and model availability across Google AI Studio projects.
/// [Human] Interaktives Menü zur Anzeige und Verwaltung der API-Key Kontingente (Free-Tier 20 Req/Tag,
/// Countdown bis zum täglichen Reset um 02:00 Uhr und Modell-Wechsel).
/// </summary>
public static class QuotaTrackingMenu {
    public static void Show() {
        while (true) {
            ApiKeyProfileOverview.Render();

            Ui.Step("API-Key Kontingente & Quota-Tracking");

            string choice1 = "1) 🔄 Quota-Übersicht aktualisieren";
            string choice2 = "2) 🔑 Aktives Profil konfigurieren (Auto-Extraktion / Refinement / Chat)";
            string choice3 = "3) ℹ️ Free-Tier Details, Reset-Zeiten & Modell-Regeln";
            string choice4 = "4) 🧹 Quota-Status zurücksetzen (Manuell auf 'Bereit' setzen)";

            var selection = Ui.Select(
                "Was möchten Sie tun?",
                [choice1, choice2, choice3, choice4],
                backLabel: "🚪 Zurück zum Hauptmenü");

            if (!selection.IsValue) break;

            if (selection.Value == choice1) {
                // Loops and re-renders
                continue;
            }
            else if (selection.Value == choice2) {
                ApiKeyProfileMenu.Show();
            }
            else if (selection.Value == choice3) {
                ShowQuotaDetails();
            }
            else if (selection.Value == choice4) {
                if (Ui.Confirm("Möchten Sie alle aufgezeichneten Quota-Zähler für heute auf 'Bereit' zurücksetzen?", false)) {
                    ApiKeyQuotaTracker.ResetAll();
                    Ui.Success("Alle Quota-Einträge wurden erfolgreich zurückgesetzt!");
                }
            }
        }
    }

    private static void ShowQuotaDetails() {
        string timeUntilReset = ApiKeyQuotaTracker.FormatTimeUntilReset();

        var panel = new Panel(new Markup(
            "[bold cyan]Google AI Studio Free-Tier Kontingent-Regeln:[/]\n\n" +
            "• [bold]Tageslimit:[/] 20 Anfragen pro Tag [bold underline]pro Projekt pro Modell[/].\n" +
            $"• [bold]Täglicher Reset:[/] Jeden Tag um [bold yellow]00:00 UTC (02:00 Uhr MEZ)[/] — in [bold]{timeUntilReset}[/].\n" +
            "• [bold]Unabhängige Modell-Buckets:[/] [cyan]gemini-3.8-flash[/] und [cyan]gemini-3.7-flash[/] besitzen separate Kontingente!\n" +
            "  Wenn ein API-Key für 3.8 das 20er-Limit erreicht hat, kann mit [green]--model gemini-3.7-flash[/] sofort weitergearbeitet werden.\n" +
            "• [bold]Hohe Auslastung (503):[/] Wenn Google temporär 'high demand' meldet, wartet die Retry-Logik 64s und sendet automatisch erneut.\n" +
            "• [bold]Mehrere Keys:[/] Durch 5 konfigurierte Testprojekte stehen insgesamt bis zu [bold green]100 Anfragen / Tag pro Modell[/] bereit!"
        )) {
            Header = new PanelHeader("[bold yellow]ℹ️ Kontingent- & Reset-Informationen[/]"),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
        Ui.Blank();
        Ui.Info("Drücken Sie eine Taste oder wählen Sie eine Option...");
    }
}
