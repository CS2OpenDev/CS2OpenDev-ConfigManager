using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>Names and limits of the v2 archive (docs/design.md, "Archive format v2").</summary>
public static class ArchiveFormat
{
    /// <summary>Where a self-contained archive carries its restorer. Nothing else may sit under it.</summary>
    public const string RestorerFolder = "restorer/";

    /// <summary>A restorer is one native executable, so it may be larger than any settings file.</summary>
    public const long MaxRestorerBytes = 64L * 1024 * 1024;

    public const string Format = "acb/2";
    public const string ManifestEntry = "manifest.json";
    public const string SignatureEntry = "signature.dsse.json";
    public const string ReadmeEntry = "README.txt";

    /// <summary>The capture record: every section's settings in Valve KeyValues, the text format of CS2's own files.</summary>
    public const string CaptureEntry = "capture.vdf";

    public const string StatementType = "https://in-toto.io/Statement/v1";
    public const string PayloadType = "application/vnd.in-toto+json";
    public const string PredicateType = "https://cs2opendev.dev/acb/trust/v1";

    public const int MaxEntries = 10_000;
    public const long MaxEntryBytes = 16L * 1024 * 1024;
    public const long MaxTotalBytes = 256L * 1024 * 1024;
    public const int MaxPathLength = 240;

    /// <summary>The platform this process runs on, as archives name it, or null for one no section set exists for.</summary>
    public static string? CurrentPlatform =>
        RuntimeInformation.OSArchitecture != Architecture.X64 ? null
        : OperatingSystem.IsWindows() ? Platforms.WindowsX64
        : OperatingSystem.IsLinux() ? Platforms.LinuxX64
        : null;

    /// <summary>The one restorer an archive for <paramref name="platform" /> may carry: acb for that platform.</summary>
    public static string RestorerEntry(string platform) => RestorerFolder + RestorerFileName(platform);

    public static string RestorerFileName(string platform) => platform == Platforms.WindowsX64 ? "acb.exe" : "acb";

    /// <summary>The most one entry may unpack to: a restorer's cap under <see cref="RestorerFolder" />, a settings file's elsewhere.</summary>
    public static long EntryCap(string path) => path.StartsWith(RestorerFolder, StringComparison.Ordinal) ? MaxRestorerBytes : MaxEntryBytes;
}

public static class Platforms
{
    public const string WindowsX64 = "windows-x64";
    public const string LinuxX64 = "linux-x64";

    public static bool IsKnown(string? platform) => platform is WindowsX64 or LinuxX64;
}

/// <summary>The trust level an archive claims; the signature decides whether the claim holds.</summary>
public enum TrustLevel
{
    /// <summary>Files match the manifest. Catches damage, not deliberate edits.</summary>
    Hash,

    /// <summary>Sealed with a key derived from a passphrase.</summary>
    Seal,

    /// <summary>Signed by the CS2OpenDev service for a Steam account.</summary>
    Identity
}

public sealed record ArchiveTool(string Name, string Version);

public sealed record ArchiveSection(string Id, string Label, IReadOnlyList<string>? Notes = null);

public sealed record ArchiveEntry(string Path, long Size, string Sha256);

public sealed record ArchiveManifest(
    string Format,
    string Platform,
    DateTimeOffset Created,
    ArchiveTool Tool,
    string? SteamAccount,
    IReadOnlyList<ArchiveSection> Sections,
    IReadOnlyList<ArchiveEntry> Entries);

public sealed record DigestSet([property: JsonPropertyName("sha256")] string Sha256);

public sealed record StatementSubject(string Name, DigestSet Digest);

/// <summary>
///     The level, and what checking it needs: at the seal level how its key was made from the passphrase; at the
///     identity level the Steam account, when it was signed, and the delegation of the key that signed it.
/// </summary>
public sealed record TrustPredicate(string Level, SealKdf? Kdf = null, string? SteamId64 = null, DateTimeOffset? SignedAt = null,
    DsseEnvelope? Delegation = null);

/// <summary>The in-toto style statement a DSSE envelope carries: which manifest, at which trust level.</summary>
public sealed record Statement(
    [property: JsonPropertyName("_type")] string Type,
    IReadOnlyList<StatementSubject> Subject,
    string PredicateType,
    TrustPredicate Predicate);

public sealed record DsseSignature(string Keyid, string Sig);

public sealed record DsseEnvelope(string PayloadType, string Payload, IReadOnlyList<DsseSignature> Signatures);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ArchiveManifest))]
[JsonSerializable(typeof(Statement))]
[JsonSerializable(typeof(DsseEnvelope))]
[JsonSerializable(typeof(Delegation))]
internal sealed partial class ArchiveJson : JsonSerializerContext
{
    /// <summary>
    ///     The options every archive document is written and read with. Unknown members are refused, so a field a
    ///     later format adds cannot be slipped past this reader as ignored data. Relaxed escaping keeps paths and
    ///     labels readable in a text editor; the documents are never embedded in HTML.
    /// </summary>
    internal static ArchiveJson Strict { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
