namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] The folder the interactive refinement menu offers first. Saved by the menu when the
/// user picks another one; empty until then.
/// [Human] Der Ordner, den das Refinement-Menü zuerst anbietet.
/// </summary>
public class RefinementUiHelperConfig {
    public string PredefinedPath { get; set; } = "";
}
