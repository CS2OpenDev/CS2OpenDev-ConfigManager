using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>What an archive says about itself, apart from its entries.</summary>
public sealed record ArchiveContent(
    string Platform,
    ArchiveTool Tool,
    string? SteamAccount,
    IReadOnlyList<ArchiveSection> Sections,
    string Readme);

/// <summary>Writes a v2 archive from a staging folder holding <c>capture.vdf</c> and <c>files/</c>.</summary>
public static class ArchiveWriter
{
    /// <summary>
    ///     Zips every file under <paramref name="stagingDir" /> with a manifest naming each one, a README, and a
    ///     DSSE envelope: at the <see cref="TrustLevel.Seal" /> level, signed with <paramref name="seal" />, when
    ///     given one, and at the <see cref="TrustLevel.Hash" /> level otherwise. With <paramref name="restorer" />, the
    ///     archive is self-contained: those bytes go in as the platform's <see cref="ArchiveFormat.RestorerEntry" />,
    ///     listed in the manifest like any entry. The zip is written beside <paramref name="zipPath" /> and moved into
    ///     place only when complete; an existing file there is never replaced. When <paramref name="identity" />
    ///     signs for another account than the one whose settings these are, or the settings name no account,
    ///     <paramref name="otherSigner" /> is asked with what to say, and without it, or with no for an answer,
    ///     nothing is written.
    /// </summary>
    public static ArchiveManifest Write(string stagingDir, string zipPath, ArchiveContent content, DateTimeOffset created, CancellationToken ct,
        SealKey? seal = null, byte[]? restorer = null, IIdentitySigner? identity = null, Func<string, bool>? otherSigner = null)
    {
        if (seal is not null && identity is not null)
        {
            throw new ArgumentException("A backup is sealed or signed for a Steam account, not both.", nameof(identity));
        }

        if (!Platforms.IsKnown(content.Platform))
        {
            throw new ArgumentException($"Unknown platform '{content.Platform}'.", nameof(content));
        }

        if (File.Exists(zipPath))
        {
            throw new IOException($"{zipPath} already exists.");
        }

        Dictionary<string, byte[]> data = new(StringComparer.Ordinal)
        {
            [ArchiveFormat.ReadmeEntry] = Encoding.UTF8.GetBytes(content.Readme.ReplaceLineEndings("\r\n"))
        };
        foreach (string file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"{file} is a link; a backup holds only files it read itself.");
            }

            string relative = Path.GetRelativePath(stagingDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative is ArchiveFormat.ManifestEntry or ArchiveFormat.SignatureEntry or ArchiveFormat.ReadmeEntry
                || relative.StartsWith(ArchiveFormat.RestorerFolder, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The staging folder holds {relative}, a name the archive writes itself.");
            }

            string? problem = ArchivePaths.Problem(relative);
            if (problem != null)
            {
                throw new IOException($"'{relative}' is {problem}, so it cannot go into a backup.");
            }

            data[relative] = File.ReadAllBytes(file);
        }

        if (restorer is not null)
        {
            data[ArchiveFormat.RestorerEntry(content.Platform)] = restorer;
        }

