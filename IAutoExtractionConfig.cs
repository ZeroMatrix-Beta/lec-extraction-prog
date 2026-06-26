namespace Config;

public interface IAutoExtractionConfig {
    bool GoIntoLatexRefinement { get; }
    bool GenerateOffsetFiles { get; }
    bool GenerateAudioFile { get; }
    int NumberOfParts { get; }
    int OverlapSeconds { get; }
    string TargetFolder { get; }
    bool CreateLogFiles { get; }
    double? GoogleVideoFps { get; }
}
