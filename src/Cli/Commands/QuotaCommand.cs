using System.CommandLine;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] CLI command 'lecx quota' (alias: 'profiles') to inspect Google AI Studio API-key profiles,
/// active session assignments, and daily Free-Tier quotas per model.
/// [Human] Zeigt eine detaillierte Übersicht aller konfigurierten API-Key Profile und ihrer Free-Tier Kontingente an.
/// </summary>
public static class QuotaCommand {
    public static Command Build() {
        var command = new Command("quota", "Inspect API-key profiles, active mappings, and daily Free-Tier quotas.") {
            Aliases = { "profiles" }
        };

        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var data = ApiKeyProfileOverview.GetProfilesData();

            CliOutput.Payload(context, data, ApiKeyProfileOverview.Render);
            return ExitCodes.Success;
        });

        return command;
    }
}
