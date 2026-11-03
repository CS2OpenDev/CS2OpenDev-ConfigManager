using System.IO.Compression;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>Why an archive was refused. <see cref="Exception.Message" /> is written for the person restoring.</summary>
public class ArchiveRejectedException(string message) : Exception(message);

/// <summary>
///     A sealed archive that was not opened: no passphrase was given (<see cref="Wrong" /> false), or the one given
///     does not open it (true), which is also what an archive changed after it was sealed looks like.
/// </summary>
public sealed class SealNotOpenedException(string message, bool wrong) : ArchiveRejectedException(message)
{
    public const string NeededMessage = "This backup is sealed with a passphrase, and none was given.";

    public const string WrongMessage =
        "The passphrase does not open this backup's seal: it is not the one the backup was sealed with, or the backup was changed after it was sealed.";

    public bool Wrong { get; } = wrong;
}

public sealed record ArchiveReadOptions
{
    /// <summary>The platform the archive must be for; null to read any (checking or showing, never restoring).</summary>
    public string? RequirePlatform { get; init; } = ArchiveFormat.CurrentPlatform;

    public TrustLevel MinimumLevel { get; init; } = TrustLevel.Hash;

    /// <summary>The passphrase to open a sealed archive with; unused for any other.</summary>
    public string? Passphrase { get; init; }

    /// <summary>A sealed archive's key, handed on by the run that derived it; tried before <see cref="Passphrase" />.</summary>
    internal byte[]? SealKey { get; init; }
}

/// <summary>
///     An archive whose every entry matched its manifest when read. The bytes are held in memory from that one
///     read, so a file swapped on disk afterwards cannot reach a restore.
/// </summary>
public sealed class VerifiedArchive
{
    private readonly Dictionary<string, byte[]> _entries;

    internal VerifiedArchive(ArchiveManifest manifest, TrustLevel level, string manifestSha256, Dictionary<string, byte[]> entries,
        string? signer = null, SealKey? sealKey = null)
    {
        Manifest = manifest;
        Level = level;
        Signer = signer;
        SealKey = sealKey;
        ManifestSha256 = manifestSha256;
        _entries = entries;
    }

    public ArchiveManifest Manifest { get; }

    public TrustLevel Level { get; }

    /// <summary>
    ///     The SteamID64 whose signature was checked, at the identity level only; null below it. What the manifest
    ///     says about its own account is never a signer.
    /// </summary>
    public string? Signer { get; }

    /// <summary>The key that opened a sealed archive, so a second run (the administrator's) need not ask again; null below the seal level.</summary>
    public SealKey? SealKey { get; }

    /// <summary>The SHA-256 of manifest.json: one value that names this exact archive's content.</summary>
    public string ManifestSha256 { get; }

    /// <summary>The restorer this archive carries, as its manifest lists it; null when it is not self-contained.</summary>
    public ArchiveEntry? Restorer =>
        Manifest.Entries.FirstOrDefault(e => e.Path == ArchiveFormat.RestorerEntry(Manifest.Platform));

    public bool Contains(string path) => _entries.ContainsKey(path);

    public ReadOnlyMemory<byte> Read(string path) =>
        _entries.TryGetValue(path, out byte[]? bytes) ? bytes : throw new FileNotFoundException($"The backup has no {path}.");

