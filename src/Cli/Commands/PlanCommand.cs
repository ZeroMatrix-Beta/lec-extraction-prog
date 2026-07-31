using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The command to run first. It reports what a run would do - which videos, how many segments, how
/// many billable requests, under which model and key profile - and calls nothing. A wrong
/// <c>--folder</c>, an unset API key or a stale resume window is far cheaper to find here than
/// halfway through a paid batch.
/// </summary>
public static class PlanCommand {
    public static readonly Option<string?> Video = new("--video") {
        Description = "A single video file to work on."
    };

    public static readonly Option<string?> Folder = new("--folder") {
        Description = "Work on every .mp4 in this folder. Defaults to the configured source folder."
    };

    public static readonly Option<string?> From = new("--from") {
        Description = "Skip videos before this one (matched on the file name), keeping chronological order."
    };

    public static readonly Option<double?> ResumeWindow = new("--resume-window") {
        Description = "Hours a finished .tex part stays reusable. Default 2, matching the pipeline's own constant."
    };

    public static readonly Option<bool> Force = new("--force") {
        Description = "Ignore existing .tex parts and re-request every segment."
    };

    public static Command Build() {
        var command = new Command("plan", "Report what a run would do - videos, segments, requests - without calling the API.") {
            Video, Folder, From, ResumeWindow, Force,
            ExtractionOptions.Out, ExtractionOptions.Parts, ExtractionOptions.Overlap,
            ExtractionOptions.Speed, ExtractionOptions.Model, ExtractionOptions.Profile
        };

        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            if (!TryResolveVideos(parseResult, config, out var videos, out int failure)) {
                return failure;
            }

            var plan = ExtractionPlanner.Build(
                config,
                videos,
                parseResult.GetValue(ResumeWindow) ?? ExtractionPlanner.DefaultResumeWindowHours,
                parseResult.GetValue(Force));

            CliOutput.Payload(context, plan, () => Render(plan));

            // A plan whose key does not resolve is still a valid plan, but the run it describes
            // cannot happen - so it reports the configuration problem rather than success.
            return plan.ApiKeyResolves ? ExitCodes.Success : ExitCodes.Configuration;
        });

        return command;
    }

    /// <summary>
    /// Resolves --video / --folder / the configured source folder into an ordered file list.
    /// Shared with <c>run</c>, so both commands select the same videos from the same arguments.
    /// </summary>
    public static bool TryResolveVideos(
        ParseResult parseResult,
        AiStudioAutoExtractionConfig config,
        out IReadOnlyList<string> videos,
        out int failureExitCode) {

        videos = [];
        failureExitCode = ExitCodes.Success;

        string? single = parseResult.GetValue(Video);
        if (!string.IsNullOrWhiteSpace(single)) {
            if (!File.Exists(single)) {
                Ui.Error($"Video not found: '{single}'", "plan");
                failureExitCode = ExitCodes.Usage;
                return false;
            }
            videos = [Path.GetFullPath(single)];
            return true;
        }

        string folder = parseResult.GetValue(Folder) is string requested && !string.IsNullOrWhiteSpace(requested)
            ? requested
            : config.SourceFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
            Ui.Error($"Source folder not found: '{folder}'", "plan");
            failureExitCode = ExitCodes.Usage;
            return false;
        }

        config.SourceFolder = Path.GetFullPath(folder);
        var found = Directory.GetFiles(config.SourceFolder, "*.mp4").ToList();

        string? from = parseResult.GetValue(From);
        if (!string.IsNullOrWhiteSpace(from)) {
            // Ordering happens in the planner, so filter on the same ordering rather than on the
            // raw directory listing - "start at this video" has to mean chronologically.
            var ordered = found
                .OrderBy(video => Media.VideoDateParser.Parse(video).Date)
                .ThenBy(video => Media.VideoDateParser.Parse(video).WeekNumber ?? int.MaxValue)
                .ThenBy(video => video)
                .ToList();

            int index = ordered.FindIndex(video => Path.GetFileName(video).Contains(from, StringComparison.OrdinalIgnoreCase));
            if (index < 0) {
                Ui.Error($"No video in '{config.SourceFolder}' matches --from '{from}'.", "plan");
                failureExitCode = ExitCodes.Usage;
                return false;
            }
            found = [.. ordered.Skip(index)];
        }

        if (found.Count == 0) {
            Ui.Warn($"No .mp4 files in '{config.SourceFolder}'.", "plan");
        }

        videos = found;
        return true;
    }

    private static void Render(ExtractionPlan plan) {
        Ui.Header("Plan");
        Ui.Table("Lauf", [
            ("Backend", plan.Backend),
            ("Modell", plan.Model),
            ("API-Key", $"Profil {plan.ApiKeyProfile} ({plan.ApiKeyEnvName}) — {(plan.ApiKeyResolves ? "gesetzt" : "FEHLT")}"),
            ("Quelle", plan.SourceFolder),
            ("Ziel", plan.TargetFolder),
            ("Videos", plan.VideoCount.ToString()),
            ("Segmente je Video", $"{plan.SegmentsPerVideo} ({plan.OverlapSeconds}s Overlap, {plan.SpeedMultiplier}x)"),
            ("Offene Anfragen", plan.PendingRequests.ToString()),
            ("Wiederverwendbar", $"{plan.ResumableSegments} (Fenster: {plan.ResumeWindowHours}h)"),
            ("Refinement danach", plan.RefinementFollows ? "ja" : "nein")
        ]);

        foreach (var video in plan.Videos) {
            string state = video.PendingSegments == 0
                ? "vollständig vorhanden"
                : $"{video.PendingSegments} von {video.SegmentCount} offen";
            Ui.Detail($"{video.FileName} — {state}");
        }

        foreach (string warning in plan.Warnings) {
            Ui.Warn(warning, "plan");
        }

        if (!plan.ApiKeyResolves) {
            Ui.Error($"Der API-Key '{plan.ApiKeyEnvName}' ist nicht gesetzt — dieser Lauf würde scheitern.", "plan");
        }
    }
}
