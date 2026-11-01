namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>
///     The one spelling an archive path may have: forward slashes, relative, and nothing Windows or Linux would read
///     as a different file. Signed JAR and APK v1 fell to paths two readers understood differently; refusing every
///     ambiguous spelling is cheaper than reasoning about each.
/// </summary>
public static class ArchivePaths
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Why <paramref name="path" /> is not a valid archive path, or null when it is.</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "an empty path";
        }

        if (path.Length > ArchiveFormat.MaxPathLength)
        {
            return "a path over " + ArchiveFormat.MaxPathLength + " characters";
        }

        foreach (char c in path)
        {
            if (c < 0x20 || c == 0x7F || c is '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*')
            {
                return "a path with a character Windows or Linux treats specially";
            }
        }

        if (path[0] == '/')
        {
            return "an absolute path";
        }

        foreach (string segment in path.Split('/'))
        {
            if (segment.Length == 0)
            {
                return "a path with an empty segment";
            }

            if (segment is "." or "..")
            {
                return "a path that climbs out of its folder";
            }

            // Windows drops a trailing dot or space, so "a." and "a" would be one file there and two here.
            if (segment[^1] is '.' or ' ')
            {
                return "a path segment ending in a dot or a space";
            }

            int dot = segment.IndexOf('.');
            if (DeviceNames.Contains(dot < 0 ? segment : segment[..dot]))
            {
                return "a path naming a Windows device";
            }
        }

        return null;
    }

    /// <summary>
    ///     <paramref name="root" /> joined with <paramref name="path" />, after checking the result stays inside
    ///     <paramref name="root" />. Throws for an invalid path.
    /// </summary>
    public static string Under(string root, string path)
    {
        string? problem = Problem(path);
        if (problem != null)
        {
            throw new ArgumentException($"'{path}' is {problem}.", nameof(path));
        }

        string full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison) ? full : throw new ArgumentException($"'{path}' leaves {root}.", nameof(path));
    }
}
