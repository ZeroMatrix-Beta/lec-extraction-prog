namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] How the inlined preceding parts are slimmed, one profile per kind of part: OlderParts for
/// every part except the one directly before the current part (only while SlimOlderReferenceParts is
/// on), PrecedingPart for that one. Applied after StripCommentsFromReferenceContext. The class defaults
/// are the behaviour from before this setting existed (2026-10-16): older parts lose speech bodies and
/// TikZ, the preceding part goes in complete. The preamble's rule 5 is built from both profiles.
/// [Human] Wie die eingebetteten Vorgänger-Teile gekürzt werden: ein Profil für ältere Teile, eins für
/// den direkten Vorgänger.
/// </summary>
public class ReferenceSlimmingConfig {
    public ReferenceSlimProfile OlderParts { get; set; } = new() { RemoveEnvironments = ["tikzpicture"], EmptyEnvironments = ["speech"] };
    public ReferenceSlimProfile PrecedingPart { get; set; } = new();
}

/// <summary>
/// [AI Context] One call of ReferenceContextSlimmer: what it takes out of one file. Empty by default,
/// i.e. the file stays as it is.
/// [Human] Was der Slimmer bei einem Aufruf aus einer Datei entfernt.
/// </summary>
public class ReferenceSlimProfile {
    /// <summary>Remove LaTeX comments and `hidden` environments first (as StripCommentsFromReferenceContext does).</summary>
    public bool StripCommentsAndHidden { get; set; } = false;

    /// <summary>
    /// Environments removed entirely, begin to end. `\label`s inside them are kept, one per line, so
    /// references to them still resolve for the model.
    /// </summary>
    public string[] RemoveEnvironments { get; set; } = [];

    /// <summary>Environments whose body is replaced by `% tokenOpt`; the begin line (with its timestamp) and the body's `\label`s stay.</summary>
    public string[] EmptyEnvironments { get; set; } = [];

    /// <summary>Commands removed with their one braced argument, e.g. `inlinemetanote`.</summary>
    public string[] RemoveCommands { get; set; } = [];

    /// <summary>Leave `% tokenOpt (name)` where a removed environment stood. Off: nothing is left.</summary>
    public bool MarkRemovedEnvironments { get; set; } = true;

    /// <summary>Whether this profile removes anything beyond comments and `hidden` blocks (decides the log line).</summary>
    public bool RemovesMoreThanComments() => RemoveEnvironments.Length > 0 || EmptyEnvironments.Length > 0 || RemoveCommands.Length > 0;
}
