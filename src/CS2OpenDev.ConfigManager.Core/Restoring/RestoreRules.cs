using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>Built-in rules about who applies a section, which an archive or a policy cannot change.</summary>
public static class RestoreRules
{
    /// <summary>
    ///     Whether a section is applied only by an elevated process, into the administrator-only undo store. On
    ///     Windows that is NVIDIA's driver settings, which the driver saves only for an administrator. Power plans
    ///     and vibrance are machine-wide but Windows lets a standard user change them, so they run as the player
    ///     and their records stay in the player's store. A name that is not a section throws.
    /// </summary>
    public static bool NeedsAdministrator(string section) =>
        Sections.Scope(section) == SectionScope.Machine && OperatingSystem.IsWindows() && section == Sections.Nvidia;

    /// <summary>
    ///     Whether an administrator's acb leaves a section, restore or undo: it is written through the Steam folder,
    ///     which other users of the PC can change, so a link planted there would turn the write into one anywhere.
    /// </summary>
    public static bool LeftByAdministrator(string section) => Sections.NeedsSteam(section);

    /// <summary>The sections only an elevated run applies, in <see cref="Sections.All" /> order.</summary>
    public static IReadOnlyList<string> AdministratorSections => [.. Sections.All.Where(NeedsAdministrator)];
}
