using System.Globalization;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>Opening archives and policies the one way every command does, and naming what they say.</summary>
internal static class Archives
{
    /// <summary>
    ///     Opens and verifies a v2 archive; null with <paramref name="problem" /> set when it is refused. A sealed one
    ///     is opened with what <paramref name="ask" /> gives. See
    ///     <see cref="RestoreSession.OpenArchive(string, string?, TrustLevel, out string?, PassphrasePrompt, byte[])" />.
    /// </summary>
    public static VerifiedArchive? Open(string path, string? requirePlatform, TrustLevel minimumLevel, out string? problem, PassphrasePrompt? ask = null) =>
        RestoreSession.OpenArchive(path, requirePlatform, minimumLevel, out problem, ask);

    /// <summary>
    ///     Where a sealed backup's passphrase comes from for one command: the file --passphrase-file names (once; a
    ///     passphrase in a file that does not open the seal is not asked for again), or else the person at the
    ///     terminal, without showing it. Null when neither is there, so the refusal says how to give one.
    /// </summary>
    public static PassphrasePrompt? Passphrase(CommandLine line, AcbHost host)
    {
        if (line.Value("passphrase-file") is { } file)
        {
            string passphrase = PassphraseFile(file, host);
            return again => again ? null : passphrase;
        }

        return host.Interactive
            ? again => host.ReadSecret(again ? "That passphrase does not open the seal. Passphrase (Enter alone to stop): " : "This backup is sealed. Passphrase: ")
            : null;
    }

    /// <summary>
    ///     A passphrase file's text: its first line, without the line break, or standard input's first line for
    ///     <c>-</c>. Nothing else is trimmed, as a space may be part of the passphrase.
    /// </summary>
    public static string PassphraseFile(string file, AcbHost host)
    {
        string? text;
        try
        {
            text = file == "-" ? host.ReadLine() : File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UsageException($"The passphrase file {file} cannot be read: {ex.Message}");
        }

        int end = text?.IndexOfAny(['\r', '\n']) ?? -1;
        text = end >= 0 ? text![..end] : text;
        return string.IsNullOrEmpty(text) ? throw new UsageException($"The passphrase file {file} holds no passphrase on its first line.") : text;
    }

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
        TrustLevel.Seal => "seal (opened with its passphrase: made by someone who knows it, and unchanged since)",
        _ => "identity (signed through the CS2OpenDev service for the Steam account below: made by that account, unchanged since)"
    };

    /// <summary>What every command shows first: which backup, at which level, made when and by what, and its restorer.</summary>
    public static void Header(Output output, string path, VerifiedArchive archive, AcbHost host)
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
        foreach (string line in SignerLines(archive))
        {
            output.Line(line);
        }

        if (archive.IdentitySigner is { } signer)
        {
            report.SignedBy = signer.SteamId64;
            report.SignedAt = signer.SignedAt;
        }

        if (RestorerLine(archive, host) is { } restorer)
        {
            report.RestorerSha256 = archive.Restorer!.Sha256;
            report.RestorerIsThisAcb = IsThisAcb(archive, host);
            output.Line(restorer);
        }
    }

    /// <summary>
    ///     Who an identity-level backup is signed for, when, with which key; and when the settings are another
    ///     account's, that too. Empty below the identity level.
    /// </summary>
    public static IReadOnlyList<string> SignerLines(VerifiedArchive archive)
    {
        if (archive.IdentitySigner is not { } signer)
        {
            return [];
        }

        List<string> lines =
        [
            string.Create(CultureInfo.InvariantCulture, $"Signed:   Steam account {signer.SteamId64}, {signer.SignedAt:yyyy-MM-dd HH:mm}Z (key {signer.Keyid[..16]})")
        ];
        string account = (long.Parse(signer.SteamId64, CultureInfo.InvariantCulture) - 76561197960265728L).ToString(CultureInfo.InvariantCulture);
        if (archive.Manifest.SteamAccount is { } settings && settings != account)
        {
            lines.Add($"          the settings are account {Output.Printable(settings)}'s; account {account} signed them");
        }

        return lines;
    }

    /// <summary>
    ///     What a self-contained backup's restorer is, against this acb: the same bytes, or another build to check
    ///     against its release's SHA256SUMS before running it. Null when the backup carries none.
    /// </summary>
    public static string? RestorerLine(VerifiedArchive archive, AcbHost host)
    {
        if (archive.Restorer is not { } restorer)
        {
            return null;
        }

        return IsThisAcb(archive, host)
            ? $"Restorer: {restorer.Path}, the same program as this acb ({restorer.Sha256})"
            : $"Restorer: {restorer.Path} ({restorer.Sha256}), not this acb's build. Before running it, check that SHA-256 against "
              + "the SHA256SUMS of the acb release it should be.";
    }

    private static bool IsThisAcb(VerifiedArchive archive, AcbHost host) =>
        archive.Restorer is { } restorer && OwnSha256(host.Executable) is { } own && own == restorer.Sha256;

    private static readonly Dictionary<string, string?> OwnHashes = new(StringComparer.Ordinal);

    /// <summary>The SHA-256 of this acb's program file, read once per path; null when it cannot be read.</summary>
    private static string? OwnSha256(string? executable)
    {
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        lock (OwnHashes)
        {
            if (!OwnHashes.TryGetValue(executable, out string? sha))
            {
                try
                {
                    using FileStream file = new(executable, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sha = null;
                }

                OwnHashes[executable] = sha;
            }

            return sha;
        }
    }

    public static string Version() =>
        typeof(Archives).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
