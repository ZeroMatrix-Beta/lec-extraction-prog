using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] The upload half of the InlinePrecedingLecTexParts switch (Phase 12): sends each
/// preceding part's .tex to the backend as a file reference instead of inlining its text into the
/// prompt, and produces the text that has to be said instead.
///
/// <para>Why the text still matters: a File-API / GCS reference cannot carry the
/// &lt;reference_context file="..."&gt; wrapper the inline path uses, so the read-only instruction that
/// wrapper implied has to be restated explicitly. Dropping it would be a silent prompt regression
/// that only shows up as degraded transcription quality.</para>
///
/// <para>Shared by both backends deliberately. The per-backend difference (Files API vs. GCS bucket)
/// already lives inside AttachmentUploader, so nothing here is backend-specific - this is the
/// opportunistic-extraction rule from the plan, not a step towards unifying the sessions.</para>
/// [Human] Lädt die .tex-Dateien vorheriger Teile als Anhang hoch, statt sie in den Prompt zu kopieren,
/// und liefert den Ersatztext, der die Read-only-Regel weiterhin ausspricht.
/// </summary>
public static class PrecedingTexReferences {
    /// <summary>
    /// [AI Context] The result of one upload round. Returned rather than written into the caller's
    /// prompt builder so the session stays the single writer of its own request (the Phase 4.5 rule).
    /// <paramref name="ReferenceText"/> is appended after the anchor block and before the static
    /// prompt beginning; <paramref name="Parts"/> go after the whole text Part, before the video.
    /// [Human] Ergebnis eines Upload-Durchlaufs: der Ersatztext und die hochgeladenen Datei-Parts.
    /// </summary>
    public sealed record Result(string ReferenceText, List<Part> Parts);

    /// <summary>
    /// [AI Context] Uploads each preceding .tex file and builds the notice naming them. A file whose
    /// upload fails is inlined instead, exactly as the InlinePrecedingLecTexParts=true path would have
    /// done, so a failed upload degrades to the old behaviour rather than silently dropping context the
    /// model needs to resolve \ref{...} back into earlier parts.
    /// [Human] Lädt jede .tex-Datei hoch. Scheitert ein Upload, wird die Datei wie früher direkt
    /// eingebettet - der Kontext geht nie verloren.
    /// </summary>
    public static async Task<Result> UploadAsync(List<string> previousTexFiles, AttachmentUploader uploader) {
        Ui.Info("Lade folgende bereits generierte .tex-Dateien als Datei-Referenz hoch (statt sie in den Prompt einzubetten):", "Kontext");

        var parts = new List<Part>();
        var uploadedNames = new List<string>();
        var builder = new StringBuilder();

        foreach (var previousTexFile in previousTexFiles) {
            string previousTexFileName = Path.GetFileName(previousTexFile);
            Ui.Detail($"- {previousTexFileName}");

            bool uploaded = await uploader.UploadAndAttachFileAsync(previousTexFile, parts, uploadTextAsFile: true);
            if (uploaded) {
                uploadedNames.Add(previousTexFileName);
                continue;
            }

            Ui.Warn($"Upload von '{previousTexFileName}' fehlgeschlagen - die Datei wird stattdessen direkt in den Prompt eingebettet.", "Kontext");
            string previousTexContent = await System.IO.File.ReadAllTextAsync(previousTexFile);
            builder.Append($"<reference_context file=\"{previousTexFileName}\">\n{previousTexContent}\n</reference_context>\n\n");
        }

        if (uploadedNames.Count > 0) {
            builder.Append(
                "NOTE: The LaTeX output of the preceding part(s) is attached to this request as read-only " +
                $"reference file(s): {string.Join(", ", uploadedNames)}. The CRITICAL RULES above apply to those " +
                "attachments unchanged - they are reference material only, and the sole transcription target is the " +
                "attached video segment.\n\n");
        }

        return new Result(builder.ToString(), parts);
    }
}
