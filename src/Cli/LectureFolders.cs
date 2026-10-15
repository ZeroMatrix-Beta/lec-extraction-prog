using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;

namespace LectureExtraction.Cli;

/// <summary>
/// [AI Context] Which lecture output folders a --video / --folder pair names, for the commands that
/// read output rather than videos (status, clean, finish). --video takes a video file or lecture name
/// and finds its folder under the output root; --folder takes one lecture folder or a parent of them;
/// neither means the configured output root. Was copied into status and clean.
/// [Human] Findet die Vorlesungsordner zu --video / --folder.
/// </summary>
public static class LectureFolders {
    public static List<string> Resolve(string? video, string? folder, IAutoExtractionConfig config) {
        if (!string.IsNullOrWhiteSpace(video)) {
            string candidate = Path.Combine(ExtractionHelpers.OutputRoot(config), ExtractionHelpers.ComputeOutputFolderName(video));
            if (Directory.Exists(candidate)) return [candidate];
            if (Directory.Exists(video)) return [Path.GetFullPath(video)];
            return [];
        }

        string root = !string.IsNullOrWhiteSpace(folder)
            ? Path.GetFullPath(folder)
            : ExtractionHelpers.OutputRoot(config);

        if (!Directory.Exists(root)) return [];
        // The root itself is a lecture when it holds tmp/ or a step1 file; a PDF alone does not make
        // it one, since a parent folder can hold PDFs too.
        if (Directory.Exists(Path.Combine(root, "tmp")) || Directory.GetFiles(root, "step1-*.tex").Length > 0) {
            return [root];
        }

        return [.. Directory.GetDirectories(root).Where(LectureProgress.IsLectureFolder).OrderBy(Path.GetFileName)];
    }
}
