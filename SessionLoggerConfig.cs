using Config;

namespace Infrastructure;

/// <summary>
/// Configuration for the SessionLogger, defining where log files should be stored.
/// </summary>
public class SessionLoggerConfig {
  public string LogFolderPath { get; set; } = AppConfig.LogFolder;
}