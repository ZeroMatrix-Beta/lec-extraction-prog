using Config;

namespace FfmpegUtilities;

/// <summary>
/// [AI Context] Configuration DTO for the interactive FFmpeg session.
/// Binds to FfmpegSessionConfig.json and the FfmpegSessionConfig section in appsettings.json.
/// </summary>
public class FfmpegSessionConfig {
  public string SourceFolder { get; set; } = AppConfig.FfmpegSourceFolder;
  public string TargetFolder { get; set; } = AppConfig.FfmpegTargetFolder;
}