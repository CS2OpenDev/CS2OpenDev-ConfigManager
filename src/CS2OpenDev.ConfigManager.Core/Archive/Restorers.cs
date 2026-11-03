namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>Where a self-contained archive's restorer runs from, and the backup it came in.</summary>
public static class Restorers
{
    /// <summary>
    ///     The backup <paramref name="executable" /> was unpacked from, or null. Windows' Extract All and
    ///     <c>unzip x.zip -d x</c> both put the folder beside the zip under the zip's own name, so a restorer at
    ///     <c>dir/name/restorer/acb</c> looks for <c>dir/name.zip</c>. The zip is only proposed: it is opened and
    ///     checked like any other, and the unpacked folder is never read.
    /// </summary>
    public static string? HomeArchive(string? executable)
    {
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        string? restorerDir = Path.GetDirectoryName(Path.GetFullPath(executable));
        if (restorerDir is null
            || !string.Equals(Path.GetFileName(restorerDir), ArchiveFormat.RestorerFolder.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? unpacked = Path.GetDirectoryName(restorerDir);
        string? beside = unpacked is null ? null : Path.GetDirectoryName(unpacked);
        if (unpacked is null || beside is null)
        {
            return null;
        }

        string zip = Path.Combine(beside, Path.GetFileName(unpacked) + ".zip");
        return File.Exists(zip) ? zip : null;
    }

    /// <summary>
    ///     Why <paramref name="executable" /> cannot be carried as a <paramref name="platform" /> restorer, or null.
    ///     Only a published acb can be, named <c>acb</c> or as a release names it (<c>acb-win-x64.exe</c>,
    ///     <c>acb-linux-x64</c>): a test host or <c>dotnet acb.dll</c> would carry the wrong program. It goes in
    ///     under <see cref="ArchiveFormat.RestorerEntry" /> whatever its own name.
    /// </summary>
    public static string? Problem(string? executable, string platform)
    {
        if (string.IsNullOrEmpty(executable) || !File.Exists(executable))
        {
            return "acb cannot find its own program file to carry";
        }

        string expected = ArchiveFormat.RestorerFileName(platform);
        string extension = Path.GetExtension(expected);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string name = Path.GetFileName(executable);
        string stem = Path.GetFileNameWithoutExtension(name);
        bool published = string.Equals(Path.GetExtension(name), extension, comparison)
                         && (string.Equals(stem, "acb", comparison) || stem.StartsWith("acb-", comparison));
        return published ? null : $"this process runs as {name}, not a published {expected}, so it has no restorer to carry";
    }
}