        List<ArchiveEntry> entries = [.. data.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ArchiveEntry(p.Key, p.Value.LongLength, Dsse.Sha256Hex(p.Value)))];
        CheckLimits(entries);

        ArchiveManifest manifest = new(ArchiveFormat.Format, content.Platform, created.ToUniversalTime(), content.Tool,
            content.Sections, entries, content.SteamAccount);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveJson.Strict.ArchiveManifest);
        byte[] envelopeBytes = identity is null ? Envelope(manifestBytes, seal) : Signed(manifestBytes, content, identity, otherSigner);

        string partial = zipPath + ".partial";
        File.Delete(partial);
        try
        {
            using (FileStream stream = new(partial, FileMode.CreateNew, FileAccess.Write))
            using (ZipArchive zip = new(stream, ZipArchiveMode.Create))
            {
                Add(zip, ArchiveFormat.ManifestEntry, manifestBytes, created);
                Add(zip, ArchiveFormat.SignatureEntry, envelopeBytes, created);
                foreach (ArchiveEntry entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    Add(zip, entry.Path, data[entry.Path], created);
                }
            }

            File.Move(partial, zipPath, overwrite: false);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return manifest;
    }

    internal static void CheckLimits(IReadOnlyList<ArchiveEntry> entries)
    {
        if (entries.Count + 2 > ArchiveFormat.MaxEntries)
        {
            throw new IOException($"A backup holds at most {ArchiveFormat.MaxEntries} files.");
        }

        long total = 0;
        foreach (ArchiveEntry entry in entries)
        {
            long cap = ArchiveFormat.EntryCap(entry.Path);
            if (entry.Size > cap)
            {
                throw new IOException($"{entry.Path} is over {cap / (1024 * 1024)} MB.");
            }

            total += entry.Size;
        }

        if (total > ArchiveFormat.MaxTotalBytes)
        {
            throw new IOException($"A backup holds at most {ArchiveFormat.MaxTotalBytes / (1024 * 1024)} MB.");
        }
    }

    /// <summary>
    ///     The signer's envelope, checked here as any reader checks it (against this build's roots, for this manifest)
    ///     before it is written, so no backup goes out that its own acb would refuse. A signer other than the
    ///     settings' account (someone else signed in, or another account's sign-in raced this one) is never written
    ///     without a yes, nor is any signer when the settings name no account to compare it with.
    /// </summary>
    private static byte[] Signed(byte[] manifestBytes, ArchiveContent content, IIdentitySigner identity, Func<string, bool>? otherSigner)
    {
        string manifestSha = Dsse.Sha256Hex(manifestBytes);
        DsseEnvelope envelope = identity.Sign(manifestSha, content.Platform, content.Tool);
        byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ArchiveJson.Strict.DsseEnvelope);
        IdentitySigner signer;
        try
        {
            signer = ArchiveReader.CheckEnvelope(envelopeBytes, manifestSha, TrustLevel.Identity, IdentityTrust.Current, DateTimeOffset.UtcNow)!;
        }
        catch (ArchiveRejectedException ex)
        {
            throw new IOException("The signature the service returned does not check: " + ex.Message
                                  + (ex.MayBeNewer ? " If a newer acb is out, update to it and sign again." : ""), ex);
        }

        string? said = content.SteamAccount is null
            ? $"The service signed this backup for Steam {Identity.Account(signer.AccountId)}, and the settings name no Steam account to check that against. "
              + "If you did not sign in as that account in the browser, someone else's sign-in reached acb first."
            : signer.OtherAccount(content.SteamAccount) is not null
                ? $"The service signed this backup for Steam {Identity.Account(signer.AccountId)}, but the settings are from {Identity.Account(content.SteamAccount)}. "
                  + "If you did not sign in as that account in the browser, someone else's sign-in reached acb first."
                : null;
        if (said is not null)
        {
            if (otherSigner?.Invoke(said) != true)
            {
                throw new IOException(said + " No backup was written.");
            }
        }

        return envelopeBytes;
    }

    private static byte[] Envelope(byte[] manifestBytes, SealKey? seal)
    {
        Statement statement = new(ArchiveFormat.StatementType,
            [new StatementSubject(ArchiveFormat.ManifestEntry, new DigestSet(Dsse.Sha256Hex(manifestBytes)))],
            ArchiveFormat.PredicateType, seal is null ? new TrustPredicate("hash") : new TrustPredicate("seal", seal.Kdf));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(statement, ArchiveJson.Strict.Statement);
        DsseSignature[] signatures = seal is null
            ? []
            : [new DsseSignature(Seal.KeyId, Convert.ToBase64String(seal.Sign(Dsse.PreAuthEncoding(ArchiveFormat.PayloadType, payload))))];
        DsseEnvelope envelope = new(ArchiveFormat.PayloadType, Convert.ToBase64String(payload), signatures);
        return JsonSerializer.SerializeToUtf8Bytes(envelope, ArchiveJson.Strict.DsseEnvelope);
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes, DateTimeOffset created)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = created;
        // unzip and file managers on Linux give an entry the Unix mode stored with it, so the restorer unpacks runnable.
        if (name == ArchiveFormat.RestorerEntry(Platforms.LinuxX64))
        {
            entry.ExternalAttributes = Convert.ToInt32("100755", 8) << 16;
        }

        using Stream stream = entry.Open();
        stream.Write(bytes);
    }
}
