using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The whole pipeline, headless. Video in, segments, LaTeX, refinement and PDF out.
///
/// <para>There is no orchestration here, because the pipeline already chains: a successful
/// extraction constructs a <c>LatexRefinementSession</c> and awaits it, which runs steps 1-3 and
/// then the PDF. So this command supplies arguments and picks an entry point; the sequencing is
/// the pipeline's own. <c>--stop-after extract</c> works by turning that chaining off rather than
/// by reimplementing it.</para>
///
/// <para>This is the first command that can spend money, so it refuses to start when the plan says
/// the key does not resolve, and <c>--dry-run</c> prints exactly what it would do.</para>
/// </summary>
public static class RunCommand {
    private static readonly Option<string?> StopAfter = new("--stop-after") {
        Description = "Stop after a stage: extract (skip refinement and PDF) or full (default)."
    };

    public static Command Build(bool chainRefinement, string name, string description) {
        var command = new Command(name, description) {
            PlanCommand.Video, PlanCommand.Folder, PlanCommand.From,
            PlanCommand.ResumeWindow, PlanCommand.Force,
            ExtractionOptions.Out, ExtractionOptions.Parts, ExtractionOptions.Overlap,
            ExtractionOptions.Speed, ExtractionOptions.Preset,
            ExtractionOptions.Model, ExtractionOptions.Profile
        };

        if (chainRefinement) {
            command.Add(StopAfter);
        }

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            if (!PlanCommand.TryResolveVideos(parseResult, config, out var videos, out int failure)) {
                return failure;
            }

            // `extract run` never chains; `run --stop-after extract` chooses not to.
            bool stopAfterExtract = !chainRefinement
                || string.Equals(parseResult.GetValue(StopAfter), "extract", StringComparison.OrdinalIgnoreCase);
            config.GoIntoLatexRefinement = !stopAfterExtract;

            double resumeWindow = parseResult.GetValue(PlanCommand.ResumeWindow) ?? ExtractionPlanner.DefaultResumeWindowHours;
            bool force = parseResult.GetValue(PlanCommand.Force);
            var plan = ExtractionPlanner.Build(config, videos, resumeWindow, force);

            if (context.DryRun) {
                CliOutput.Payload(context, new { dryRun = true, plan },
                    () => Ui.Info($"{plan.VideoCount} Video(s), {plan.PendingRequests} offene Anfrage(n). Nichts ausgeführt (--dry-run)."));
                return ExitCodes.Success;
            }

            if (!plan.ApiKeyResolves) {
                Ui.Error($"Der API-Key '{plan.ApiKeyEnvName}' (Profil {plan.ApiKeyProfile}) ist nicht gesetzt.", name);
                return ExitCodes.Configuration;
            }

            if (videos.Count == 0) {
                Ui.Warn("Keine Videos zu verarbeiten.", name);
                return ExitCodes.Success;
            }

            // --force is expressed by widening the reuse window to nothing, which is the same knob
            // the pipeline reads; there is no separate "ignore the cache" path to keep in sync.
            SessionCostLedger.Reset();
            var session = BuildSession(config, force ? 0 : resumeWindow);

            bool allSucceeded = await session.RunAsync([.. videos]);

            var summary = new {
                videos = plan.VideoCount,
                requestsPlanned = plan.PendingRequests,
                generationRequests = SessionCostLedger.GenerationRequests,
                supportRequests = SessionCostLedger.SupportRequests,
                retriedAttempts = SessionCostLedger.RetriedAttempts,
                timeWaitedSeconds = Math.Round(SessionCostLedger.TimeWaited.TotalSeconds, 1),
                refinementRan = !stopAfterExtract,
                targetFolder = plan.TargetFolder,
                allSucceeded
            };

            CliOutput.Payload(context, summary, () => Ui.Table("Aufwand dieser Sitzung", SessionCostLedger.Summary()));

            // Partial success is its own exit code: some videos produced .tex and others did not,
            // which the interactive app only ever reported as a scrolling warning.
            return allSucceeded ? ExitCodes.Success : ExitCodes.Partial;
        });

        return command;
    }

    private static AiStudioAutoExtractionSession BuildSession(AiStudioAutoExtractionConfig config, double resumeWindowHours) {
        string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);
        string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName) ?? "no-key";
        Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);

        var attachmentHandler = new AttachmentUploader(
            client, config.SourceFolder, [config.SourceFolder], true, "",
            config.GoogleVideoFps, config.InlineHistoryImages, config.FileActivationDelaySeconds,
            config.VideoUploadTimeoutSeconds, config.VideoUploadMaxRetries) {
            ClientFactory = () => GoogleAiClientBuilder.BuildAiStudioClient(apiKey)
        };

        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var refinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();

        return new AiStudioAutoExtractionSession(client, config, attachmentHandler, sessionLogger, refinementConfig) {
            ResumeWindow = TimeSpan.FromHours(resumeWindowHours)
        };
    }
}
