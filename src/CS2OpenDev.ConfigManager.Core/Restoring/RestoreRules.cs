using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>Built-in rules about who applies a section, which an archive or a policy cannot change.</summary>
public static class RestoreRules
{
    /// <summary>
    ///     Whether a section is applied only by an elevated process, into the administrator-only undo store. On
    ///     Windows that is NVIDIA's driver settings, which the driver saves only for an administrator. Power plans
    ///     and vibrance are machine-wide but Windows lets a standard user change them, so they run as the player
    ///     and their records stay in the player's store.
    /// </summary>
    public static bool NeedsAdministrator(string section) => OperatingSystem.IsWindows() && section == Sections.Nvidia;
}
