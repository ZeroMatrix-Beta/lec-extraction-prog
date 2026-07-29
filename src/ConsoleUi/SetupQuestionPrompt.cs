using System;
using System.Collections.Generic;

namespace LectureExtraction.ConsoleUi;

/// <summary>
/// [AI Context] The yes/no questions in a session's setup phase, which need more than yes and no:
/// stepping back to the previous setup step, leaving the session entirely, and - where the caller
/// supports it - switching the API key profile before answering.
///
/// <para>This replaces the typed <c>PromptWithCommands</c> in both chat sessions, where those extra
/// answers were reachable only by typing <c>exit</c> or <c>change-key 2</c> at a <c>(j/n)</c>
/// prompt that advertised neither. The options are now visible entries in the same arrow-key menu
/// as the rest of the app.</para>
/// [Human] Ja/Nein-Frage im Setup, zusätzlich mit "Zurück", "Abbrechen" und optional
/// "API-Key Profil wechseln" - vorher nur durch Eintippen versteckter Befehle erreichbar.
/// </summary>
public static class SetupQuestionPrompt {
    private enum Answer { Yes, No, ChangeApiKeyProfile }

    /// <summary>
    /// [AI Context] Asks <paramref name="question"/>. Returns <see cref="PromptOutcome.Restart"/>
    /// after the API key profile was changed - the caller's setup flow has to start over, because
    /// everything it derived from the old profile (the client, the resolved key) is stale. That is
    /// the old <c>"__CHANGED_KEY__"</c> sentinel, now typed.
    /// [Human] Stellt die Frage; nach einem Profilwechsel muss das Setup neu beginnen.
    /// </summary>
    public static PromptResult<bool> Ask(string question, Action? onChangeApiKeyProfile = null) {
        var choices = new List<(string Label, Answer Value)> {
            ("Ja", Answer.Yes),
            ("Nein", Answer.No)
        };
        if (onChangeApiKeyProfile != null) {
            choices.Add(("🔑 API-Key Profil wechseln", Answer.ChangeApiKeyProfile));
        }

        var choice = Ui.Select(question, choices, allowBack: true, allowExit: true);
        if (!choice.IsValue) {
            return new PromptResult<bool>(choice.Outcome, false);
        }

        if (choice.Value == Answer.ChangeApiKeyProfile) {
            onChangeApiKeyProfile!.Invoke();
            return PromptResult.Restart<bool>();
        }

        return PromptResult.FromValue(choice.Value == Answer.Yes);
    }
}
