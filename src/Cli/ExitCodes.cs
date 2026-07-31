namespace LectureExtraction.Cli;

/// <summary>
/// The process exit codes the CLI promises. They are part of the public contract an automation
/// caller depends on, so they are frozen here rather than spelled as magic numbers at each return
/// site. <see cref="Partial"/> exists because a batch can finish having transcribed some videos and
/// failed others - a condition that is only a scrolling warning in the interactive app, but has to
/// be visible in the exit status for an unattended caller.
/// </summary>
public static class ExitCodes {
    /// <summary>Everything requested completed.</summary>
    public const int Success = 0;

    /// <summary>An unhandled exception escaped the command.</summary>
    public const int Unexpected = 1;

    /// <summary>The arguments could not be parsed, or were contradictory.</summary>
    public const int Usage = 2;

    /// <summary>A prompt was reached that had no answer supplied and no safe default.</summary>
    public const int UnattendedPrompt = 3;

    /// <summary>Configuration or credentials are missing/unusable (e.g. an unset API-key variable).</summary>
    public const int Configuration = 4;

    /// <summary>The API kept failing until the retry policy gave up.</summary>
    public const int ApiExhausted = 5;

    /// <summary>Some work items succeeded and others failed; the payload names which.</summary>
    public const int Partial = 6;
}
