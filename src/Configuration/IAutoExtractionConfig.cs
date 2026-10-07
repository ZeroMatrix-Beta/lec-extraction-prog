namespace LectureExtraction.Configuration;

public interface IAutoExtractionConfig {
    double SpeedMultiplier { get; }
    bool GoIntoLatexRefinement { get; }
    bool UseChosenModelForRestOfPipeline { get; }
    bool GenerateOffsetFiles { get; }
    bool GenerateAudioFile { get; }
    NumberOfParts NumberOfParts { get; }
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
    bool SendDummyFileDuringTranscription { get; }
    bool InlinePrecedingLecTexParts { get; }
    bool VerboseConsoleOutput { get; }
    bool OnlyDoWarmUp { get; }
    int HighDemandDelaySeconds { get; }
    int MaxContinueRequests { get; }
}