    /// <summary>Writes every entry into <paramref name="emptyDir" />, which must exist and be empty.</summary>
    public void ExtractTo(string emptyDir)
    {
        if (!Directory.Exists(emptyDir) || Directory.EnumerateFileSystemEntries(emptyDir).Any())
        {
            throw new IOException($"{emptyDir} must be an existing, empty folder.");
        }

        foreach ((string path, byte[] bytes) in _entries)
        {
            string target = ArchivePaths.Under(emptyDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream stream = new(target, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
        }
    }
}

/// <summary>Opens a v2 archive and refuses anything that does not match its manifest exactly.</summary>
public static class ArchiveReader
{
    public static VerifiedArchive Open(string zipPath, ArchiveReadOptions options)
    {
        using FileStream file = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Open(file, options);
    }

    /// <summary>As <see cref="Open(string, ArchiveReadOptions)" />, from a zip's bytes that did not come from a path.</summary>
    public static VerifiedArchive Open(Stream zip, ArchiveReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Dictionary<string, byte[]> raw = ReadZip(zip);

        byte[] manifestBytes = Take(raw, ArchiveFormat.ManifestEntry);
        byte[] envelopeBytes = Take(raw, ArchiveFormat.SignatureEntry);
        ArchiveManifest manifest = Parse(manifestBytes, ArchiveJson.Strict.ArchiveManifest, ArchiveFormat.ManifestEntry);

        if (manifest.Format != ArchiveFormat.Format)
        {
            throw new ArchiveRejectedException($"This backup is format '{manifest.Format}', which this version does not read. A newer version may.");
        }

        if (!Platforms.IsKnown(manifest.Platform))
        {
            throw new ArchiveRejectedException($"This backup is for '{manifest.Platform}', a platform this version does not know.");
        }

        if (options.RequirePlatform != null && manifest.Platform != options.RequirePlatform)
        {
            throw new ArchiveRejectedException($"This backup was made on {manifest.Platform} and this PC is {options.RequirePlatform}. Backups restore only on the platform they were made on.");
        }

        string manifestSha = Dsse.Sha256Hex(manifestBytes);
        (TrustLevel level, SealKey? key) = VerifyEnvelope(envelopeBytes, manifestSha, options);
        if (level < options.MinimumLevel)
        {
            throw new ArchiveRejectedException($"This backup is at the '{Name(level)}' level and at least '{Name(options.MinimumLevel)}' is required.");
        }

        VerifyEntries(manifest, raw);
        return new VerifiedArchive(manifest, level, manifestSha, raw, sealKey: key);
    }

    public static string Name(TrustLevel level) => level switch
    {
        TrustLevel.Hash => "hash",
        TrustLevel.Seal => "seal",
        TrustLevel.Identity => "identity",
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    private static Dictionary<string, byte[]> ReadZip(Stream file)
    {
        Dictionary<string, byte[]> raw = new(StringComparer.Ordinal);
        HashSet<string> folded = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            using ZipArchive zip = new(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > ArchiveFormat.MaxEntries)
            {
                throw new ArchiveRejectedException($"The backup has more than {ArchiveFormat.MaxEntries} entries.");
            }

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                string? problem = ArchivePaths.Problem(name);
                if (problem != null)
                {
                    throw new ArchiveRejectedException($"The backup holds '{name}', which is {problem}.");
                }

                if (!folded.Add(name))
                {
                    throw new ArchiveRejectedException($"The backup holds '{name}' twice, counting upper and lower case as the same.");
                }

                if (entry.IsEncrypted)
                {
                    throw new ArchiveRejectedException($"The backup's '{name}' is encrypted.");
                }

                byte[] bytes = ReadCapped(entry, ArchiveFormat.EntryCap(name));
                total += bytes.LongLength;
                if (total > ArchiveFormat.MaxTotalBytes)
                {
                    throw new ArchiveRejectedException($"The backup unpacks to more than {ArchiveFormat.MaxTotalBytes / (1024 * 1024)} MB.");
                }

                raw[name] = bytes;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveRejectedException("The file is not a readable zip: " + ex.Message);
        }

        return raw;
    }

    /// <summary>Reads an entry's decompressed bytes, stopping one byte past the cap whatever its header claims.</summary>
    private static byte[] ReadCapped(ZipArchiveEntry entry, long cap)
    {
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > cap)
            {
                throw new ArchiveRejectedException($"The backup's '{entry.FullName}' unpacks to more than {cap / (1024 * 1024)} MB.");
            }
        }

        return buffer.ToArray();
    }

    private static byte[] Take(Dictionary<string, byte[]> raw, string name)
    {
        if (!raw.Remove(name, out byte[]? bytes))
        {
            throw new ArchiveRejectedException($"The backup has no {name}, so it is not a backup this version made, or it was changed.");
        }

        return bytes;
    }

