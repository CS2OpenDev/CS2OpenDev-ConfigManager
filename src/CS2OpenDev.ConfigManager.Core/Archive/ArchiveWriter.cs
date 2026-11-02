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
    ///     given one, and at the <see cref="TrustLevel.Hash" /> level otherwise. The zip is written beside
    ///     <paramref name="zipPath" /> and moved into place only when complete; an existing file there is never
    ///     replaced.
    /// </summary>
    public static ArchiveManifest Write(string stagingDir, string zipPath, ArchiveContent content, DateTimeOffset created, CancellationToken ct,
        SealKey? seal = null)
    {
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
            if (relative is ArchiveFormat.ManifestEntry or ArchiveFormat.SignatureEntry or ArchiveFormat.ReadmeEntry)
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

        List<ArchiveEntry> entries = [.. data.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ArchiveEntry(p.Key, p.Value.LongLength, Dsse.Sha256Hex(p.Value)))];
        CheckLimits(entries);

        ArchiveManifest manifest = new(ArchiveFormat.Format, content.Platform, created.ToUniversalTime(), content.Tool,
            content.SteamAccount, content.Sections, entries);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveJson.Strict.ArchiveManifest);
        byte[] envelopeBytes = Envelope(manifestBytes, seal);

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
            if (entry.Size > ArchiveFormat.MaxEntryBytes)
            {
                throw new IOException($"{entry.Path} is over {ArchiveFormat.MaxEntryBytes / (1024 * 1024)} MB.");
            }

            total += entry.Size;
        }

        if (total > ArchiveFormat.MaxTotalBytes)
        {
            throw new IOException($"A backup holds at most {ArchiveFormat.MaxTotalBytes / (1024 * 1024)} MB.");
        }
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
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }
}
