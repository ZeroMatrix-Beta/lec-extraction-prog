using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] <c>lecx thoughts list|show</c>: reads the <c>-thoughts.md</c> files the stages write when
/// <c>ThoughtSummaries.ToSeparateFile</c> is on. Free and local. Written for an agent first: both
/// subcommands have a <c>--json</c> payload, <c>--stage</c> takes the stable keys the files carry
/// (<c>extract-part1</c>, <c>merge</c>, <c>speech</c>, <c>final</c>, <c>pdf-repair-1</c>; a prefix such as
/// <c>extract</c> or <c>pdf</c> matches all of them), and <c>show</c> also accepts the stage's .tex
/// output in place of the thoughts file.
/// [Human] Zeigt die Gedanken-Zusammenfassungen an, die die Stufen geschrieben haben.
/// </summary>
public static class ThoughtsCommand {
    private static readonly Option<string?> Folder = new("--folder") {
        Description = "A lecture output folder, or a folder of them. Defaults to the configured target folder."
    };

    private static readonly Option<string?> Video = new("--video") {
        Description = "Only the output folder of this video (path or file name)."
    };

    private static readonly Option<string?> Stage = new("--stage") {
        Description = "Stage key or prefix: extract-partN, merge, speech, final, pdf-repair-N (extract, pdf match all parts/rounds)."
    };

    private static readonly Option<int?> Request = new("--request") {
        Description = "Only this request (1 = the first, 2+ = continues)."
    };

    private static readonly Argument<string?> Target = new("path") {
        Description = "A thoughts file, the .tex output it belongs to, or a folder. Optional.",
        Arity = ArgumentArity.ZeroOrOne
    };

