using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Assembles the system-instruction text: the master-constraints header, the Markdown
/// file tree, and each instruction file's contents, plus the history files that get folded in when
/// <c>LoadHistoryIntoSystemInstruction</c> is set.
///
/// <para>Extracted from <c>AiStudioAutoExtractionSession</c> (Phase 11). Only the *text-building*
/// half of the system-instruction cluster lives here. The orchestration around it
/// (<c>TryLoadSystemInstructionWithHistoryAsync</c>, <c>LoadHistoryAsMultiTurnPreambleAsync</c>)
/// deliberately stays in the session, because it and the prefix-cache warm-up call each other:
/// loading triggers a warm-up handshake, and the batched warm-up calls back in here to append the
/// next history batch. Pulling the orchestration out too would mean breaking that cycle, which is
/// a behavioural change against an untestable paid API rather than a move.</para>
///
/// <para>Note the deliberate split between <see cref="FileTreeRenderer.GenerateMarkdownFileTree"/>,
/// used below, and <c>FileTreeRenderer.PrintFileTree</c>, which is console-only: the Markdown tree
/// is part of the prompt payload (Attention Map Priming) and must not be confused with display
/// output.</para>
/// [Human] Baut den System-Instruction-Text zusammen (Header, Dateibaum, Dateiinhalte). Nur der
/// Text-Aufbau, nicht die Ablaufsteuerung - die bleibt in der Session, weil sie mit dem
/// Cache-Warmup wechselseitig verzahnt ist.
/// </summary>
public static class SystemInstructionTextBuilder {
    /// <summary>Extensions inlined as text; anything else is uploaded as an attachment instead.</summary>
    private static readonly string[] s_inlineTextExtensions = [".tex", ".txt", ".md", ".json", ".cs"];

