using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The engine over a capture folder (backup.vdf beside files/), with the mode named by the caller as acb names it
///     from the command. What a folder says about itself never makes a restore an undo.
/// </summary>
internal static class Engine
{
    public static RunReport Restore(string captureDir, RestoreOptions options) =>
        ConfigBackup.Restore(new FolderSource(captureDir), RestoreMode.Restore, options, null);

    public static RunReport Undo(string recordDir, RestoreOptions options) =>
        ConfigBackup.Restore(new FolderSource(recordDir), RestoreMode.Undo, options, null);
}