    public static Command Build() {
        var group = new Command("thoughts", "Read the thought summaries the stages wrote (-thoughts.md files). Free.");

        var list = new Command("list", "List thoughts files with stage, model, requests and thinking tokens.") { Folder, Video, Stage };
        list.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var files = Discover(parseResult, parseResult.GetValue(Stage));

            var payload = files.Select(Summarize).ToList();
            CliOutput.Payload(context, payload, () => {
                if (payload.Count == 0) {
                    Ui.Warn("Keine Gedanken-Dateien gefunden. ThoughtSummaries.Enabled (oder --thoughts) muss beim Lauf an gewesen sein.", "thoughts");
                    return;
                }
                foreach (var file in files) {
                    string tokens = file.Requests.Sum(r => r.ThoughtTokens).ToString("N0", CultureInfo.InvariantCulture);
                    Print($"{file.Header.Started:yyyy-MM-dd HH:mm}  {file.Header.StageKey,-14} {file.Requests.Count,2} req  {tokens,9} thinking tok  {file.Path}");
                }
            });
            return ExitCodes.Success;
        });

        var show = new Command("show", "Print one thoughts file's summaries (the newest match if several).") { Target, Folder, Video, Stage, Request };
        show.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var file = ResolveOne(parseResult);
            if (file == null) {
                Ui.Error("Keine passende Gedanken-Datei gefunden. 'lecx thoughts list' zeigt, welche es gibt.", "thoughts");
                return ExitCodes.Usage;
            }

            int? request = parseResult.GetValue(Request);
            var requests = request is int number ? file.Requests.Where(r => r.Request == number).ToList() : file.Requests.ToList();

            CliOutput.Payload(context, new {
                file = file.Path,
                file.Header.StageKey,
                file.Header.Stage,
                file.Header.Output,
                file.Header.Model,
                file.Header.Started,
                totalThoughtTokens = file.Requests.Sum(r => r.ThoughtTokens),
                requests
            }, () => {
                Print($"# {file.Header.Stage} ({file.Header.StageKey}) · {file.Header.Model} · {file.Header.Started:yyyy-MM-dd HH:mm:ss}");
                Print($"# {file.Path}");
                foreach (var r in requests) {
                    Print("");
                    Print($"## Request {r.Request} · {r.ThoughtTokens.ToString("N0", CultureInfo.InvariantCulture)} thinking tokens · {r.OutputTokens.ToString("N0", CultureInfo.InvariantCulture)} output tokens{(r.FinishReason != null ? $" · {r.FinishReason}" : "")}");
                    Print("");
                    Print(string.IsNullOrWhiteSpace(r.Text) ? "(no summary returned)" : r.Text);
                }
            });
            return ExitCodes.Success;
        });

        group.Add(list);
        group.Add(show);
        return group;
    }

    /// <summary>
    /// The command's own output, straight to stdout: Spectre wraps a redirected console at 80 columns,
    /// which would break every path and summary line a caller copies or greps.
    /// </summary>
    private static void Print(string line) => Console.Out.WriteLine(line);

    private static object Summarize(ThoughtFileContent file) => new {
        file = file.Path,
        file.Header.StageKey,
        file.Header.Stage,
        file.Header.Output,
        file.Header.Model,
        file.Header.Started,
        requests = file.Requests.Count,
        thoughtTokens = file.Requests.Sum(r => r.ThoughtTokens),
        outputTokens = file.Requests.Sum(r => r.OutputTokens),
        summaryCharacters = file.Requests.Sum(r => r.Text?.Length ?? 0)
    };

    /// <summary>Exposed for tests: does <paramref name="stageKey"/> match the <c>--stage</c> filter?</summary>
    public static bool StageMatches(string stageKey, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || stageKey.StartsWith(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Exposed for tests: every parseable thoughts file under <paramref name="root"/>, oldest first.</summary>
    public static List<ThoughtFileContent> FindIn(string root, string? stageFilter) {
        if (!Directory.Exists(root)) {
            return [];
        }

        var found = new List<ThoughtFileContent>();
        foreach (string path in Directory.EnumerateFiles(root, $"*{ThoughtFile.FileSuffix}*.md", SearchOption.AllDirectories)) {
            if (!ThoughtFile.IsThoughtFile(path)) {
                continue;
            }
            var content = ThoughtFile.Read(path);
            if (content != null && StageMatches(content.Header.StageKey, stageFilter)) {
                found.Add(content);
            }
        }
        return [.. found.OrderBy(f => f.Header.Started).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<ThoughtFileContent> Discover(ParseResult parseResult, string? stageFilter) =>
        FindIn(ResolveRoot(parseResult), stageFilter);

    private static string ResolveRoot(ParseResult parseResult) {
        var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
        string configuredRoot = string.IsNullOrWhiteSpace(config.TargetFolder)
            ? Path.Combine(config.SourceFolder, "extracted_output")
            : config.TargetFolder;

        if (parseResult.GetValue(Video) is string video && !string.IsNullOrWhiteSpace(video)) {
            if (Directory.Exists(video)) {
                return Path.GetFullPath(video);
            }
            string baseFolder = parseResult.GetValue(Folder) is string folderForVideo && !string.IsNullOrWhiteSpace(folderForVideo)
                ? Path.GetFullPath(folderForVideo)
                : configuredRoot;
            return Path.Combine(baseFolder, ExtractionHelpers.ComputeOutputFolderName(video));
        }

        if (parseResult.GetValue(Folder) is string folder && !string.IsNullOrWhiteSpace(folder)) {
            return Path.GetFullPath(folder);
        }
        return configuredRoot;
    }

    private static ThoughtFileContent? ResolveOne(ParseResult parseResult) {
        string? stageFilter = parseResult.GetValue(Stage);
        string? target = parseResult.GetValue(Target);

        if (!string.IsNullOrWhiteSpace(target)) {
            string full = Path.GetFullPath(target);
            if (Directory.Exists(full)) {
                return FindIn(full, stageFilter).LastOrDefault();
            }
            if (File.Exists(full) && ThoughtFile.IsThoughtFile(full)) {
                return ThoughtFile.Read(full);
            }

            // An output file: its thoughts sit beside it as <stem>-thoughts[-N].md; take the newest.
            // The output need not exist - a step whose answer was cut off still wrote its thoughts.
            string folder = Path.GetDirectoryName(full) ?? ".";
            if (!Directory.Exists(folder)) {
                return null;
            }
            string stem = Path.GetFileNameWithoutExtension(full);
            return Directory.EnumerateFiles(folder, $"{stem}{ThoughtFile.FileSuffix}*.md")
                .Where(ThoughtFile.IsThoughtFile)
                .Select(ThoughtFile.Read)
                .OfType<ThoughtFileContent>()
                .OrderBy(f => f.Header.Started)
                .LastOrDefault();
        }

        return Discover(parseResult, stageFilter).LastOrDefault();
    }
}
