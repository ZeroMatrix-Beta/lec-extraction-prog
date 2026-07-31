using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The read-only half of <c>lecx config</c>. These commands answer the questions a caller has to
/// resolve before spending anything - which model is selected, which key profile is active and
/// whether it actually resolves, which folders are configured - and they issue no API call.
/// <c>config set</c> is deliberately absent until the config-writeback seam lands, because writing
/// today would go through a <c>ConfigLoader.Save</c> that also rewrites the working copy.
/// </summary>
public static class ConfigCommands {
    public static Command Build() {
        var config = new Command("config", "Inspect the configuration the pipeline would run with.");
        config.Add(BuildList());
        config.Add(BuildGet());
        config.Add(BuildSet());
        config.Add(BuildModels());
        config.Add(BuildProfiles());
        config.Add(BuildFolders());
        return config;
    }

    private static Command BuildList() {
        var section = new Option<string?>("--section") {
            Description = "Limit output to one section, e.g. AiStudioAutoExtractionConfig."
        };

        var command = new Command("list", "Print the effective configuration.") { section };
        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            string? requested = parseResult.GetValue(section);

            if (requested != null && !ConfigSectionRegistry.TryResolve(requested, out _)) {
                return UnknownSection(requested);
            }

            var names = requested != null ? [requested] : ConfigSectionRegistry.Names;
            var payload = names.ToDictionary(name => name, ConfigSectionRegistry.Load);

            CliOutput.Payload(context, payload, () => {
                foreach (var (name, value) in payload) {
                    Ui.Step(name);
                    Ui.RawLine(CliOutput.ToJson(value));
                }
            });
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildGet() {
        var key = new Argument<string>("key") {
            Description = "Dotted path, e.g. AiStudioAutoExtractionConfig.CurrentModel or ...Paths.SourceFolder."
        };

        var command = new Command("get", "Read a single configuration value.") { key };
        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            string path = parseResult.GetValue(key) ?? "";

            string[] segments = path.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) {
                Ui.Error($"'{path}' must name a section and a property, e.g. AiStudioAutoExtractionConfig.CurrentModel.", "CLI");
                return ExitCodes.Usage;
            }

            if (!ConfigSectionRegistry.TryResolve(segments[0], out var sectionType)) {
                return UnknownSection(segments[0]);
            }

            object root = ConfigSectionRegistry.Load(sectionType);
            if (!ConfigSectionRegistry.TryReadPath(root, segments[1], out object? value, out string? error)) {
                Ui.Error($"{path}: {error}", "CLI");
                return ExitCodes.Usage;
            }

            CliOutput.Payload(context, new { key = path, value }, () => Ui.RawLine(FormatScalar(value)));
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildSet() {
        var key = new Argument<string>("key") {
            Description = "Dotted path, e.g. AiStudioAutoExtractionConfig.CurrentModel."
        };
        var value = new Argument<string>("value") { Description = "The new value." };

        var command = new Command("set", "Write a single configuration value.") { key, value };
        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            string path = parseResult.GetValue(key) ?? "";
            string raw = parseResult.GetValue(value) ?? "";

            string[] segments = path.Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) {
                Ui.Error($"'{path}' must name a section and a property, e.g. AiStudioAutoExtractionConfig.CurrentModel.", "CLI");
                return ExitCodes.Usage;
            }

            if (!ConfigSectionRegistry.TryResolve(segments[0], out var sectionType)) {
                return UnknownSection(segments[0]);
            }

            object root = ConfigSectionRegistry.Load(sectionType);
            if (!ConfigSectionRegistry.TryReadPath(root, segments[1], out object? before, out string? readError)) {
                Ui.Error($"{path}: {readError}", "CLI");
                return ExitCodes.Usage;
            }

            if (!ConfigSectionRegistry.TryWritePath(root, segments[1], raw, out string? writeError)) {
                Ui.Error($"{path}: {writeError}", "CLI");
                return ExitCodes.Usage;
            }

            if (context.DryRun) {
                CliOutput.Payload(context, new { key = path, before, after = raw, written = false },
                    () => Ui.Info($"{path}: {FormatScalar(before)} -> {raw} (dry run, nothing written)"));
                return ExitCodes.Success;
            }

            // Writing config is this command's entire purpose, so it does not additionally require
            // --save-config; that flag exists to stop a *run* from persisting changes as a side
            // effect. The scope is deliberately narrow - one value, then back to read-only.
            bool previous = ConfigStore.SaveEnabled;
            ConfigStore.SaveEnabled = true;
            try {
                ConfigSectionRegistry.Save(sectionType, root);
            }
            finally {
                ConfigStore.SaveEnabled = previous;
            }

            CliOutput.Payload(context, new { key = path, before, after = raw, written = true },
                () => Ui.Success($"{path}: {FormatScalar(before)} -> {raw}"));
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildModels() {
        var command = new Command("models", "List the models each extraction backend can select.");
        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var aiStudio = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            var vertex = (VertexAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(VertexAutoExtractionConfig));

            var payload = new {
                aiStudio = DescribeModels(aiStudio.CurrentModel, aiStudio.Model),
                vertex = DescribeModels(vertex.CurrentModel, vertex.Model)
            };

            CliOutput.Payload(context, payload, () => {
                RenderModelList("AI Studio", aiStudio.CurrentModel, payload.aiStudio);
                RenderModelList("Vertex AI", vertex.CurrentModel, payload.vertex);
            });
            return ExitCodes.Success;
        });
        return command;
    }

    private static Command BuildProfiles() {
        var command = new Command("profiles", "List the API-key profiles and whether each resolves in this environment.");
        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));

            // The value is never read or printed - only whether the variable is set - so that a
            // report of the credential layout can be pasted anywhere without leaking a key.
            var profiles = config.AiStudioApiKeyEnvNames
                .Select((envName, index) => new {
                    profile = index,
                    envName,
                    isActive = index == config.ActiveApiProfile,
                    resolves = GoogleAiClientBuilder.IsApiKeyPresent(envName)
                })
                .ToList();

            var payload = new { active = config.ActiveApiProfile, profiles };

            CliOutput.Payload(context, payload, () => {
                Ui.Step("API-Key Profile");
                foreach (var profile in profiles) {
                    string marker = profile.isActive ? "*" : " ";
                    string state = profile.resolves ? "set" : "MISSING";
                    Ui.Detail($"{marker} [{profile.profile}] {profile.envName} — {state}");
                }
            });

            // A missing key for the *active* profile is the failure a caller most needs to catch
            // before starting a run, so it is reported as a configuration problem rather than OK.
            bool activeResolves = profiles.Count > config.ActiveApiProfile
                && profiles[config.ActiveApiProfile].resolves;
            return activeResolves ? ExitCodes.Success : ExitCodes.Configuration;
        });
        return command;
    }

    private static Command BuildFolders() {
        var command = new Command("folders", "List the configured source and target folders.");
        command.SetAction(parseResult => {
            var context = CliOptions.ReadContext(parseResult);
            var aiStudio = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            var vertex = (VertexAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(VertexAutoExtractionConfig));

            var payload = new {
                aiStudio = new { source = aiStudio.SourceFolder, target = aiStudio.TargetFolder, predefined = aiStudio.PredefinedSourceFolders },
                vertex = new { source = vertex.SourceFolder, target = vertex.TargetFolder, predefined = vertex.PredefinedSourceFolders }
            };

            CliOutput.Payload(context, payload, () => {
                Ui.Step("Ordner");
                Ui.Detail($"AI Studio  source: {aiStudio.SourceFolder}");
                Ui.Detail($"AI Studio  target: {Fallback(aiStudio.TargetFolder)}");
                Ui.Detail($"Vertex     source: {vertex.SourceFolder}");
                Ui.Detail($"Vertex     target: {Fallback(vertex.TargetFolder)}");
                Ui.Blank();
                foreach (string folder in aiStudio.PredefinedSourceFolders) {
                    Ui.Detail($"  vorkonfiguriert: {folder}");
                }
            });
            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>
    /// The selectable models are the *distinct* entries: the stored array is append-prone (the same
    /// config-binding defect that commit 4cc6b2b fixed for the folder shortlist and the key names,
    /// which did not cover this array), so a live config can hold the same five names many times
    /// over. Reporting the duplicate count alongside keeps that visible instead of quietly
    /// presenting a repaired list.
    /// </summary>
    private static ModelReport DescribeModels(string current, IReadOnlyList<string> available) {
        List<string> distinct = [.. available.Distinct(StringComparer.Ordinal)];
        return new ModelReport(current, distinct, available.Count, available.Count - distinct.Count);
    }

    private static void RenderModelList(string label, string current, ModelReport report) {
        Ui.Step(label);
        foreach (string model in report.Available) {
            Ui.Detail(model == current ? $"* {model}" : $"  {model}");
        }

        if (report.DuplicateEntries > 0) {
            Ui.Warn($"{report.StoredEntries} Einträge gespeichert, davon {report.DuplicateEntries} Duplikate — die Liste wächst bei jedem Start.", "Config");
        }
    }

    private sealed record ModelReport(string Current, IReadOnlyList<string> Available, int StoredEntries, int DuplicateEntries);

    private static string Fallback(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(leer — wird unter dem Quellordner angelegt)" : value;

    private static string FormatScalar(object? value) => value switch {
        null => "",
        string text => text,
        System.Collections.IEnumerable sequence and not string => CliOutput.ToJson(sequence),
        _ => value.ToString() ?? ""
    };

    private static int UnknownSection(string requested) {
        Ui.Error($"Unknown config section '{requested}'. Known: {string.Join(", ", ConfigSectionRegistry.Names)}", "CLI");
        return ExitCodes.Usage;
    }
}
