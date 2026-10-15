using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;

namespace LectureExtraction.Cli;

/// <summary>
/// [AI Context] Which lecture output folders a --video / --folder pair names, for the commands that
/// read output rather than videos (status, clean, finish). --video takes a video file or lecture name
/// and finds its folder under --folder (when given) or the output root; --folder alone takes one lecture
/// folder or a parent of them; neither means the configured output root. Was copied into status and clean.
/// [Human] Findet die Vorlesungsordner zu --video / --folder.
/// </summary>
public static class LectureFolders {
    public static List<string> Resolve(string? video, string? folder, IAutoExtractionConfig config) {
        if (!string.IsNullOrWhiteSpace(video)) {
            // Under --folder when it is given (a parent of lecture folders), else under the output root.
            string searchRoot = !string.IsNullOrWhiteSpace(folder) ? Path.GetFullPath(folder) : ExtractionHelpers.OutputRoot(config);
            string name = ExtractionHelpers.ComputeOutputFolderName(video);
            string candidate = Path.Combine(searchRoot, name);
            if (Directory.Exists(candidate)) return [candidate];
            if (Directory.Exists(video)) return [Path.GetFullPath(video)];
            Ui.Warn($"Kein Vorlesungsordner '{name}' unter {searchRoot} gefunden.", "lecx");
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