    private static T Parse<T>(byte[] bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, string what)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, type) ?? throw new ArchiveRejectedException($"The backup's {what} is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArchiveRejectedException($"The backup's {what} cannot be read: {ex.Message}");
        }
    }

    private static (TrustLevel, SealKey?) VerifyEnvelope(byte[] envelopeBytes, string manifestSha, ArchiveReadOptions options)
    {
        DsseEnvelope envelope = Parse(envelopeBytes, ArchiveJson.Strict.DsseEnvelope, ArchiveFormat.SignatureEntry);
        if (envelope.PayloadType != ArchiveFormat.PayloadType)
        {
            throw new ArchiveRejectedException($"The backup's signature carries '{envelope.PayloadType}', not a statement this version reads.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException)
        {
            throw new ArchiveRejectedException("The backup's signature payload is not base64.");
        }

        Statement statement = Parse(payload, ArchiveJson.Strict.Statement, "signature statement");
        if (statement.Type != ArchiveFormat.StatementType || statement.PredicateType != ArchiveFormat.PredicateType)
        {
            throw new ArchiveRejectedException("The backup's signature statement is not one this version reads.");
        }

        if (statement.Subject is not [{ Name: ArchiveFormat.ManifestEntry } subject] || subject.Digest.Sha256 != manifestSha)
        {
            throw new ArchiveRejectedException("manifest.json does not match the one the backup's signature names: it was changed after the backup was made.");
        }

        switch (statement.Predicate.Level)
        {
            case "hash":
                // A signature on a hash-level statement would claim more than the level says; refuse the ambiguity.
                if (envelope.Signatures.Count != 0 || statement.Predicate.Kdf is not null)
                {
                    throw new ArchiveRejectedException("The backup says it is unsigned but carries a signature or a seal's settings.");
                }

                return (TrustLevel.Hash, null);
            case "seal":
                return (TrustLevel.Seal, VerifySeal(envelope, payload, statement.Predicate.Kdf, options));
            case "identity":
                throw new ArchiveRejectedException($"This backup is at the '{statement.Predicate.Level}' level, which this version cannot check yet.");
            default:
                throw new ArchiveRejectedException($"The backup claims a trust level '{statement.Predicate.Level}' this version does not know.");
        }
    }

    /// <summary>
    ///     Checks the one <c>seal</c> signature: HMAC-SHA256, under the key the passphrase and the statement's own
    ///     settings make, over DSSE's encoding of the statement. The settings are inside what is signed, so changing
    ///     them only fails the check, and only the settings this version writes are tried.
    /// </summary>
    private static SealKey VerifySeal(DsseEnvelope envelope, byte[] payload, SealKdf? kdf, ArchiveReadOptions options)
    {
        if (Seal.KdfProblem(kdf) is { } problem)
        {
            throw new ArchiveRejectedException("The backup's seal cannot be checked: " + problem + ".");
        }

        if (envelope.Signatures is not [{ Keyid: Seal.KeyId } signature])
        {
            throw new ArchiveRejectedException("The backup says it is sealed but does not carry exactly one seal.");
        }

        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(signature.Sig);
        }
        catch (FormatException)
        {
            throw new ArchiveRejectedException("The backup's seal is not base64.");
        }

        byte[] encoded = Dsse.PreAuthEncoding(envelope.PayloadType, payload);
        if (options.SealKey is { } handed && SealKey.FromBytes(kdf!, handed) is { } given)
        {
            return given.Check(encoded, sig) ? given : throw new SealNotOpenedException(SealNotOpenedException.WrongMessage, wrong: true);
        }

        if (options.Passphrase is null)
        {
            throw new SealNotOpenedException(SealNotOpenedException.NeededMessage, wrong: false);
        }

        SealKey key = SealKey.Derive(options.Passphrase, kdf!);
        return key.Check(encoded, sig) ? key : throw new SealNotOpenedException(SealNotOpenedException.WrongMessage, wrong: true);
    }

    private static void VerifyEntries(ArchiveManifest manifest, Dictionary<string, byte[]> raw)
    {
        HashSet<string> listed = new(StringComparer.Ordinal);
        foreach (ArchiveEntry entry in manifest.Entries)
        {
            string? problem = ArchivePaths.Problem(entry.Path);
            if (problem != null)
            {
                throw new ArchiveRejectedException($"manifest.json lists '{entry.Path}', which is {problem}.");
            }

            if (!listed.Add(entry.Path))
            {
                throw new ArchiveRejectedException($"manifest.json lists '{entry.Path}' twice.");
            }

            // A restorer is a program someone may run, so only the one name for the backup's own platform is allowed.
            if (entry.Path.StartsWith(ArchiveFormat.RestorerFolder, StringComparison.Ordinal) && entry.Path != ArchiveFormat.RestorerEntry(manifest.Platform))
            {
                throw new ArchiveRejectedException(
                    $"The backup holds '{entry.Path}'. The only restorer a {manifest.Platform} backup carries is {ArchiveFormat.RestorerEntry(manifest.Platform)}.");
            }

            if (!raw.TryGetValue(entry.Path, out byte[]? bytes))
            {
                throw new ArchiveRejectedException($"The backup is missing '{entry.Path}', which its manifest lists.");
            }

            if (bytes.LongLength != entry.Size || Dsse.Sha256Hex(bytes) != entry.Sha256)
            {
                throw new ArchiveRejectedException($"'{entry.Path}' does not match the manifest: it was changed or damaged after the backup was made.");
            }
        }

        foreach (string name in raw.Keys)
        {
            if (!listed.Contains(name))
            {
                throw new ArchiveRejectedException($"The backup holds '{name}', which its manifest does not list: it was added after the backup was made.");
            }
        }
    }
}
