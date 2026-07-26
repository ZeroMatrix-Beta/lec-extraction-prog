namespace LectureExtraction.Configuration;

public interface IAutoExtractionConfig {
    bool GoIntoLatexRefinement { get; }
    bool UseChosenModelForRestOfPipeline { get; }
    bool GenerateOffsetFiles { get; }
    bool GenerateAudioFile { get; }
    int NumberOfParts { get; }
    int OverlapSeconds { get; }
    string SourceFolder { get; }
    string[] PredefinedSourceFolders { get; }
    string TargetFolder { get; }
    bool CreateLogFiles { get; }
    double? GoogleVideoFps { get; }
    bool UseGoogleSearch { get; }
    YouTubeTranscriptionTask[] YouTubeTasks { get; }
    string FfmpegPreset { get; }
    bool DebugSendReferenceFile { get; }
    bool InlinePrecedingLecTexParts { get; }
}