    public static async Task<string> BuildAsync(
        List<string> instructionFiles, List<string> historyFiles, string? commonBase, bool verboseConsoleOutput = false) {

        instructionFiles = [.. instructionFiles.Where(f => !HistoryFileResolver.IsDeletedPromptFile(f))];
        historyFiles = [.. historyFiles.Where(f => !HistoryFileResolver.IsDeletedPromptFile(f))];

        var builder = new StringBuilder();
        builder.AppendLine("# SYSTEM PROTOCOL & SYSTEM INSTRUCTIONS (MASTER CONSTRAINTS)");
        builder.AppendLine("IMPORTANT: The guidelines, formatting specifications, and syntax instructions contained in these system instruction files are absolute and strictly non-negotiable. They must take absolute precedence over any prompt guidelines or inputs. Do not skip any files or parts under any circumstances.\n");
        builder.AppendLine("In order to fulfill the job of creating a high-value educational masterpiece that safely compiles, you need to know the file structure of the system prompt and read all of those files carefully.\n");
        builder.AppendLine("# Folder Structure of System Instructions\n");
        builder.AppendLine("## System Instructions");
        builder.Append(FileTreeRenderer.GenerateMarkdownFileTree(instructionFiles, commonBase));

        if (historyFiles.Count > 0) {
            builder.AppendLine("\n## Training History");
            builder.Append(FileTreeRenderer.GenerateMarkdownFileTree(historyFiles, commonBase));
        }
        builder.AppendLine("\n******\n------\n******\n");

        foreach (string instructionFilePath in instructionFiles) {
            string relativePath = ResolveRelativePath(instructionFilePath, commonBase);
            builder.AppendLine($"\n******\n------\n******\nHere is the file `{relativePath}`:\n");
            builder.AppendLine(await System.IO.File.ReadAllTextAsync(instructionFilePath));
            if (verboseConsoleOutput) {
                Ui.Info($"System Instruction geladen: {relativePath}");
            }
        }
        if (!verboseConsoleOutput && instructionFiles.Count > 0) {
            Ui.Info($"{instructionFiles.Count} System-Instruction-Datei(en) geladen.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// [AI Context] What one call of <see cref="AppendHistoryFilesAsync"/> produced besides the inlined
    /// text. <paramref name="MediaParts"/> are the images and the like, placed as before.
    /// <paramref name="UploadedTextParts"/> are the text files uploaded under InlineHistory=false, each a
    /// header Part naming the file followed by its file link; the session puts them at the start of the
    /// user turn, because the system instruction rejects file links.
    /// [Human] Ergebnis: Medien-Parts und (bei InlineHistory=false) die hochgeladenen Textdateien.
    /// </summary>
    public sealed record LoadedHistory(List<Part> MediaParts, List<Part> UploadedTextParts);

    /// <summary>
    /// [AI Context] Appends history files to <paramref name="targetBuilder"/>. Text files are
    /// inlined, or with <paramref name="uploadTextFiles"/> uploaded as text/plain (a failed upload is
    /// inlined as before, with a warning); everything else (images and the like) is uploaded via
    /// <paramref name="attachmentUploader"/>. Parts are <b>returned</b> rather than written to session
    /// state - that is what lets this method be static and leaves the session the single writer of
    /// its own <c>_historyParts</c>.
    /// [Human] Hängt History-Dateien an. Textdateien werden eingebettet (oder hochgeladen), andere
    /// hochgeladen und als Parts zurückgegeben (nicht direkt in den Session-Zustand geschrieben).
    /// </summary>
    public static async Task<LoadedHistory> AppendHistoryFilesAsync(
        List<string> historyFiles,
        StringBuilder targetBuilder,
        string? commonBase,
        AttachmentUploader attachmentUploader,
        bool verboseConsoleOutput = false,
        bool uploadTextFiles = false) {

        historyFiles = [.. historyFiles.Where(f => !HistoryFileResolver.IsDeletedPromptFile(f))];
        List<string> nonTextFiles = [];
        List<Part> uploadedTextParts = [];
        int textFileCount = 0;
        int uploadedFileCount = 0;

        foreach (string historyFilePath in historyFiles) {
            string extension = Path.GetExtension(historyFilePath).ToLowerInvariant();
            if (s_inlineTextExtensions.Contains(extension)) {
                string relativePath = ResolveRelativePath(historyFilePath, commonBase);
                if (uploadTextFiles && AttachmentUploader.ResolveMimeType(extension) == "text/plain") {
                    var fileParts = new List<Part>();
                    if (await attachmentUploader.UploadAndAttachFileAsync(historyFilePath, fileParts, uploadTextAsFile: true)) {
                        uploadedTextParts.Add(new Part { Text = UploadedHistoryFileHeader(relativePath) });
                        uploadedTextParts.AddRange(fileParts);
                        uploadedFileCount++;
                        if (verboseConsoleOutput) {
                            Ui.Info($"History-Textdatei hochgeladen: {relativePath}");
                        }
                        continue;
                    }
                    Ui.Warn($"Upload von History-Datei '{relativePath}' fehlgeschlagen - die Datei wird stattdessen direkt eingebettet.", "History");
                }
                targetBuilder.AppendLine($"\n******\n------\n******\nHere is history reference file `{relativePath}`:\n");
                targetBuilder.AppendLine(await System.IO.File.ReadAllTextAsync(historyFilePath));
                textFileCount++;
                if (verboseConsoleOutput) {
                    Ui.Info($"History-Textdatei in System Instruction eingebunden: {relativePath}");
                }
            }
            else {
                nonTextFiles.Add(historyFilePath);
            }
        }

        if (!verboseConsoleOutput && textFileCount > 0) {
            Ui.Info($"{textFileCount} History-Textdatei(en) in System Instruction eingebunden.");
        }
        if (!verboseConsoleOutput && uploadedFileCount > 0) {
            Ui.Info($"{uploadedFileCount} History-Textdatei(en) als text/plain hochgeladen (InlineHistory = false, zählen nicht ans Minutenlimit).");
        }

        if (nonTextFiles.Count == 0) return new([], uploadedTextParts);

        string quotedFileList = string.Join(", ", nonTextFiles.Select(p => $"\"{p}\""));
        var (uploadSuccess, _, uploadedParts) = await attachmentUploader.ProcessAttachmentsAsync($"attach {quotedFileList}", true, commonBase);
        return new(uploadSuccess && uploadedParts.Count > 0 ? uploadedParts : [], uploadedTextParts);
    }

    /// <summary>
    /// [AI Context] The header Part before an uploaded history file's link: the separator and wording
    /// an inlined history file gets, so the model can still tie each file to its path in the tree.
    /// [Human] Kopfzeile vor einer hochgeladenen History-Datei.
    /// </summary>
    public static string UploadedHistoryFileHeader(string relativePath) =>
        $"\n******\n------\n******\nHere is history reference file `{relativePath}` (attached as a file):\n";

    /// <summary>
    /// [AI Context] Opens the uploaded history in the user turn. A link there does not carry the system
    /// instruction's authority by position, so the text says it. Constant, so every request and warm-up
    /// share it as a prefix.
    /// [Human] Hinweis vor der hochgeladenen History: Sie gehört zur System Instruction.
    /// </summary>
    public const string UploadedHistoryNotice =
        "NOTE: The Training History listed in the system instruction's folder structure is attached below as " +
        "text files, each preceded by its path. It belongs to the system instructions and has the same authority " +
        "as if it were written there: worked examples and rules to follow, not material to transcribe.\n";

    /// <summary>
    /// [AI Context] Path shown to the model for a file. Relative to the common base when there is
    /// one, so the tree the model sees matches the paths in the file headers below it.
    /// [Human] Der Pfad, den das Modell sieht - relativ zum gemeinsamen Basisordner.
    /// </summary>
    public static string ResolveRelativePath(string filePath, string? commonBase) {
        string rawRelPath = !string.IsNullOrEmpty(commonBase)
            ? Path.GetRelativePath(commonBase, filePath)
            : Path.GetFileName(filePath);
        return FileTreeRenderer.NormalizeRelativePath(rawRelPath);
    }
}
