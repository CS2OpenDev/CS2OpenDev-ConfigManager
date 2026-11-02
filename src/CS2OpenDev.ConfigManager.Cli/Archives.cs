using System.Globalization;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>Opening archives and policies the one way every command does, and naming what they say.</summary>
internal static class Archives
{
    /// <summary>Opens and verifies a v2 archive; null with <paramref name="problem" /> set when it is refused. See <see cref="RestoreSession.OpenArchive(string, string?, TrustLevel, out string?)" />.</summary>
    public static VerifiedArchive? Open(string path, string? requirePlatform, TrustLevel minimumLevel, out string? problem) =>
        RestoreSession.OpenArchive(path, requirePlatform, minimumLevel, out problem);

    /// <summary>Loads a policy file; null with <paramref name="problem" /> set when it is refused, or when none was given.</summary>
    public static OrganiserPolicy? Policy(string? path, out string? problem) => RestoreSession.LoadPolicy(path, out problem);

    /// <summary>Section names from --only, each checked: a misspelt one is a usage error, never a silent "nothing".</summary>
    public static List<string>? Sections(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (!Kit.Sections.All.Contains(name, StringComparer.Ordinal))
            {
                throw new UsageException($"'{name}' is not a section. Sections: {string.Join(", ", Kit.Sections.All)}.");
            }
        }

        return [.. names.Distinct(StringComparer.Ordinal)];
    }

    public static string LevelText(TrustLevel level) => level switch
    {
        TrustLevel.Hash => "hash (unsigned: anyone could have edited this; it shows only that the files match the manifest)",
        TrustLevel.Seal => "seal (sealed with a passphrase)",
        _ => "identity (signed for a Steam account)"
    };

    /// <summary>What every command shows first: which backup, at which level, made when and by what.</summary>
    public static void Header(Output output, string path, VerifiedArchive archive)
    {
        ArchiveManifest manifest = archive.Manifest;
        CommandReport report = output.Report;
        report.Archive = Path.GetFullPath(path);
        report.ArchiveSha256 = archive.ManifestSha256;
        report.Level = ArchiveReader.Name(archive.Level);
        report.Platform = manifest.Platform;
        report.Created = manifest.Created;
        report.SteamAccount = manifest.SteamAccount;
        output.Line("Backup:   " + report.Archive);
        output.Line("Level:    " + LevelText(archive.Level));
        output.Line(string.Create(CultureInfo.InvariantCulture,
            $"Made:     {manifest.Created:yyyy-MM-dd HH:mm}Z by {manifest.Tool.Name} {manifest.Tool.Version}, on {manifest.Platform}"));
        if (manifest.SteamAccount is not null)
        {
            output.Line("Account:  Steam account " + manifest.SteamAccount);
        }

        output.Line("Digest:   " + archive.ManifestSha256);
    }

    public static string Version() =>
        typeof(Archives).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
