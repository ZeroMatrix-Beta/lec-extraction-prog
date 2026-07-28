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
    /// [AI Context] Appends history files to <paramref name="targetBuilder"/>. Text files are
    /// inlined; everything else (images and the like) is uploaded via
    /// <paramref name="attachmentUploader"/> and <b>returned</b> rather than written to session
    /// state - that is what lets this method be static and leaves the session the single writer of
    /// its own <c>_historyParts</c>.
    /// [Human] Hängt History-Dateien an. Textdateien werden eingebettet, andere hochgeladen und
    /// als Parts zurückgegeben (nicht direkt in den Session-Zustand geschrieben).
    /// </summary>
    public static async Task<List<Part>> AppendHistoryFilesAsync(
        List<string> historyFiles,
        StringBuilder targetBuilder,
        string? commonBase,
        AttachmentUploader attachmentUploader,
        bool verboseConsoleOutput = false) {

        List<string> nonTextFiles = [];
        int textFileCount = 0;

        foreach (string historyFilePath in historyFiles) {
            string extension = Path.GetExtension(historyFilePath).ToLowerInvariant();
            if (s_inlineTextExtensions.Contains(extension)) {
                string relativePath = ResolveRelativePath(historyFilePath, commonBase);
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

        if (nonTextFiles.Count == 0) return [];

        string quotedFileList = string.Join(", ", nonTextFiles.Select(p => $"\"{p}\""));
        var (uploadSuccess, _, uploadedParts) = await attachmentUploader.ProcessAttachmentsAsync($"attach {quotedFileList}", true, commonBase);
        return uploadSuccess && uploadedParts.Count > 0 ? uploadedParts : [];
    }

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
