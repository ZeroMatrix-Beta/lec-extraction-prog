using System.Collections.Generic;
using Google.GenAI.Types;
using LectureExtraction.Configuration;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] What a <see cref="LatexRefinementSession"/> should work on. Replaces the four
/// telescoping constructors that differed only in how much of the same state they set.
///
/// <para>The three factories below are the three branches
/// <c>LatexRefinementSession.ExecutePipelineAsync</c> actually distinguishes: an explicit list of
/// files, one explicit file, or "whatever is in the configured source folder". Before this type,
/// which branch you got was an emergent property of which overload you happened to call and which
/// arguments you happened to leave null - and two of the three were unreachable, because all three
/// construction sites in the app used the same overload. Naming them makes the pipeline's own modes
/// visible, without deleting behaviour that has no other entry point.</para>
/// [Human] Beschreibt, was die Refinement-Pipeline verarbeiten soll - eine Datei, mehrere Dateien
/// oder den konfigurierten Ordner. Ersetzt die vier fast identischen Konstruktoren.
/// </summary>
public sealed record RefinementOptions {
    public required LatexRefinementSessionConfig Config { get; init; }

    /// <summary>The single .tex file to refine, when the caller has one specific target.</summary>
    public string? SingleFilePath { get; init; }

    /// <summary>An explicit set of .tex files, treated as parts of one document.</summary>
    public string[]? MultipleFilePaths { get; init; }

    /// <summary>
    /// [AI Context] The extraction config the .tex came from. Its presence is what enables the
    /// prerequisite checks in <c>StartAsync</c> (<c>GoIntoLatexRefinement</c>,
    /// <c>GenerateOffsetFiles</c>, <c>GenerateAudioFile</c>) and supplies <c>NumberOfParts</c>.
    /// Null when refinement runs standalone rather than as the tail of an extraction.
    /// [Human] Die Extraktions-Konfiguration, aus der die Datei stammt; null bei eigenständigem Lauf.
    /// </summary>
    public IAutoExtractionConfig? ExtractionConfig { get; init; }

    public string? AudioFilePath { get; init; }

    /// <summary>
    /// [AI Context] Audio parts already uploaded by the extraction session's parallel upload, passed
    /// through so the refinement does not re-upload the same file.
    /// [Human] Bereits hochgeladene Audio-Teile, damit nicht doppelt hochgeladen wird.
    /// </summary>
    public List<Part>? PreUploadedAudioAttachments { get; init; }

    /// <summary>
    /// [AI Context] Refine one specific .tex file. This is what every call site in the app uses:
    /// both extraction sessions after a successful run, and the interactive refinement menu.
    /// [Human] Verarbeitet genau eine .tex-Datei.
    /// </summary>
    public static RefinementOptions ForFile(
        LatexRefinementSessionConfig config,
        string filePath,
        IAutoExtractionConfig? extractionConfig = null,
        string? audioFilePath = null,
        List<Part>? preUploadedAudioAttachments = null) => new() {
            Config = config,
            SingleFilePath = filePath,
            ExtractionConfig = extractionConfig,
            AudioFilePath = audioFilePath,
            PreUploadedAudioAttachments = preUploadedAudioAttachments
        };

    /// <summary>
    /// [AI Context] Refine an explicit list of .tex parts. Nothing in the app reaches this today -
    /// the extraction sessions merge to one file before calling - but the pipeline supports it and
    /// step 1 (merge) is written for exactly this shape.
    /// [Human] Verarbeitet mehrere .tex-Teile als ein Dokument.
    /// </summary>
    public static RefinementOptions ForFiles(
        LatexRefinementSessionConfig config,
        string[] filePaths,
        IAutoExtractionConfig? extractionConfig = null,
        string? audioFilePath = null,
        List<Part>? preUploadedAudioAttachments = null) => new() {
            Config = config,
            MultipleFilePaths = filePaths,
            ExtractionConfig = extractionConfig,
            AudioFilePath = audioFilePath,
            PreUploadedAudioAttachments = preUploadedAudioAttachments
        };

    /// <summary>
    /// [AI Context] Refine every .tex in <c>config.SourceFolder</c>. The standalone batch mode; the
    /// prerequisite checks in <c>StartAsync</c> are skipped because there is no extraction config to
    /// check against.
    /// [Human] Verarbeitet alle .tex-Dateien im konfigurierten Quellordner.
    /// </summary>
    public static RefinementOptions ForConfiguredFolder(LatexRefinementSessionConfig config) => new() {
        Config = config
    };
}
