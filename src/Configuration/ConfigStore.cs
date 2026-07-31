using System;
using System.IO;

namespace LectureExtraction.Configuration;

/// <summary>
/// Process-wide policy for where <see cref="ConfigLoader{T}"/> reads and whether it may write.
/// <c>ConfigLoader&lt;T&gt;</c> is a static generic, so its statics are per-<c>T</c> and cannot hold
/// a setting that applies to all config types - this non-generic type does.
///
/// <para>Both settings exist for unattended runs. The app rewrites its own <c>*Config.json</c>
/// mid-run, so a scripted run would otherwise change the settings the interactive user comes back
/// to; and several runs sharing one directory would fight over the same file, which is why the
/// documented way to run instances in parallel used to be copying the whole output folder.</para>
/// </summary>
public static class ConfigStore {
    /// <summary>
    /// Whether <see cref="ConfigLoader{T}.Save"/> may write. Interactive runs leave this on; the
    /// CLI turns it off unless <c>--save-config</c> was passed.
    /// </summary>
    public static bool SaveEnabled { get; set; } = true;

    /// <summary>
    /// Where the <c>*Config.json</c> live. Null means the executable's folder, which is what the
    /// app has always used.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    /// <summary>The directory configuration is read from, and (when overridden) the only one written to.</summary>
    public static string ResolveDirectory() =>
        string.IsNullOrWhiteSpace(DirectoryOverride)
            ? AppDomain.CurrentDomain.BaseDirectory
            : Path.GetFullPath(DirectoryOverride);

    /// <summary>Resets to the interactive defaults. Exists so tests cannot leak policy into each other.</summary>
    public static void Reset() {
        SaveEnabled = true;
        DirectoryOverride = null;
    }
}
