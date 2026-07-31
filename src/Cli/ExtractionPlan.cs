using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Media;

namespace LectureExtraction.Cli;

/// <summary>
/// What a run would do, resolved without calling anything. This is the cheap answer to the
/// expensive question - how many videos, how many segments, how many billable requests, under
/// which model and key profile - so a wrong <c>--folder</c> or an unset API key is caught before
/// a paid run rather than during one.
/// </summary>
public sealed record ExtractionPlan(
    string Backend,
    string Model,
    int ApiKeyProfile,
    string ApiKeyEnvName,
    bool ApiKeyResolves,
    string SourceFolder,
    string TargetFolder,
    int SegmentsPerVideo,
    int OverlapSeconds,
    double SpeedMultiplier,
    bool RefinementFollows,
    double ResumeWindowHours,
    IReadOnlyList<PlannedVideo> Videos) {

    public int VideoCount => Videos.Count;

    /// <summary>Segments that still need transcribing - the number of billable generation requests.</summary>
    public int PendingRequests => Videos.Sum(video => video.PendingSegments);

    /// <summary>Segments already on disk and fresh enough to be reused.</summary>
    public int ResumableSegments => Videos.Sum(video => video.ResumableSegments);

    /// <summary>
    /// Everything about this plan that a caller should look at before starting it.
    ///
    /// <para>The output-folder collision is the expensive one. A source folder commonly holds both
    /// <c>lecture.mp4</c> and <c>lecture-speed-1-compressed.mp4</c>; the compression suffix is
    /// stripped when deriving the output folder, so both variants of one lecture resolve to the
    /// <i>same</i> folder and the same part file names. Processing both means the second run reads
    /// the first one's segments and .tex files as its own cache - work attributed to the wrong
    /// source, and a batch roughly twice as long as intended. Nothing in the interactive path
    /// reports this, which is why it is surfaced here.</para>
    /// </summary>
    public IReadOnlyList<string> Warnings {
        get {
            var warnings = Videos
                .Where(video => !video.NameMatchesSchema)
                .Select(video => $"{video.FileName}: does not match the date/week naming scheme")
                .ToList();

            warnings.AddRange(Videos
                .GroupBy(video => video.OutputFolder, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group =>
                    $"{group.Count()} videos share the output folder '{Path.GetFileName(group.Key)}' "
                    + $"({string.Join(", ", group.Select(video => video.FileName))}) - "
                    + "they will overwrite and re-read each other's parts. Select one variant."));

            return warnings;
        }
    }
}

/// <summary>One video's share of an <see cref="ExtractionPlan"/>.</summary>
public sealed record PlannedVideo(
    string FileName,
    string Path,
    bool NameMatchesSchema,
    string? LectureDate,
    int? WeekNumber,
    int SegmentCount,
    int ResumableSegments,
    string OutputFolder) {

    public int PendingSegments => Math.Max(0, SegmentCount - ResumableSegments);
}

/// <summary>Builds an <see cref="ExtractionPlan"/> from configuration and what is already on disk.</summary>
public static class ExtractionPlanner {
    /// <summary>
    /// The freshness window <c>TranscribeSegmentsAsync</c> applies when deciding whether an
    /// existing part file may be reused instead of re-requested. It is a hardcoded constant in
    /// <c>VideoProcessingState</c> with no configuration key, and it is the single most expensive
    /// thing about this pipeline that nothing surfaces: a retry after it lapses silently re-buys
    /// every part. The planner reports it for exactly that reason.
    /// </summary>
    public const double DefaultResumeWindowHours = 2;

    public static ExtractionPlan Build(
        AiStudioAutoExtractionConfig config,
        IReadOnlyList<string> videos,
        double resumeWindowHours,
        bool force) {

        string targetFolder = string.IsNullOrWhiteSpace(config.TargetFolder)
            ? Path.Combine(config.SourceFolder, "extracted_output")
            : config.TargetFolder;

        var planned = videos
            .OrderBy(video => VideoDateParser.Parse(video).Date)
            .ThenBy(video => VideoDateParser.Parse(video).WeekNumber ?? int.MaxValue)
            .ThenBy(video => video)
            .Select(video => Describe(video, config, targetFolder, resumeWindowHours, force))
            .ToList();

        string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);

        return new ExtractionPlan(
            Backend: "aistudio",
            Model: config.CurrentModel,
            ApiKeyProfile: config.ActiveApiProfile,
            ApiKeyEnvName: envName,
            ApiKeyResolves: GoogleAiClientBuilder.IsApiKeyPresent(envName),
            SourceFolder: config.SourceFolder,
            TargetFolder: targetFolder,
            SegmentsPerVideo: config.NumberOfParts,
            OverlapSeconds: config.OverlapSeconds,
            SpeedMultiplier: config.SpeedMultiplier,
            RefinementFollows: config.GoIntoLatexRefinement,
            ResumeWindowHours: resumeWindowHours,
            Videos: planned);
    }

    private static PlannedVideo Describe(
        string video,
        AiStudioAutoExtractionConfig config,
        string targetFolder,
        double resumeWindowHours,
        bool force) {

        var lecture = VideoDateParser.Parse(video);
        // The output folder carries no prefix while the .tex files do; both come from the same
        // helpers the pipeline itself uses, so the plan cannot predict paths the run will not write.
        string outputFolder = Path.Combine(targetFolder, ExtractionHelpers.ComputeOutputFolderName(video));
        string texBaseName = ExtractionHelpers.ComputeTexBaseName(video);

        return new PlannedVideo(
            FileName: Path.GetFileName(video),
            Path: Path.GetFullPath(video),
            NameMatchesSchema: lecture.IsValid,
            LectureDate: lecture.Date == DateTime.MinValue ? null : lecture.Date.ToString("yyyy-MM-dd"),
            WeekNumber: lecture.WeekNumber,
            SegmentCount: config.NumberOfParts,
            ResumableSegments: force ? 0 : CountResumableSegments(outputFolder, texBaseName, config.NumberOfParts, resumeWindowHours),
            OutputFolder: outputFolder);
    }

    /// <summary>
    /// Mirrors the reuse test in <c>TranscribeSegmentsAsync</c>: a part is skipped when its .tex
    /// exists and is younger than the window. Kept deliberately identical, because a plan that
    /// disagrees with the run it predicts is worse than no plan.
    /// </summary>
    private static int CountResumableSegments(string outputFolder, string baseName, int segmentCount, double resumeWindowHours) {
        if (!Directory.Exists(outputFolder)) {
            return 0;
        }

        var window = TimeSpan.FromHours(resumeWindowHours);
        int resumable = 0;

        for (int part = 1; part <= segmentCount; part++) {
            string texPath = Path.Combine(outputFolder, $"{baseName}-part{part}.tex");
            if (File.Exists(texPath) && (DateTime.Now - File.GetLastWriteTime(texPath)) <= window) {
                resumable++;
            }
        }

        return resumable;
    }
}
