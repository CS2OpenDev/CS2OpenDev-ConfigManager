using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>What a policy does with a backup whose identity signature was made with a revoked key.</summary>
public enum RevokedSignatures
{
    /// <summary>Refuse it; only an administrator's <c>--accept-revoked</c> applies it, for that one restore.</summary>
    Refuse,

    /// <summary>Say so and ask the person, as acb does with no policy; read it at the hash level on a yes.</summary>
    Ask
}

/// <summary>Why a policy file was refused. Nothing is restored under a policy that cannot be read whole.</summary>
public sealed class PolicyException(string message) : Exception(message);

/// <summary>
///     An organiser's policy file. Every field only takes away from what the restorer's built-in rules allow: a
///     higher minimum level, fewer Steam accounts, fewer sections, fewer NVIDIA settings, CS2 closed. There is no
///     field that adds anything, and a field this version does not know refuses the file, so a policy written for a
///     later version is never half-applied.
/// </summary>
public sealed class OrganiserPolicy
{
    private OrganiserPolicy(TrustLevel minimumLevel, IReadOnlyList<string>? steamIds, IReadOnlyList<string>? allowSections,
        IReadOnlySet<uint> nvidiaDeny, bool requireCs2Closed, string sha256, RevokedSignatures revoked, bool refuseCarriedRestorer,
        string? minimumAcbVersion)
    {
        Revoked = revoked;
        RefuseCarriedRestorer = refuseCarriedRestorer;
        MinimumAcbVersion = minimumAcbVersion;
        MinimumLevel = minimumLevel;
        SteamIds = steamIds;
        AllowSections = allowSections;
        NvidiaDeny = nvidiaDeny;
        RequireCs2Closed = requireCs2Closed;
        Sha256 = sha256;
    }

    /// <summary>The lowest trust level accepted; never below the built-in <see cref="TrustLevel.Hash" />.</summary>
    public TrustLevel MinimumLevel { get; }

    /// <summary>SteamID64s whose signed backups are accepted, or null for any. Only an identity-level backup proves its signer.</summary>
    public IReadOnlyList<string>? SteamIds { get; }

    /// <summary>The sections that may be restored, or null for every one the built-in rules allow.</summary>
    public IReadOnlyList<string>? AllowSections { get; }

    /// <summary>NVIDIA setting ids left alone even though the built-in list carries them.</summary>
    public IReadOnlySet<uint> NvidiaDeny { get; }

    /// <summary>Refuse the whole restore while CS2 runs, rather than leaving only its sections for later.</summary>
    public bool RequireCs2Closed { get; }

    /// <summary>
    ///     What happens to a backup signed with a revoked key. Unless the file says, <see cref="RevokedSignatures.Refuse" />
    ///     when the policy needs a signature (a level above hash, or Steam IDs), and <see cref="RevokedSignatures.Ask" />
    ///     otherwise, as with no policy.
    /// </summary>
    public RevokedSignatures Revoked { get; }

    /// <summary>
    ///     Refuse to check or restore with the copy of acb a backup carries. Whoever made the backup chose that copy, and
    ///     an old one does not know the keys revoked since it was built; the organiser's own acb does.
    /// </summary>
    public bool RefuseCarriedRestorer { get; }

    /// <summary>The oldest acb version that may check or restore under this policy, or null for any.</summary>
    public string? MinimumAcbVersion { get; }

    /// <summary>Whether the policy asks for more than the hash level: a minimum level above it, or named Steam accounts.</summary>
    public bool NeedsSignature => MinimumLevel > TrustLevel.Hash || SteamIds is not null;

    /// <summary>The file's SHA-256, so an elevated rerun applies the very policy the person ran with.</summary>
    public string Sha256 { get; }

    public static OrganiserPolicy Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PolicyException($"The policy file {path} cannot be read: {ex.Message}");
        }

        return Parse(bytes);
    }

    public static OrganiserPolicy Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        PolicyDocument document;
        try
        {
            document = JsonSerializer.Deserialize(JsonText.Utf8(bytes), PolicyJson.Default.PolicyDocument)
                       ?? throw new PolicyException("The policy file is empty.");
        }
        catch (JsonException ex)
        {
            throw new PolicyException("The policy file cannot be read: " + ex.Message);
        }

        TrustLevel level = document.MinimumLevel switch
        {
            null or "hash" => TrustLevel.Hash,
            "seal" => TrustLevel.Seal,
            "identity" => TrustLevel.Identity,
            _ => throw new PolicyException($"The policy's minimumLevel '{document.MinimumLevel}' is not hash, seal or identity.")
        };

        foreach (string id in document.SteamIds ?? [])
        {
            if (id.Length != 17 || !id.All(char.IsAsciiDigit) || !id.StartsWith("7656119", StringComparison.Ordinal))
            {
                throw new PolicyException($"The policy's steamIds entry '{id}' is not a SteamID64.");
            }
        }

        foreach (string section in document.Sections?.Allow ?? [])
        {
            // A misspelt section would otherwise narrow to nothing without a word; refusing says which.
            if (!Sections.All.Contains(section, StringComparer.Ordinal))
            {
                throw new PolicyException($"The policy allows '{section}', which is not a section. Sections: {string.Join(", ", Sections.All)}.");
            }
        }

        HashSet<uint> deny = [];
        foreach (string id in document.Nvidia?.Deny ?? [])
        {
            if (!id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(id.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
            {
                throw new PolicyException($"The policy's nvidia.deny entry '{id}' is not a setting id such as 0x10835002.");
            }

            deny.Add(value);
        }

        bool needsSignature = level > TrustLevel.Hash || document.SteamIds is not null;
        RevokedSignatures revoked = document.RevokedSignatures switch
        {
            null => needsSignature ? RevokedSignatures.Refuse : RevokedSignatures.Ask,
            "refuse" => RevokedSignatures.Refuse,
            "ask" => RevokedSignatures.Ask,
            _ => throw new PolicyException($"The policy's revokedSignatures '{document.RevokedSignatures}' is not refuse or ask.")
        };

        if (document.MinimumAcbVersion is { } minimum && !AcbVersion.TryParse(minimum, out _))
        {
            throw new PolicyException($"The policy's minimumAcbVersion '{minimum}' is not a version such as 1.2.0.");
        }

        return new OrganiserPolicy(level, document.SteamIds, document.Sections?.Allow, deny, document.RequireCs2Closed ?? false, Dsse.Sha256Hex(bytes),
            revoked, document.RefuseCarriedRestorer ?? false, document.MinimumAcbVersion);
    }

    /// <summary>
    ///     Why the policy refuses the acb doing the check: older than <see cref="MinimumAcbVersion" />, or the copy the
    ///     backup carries when <see cref="RefuseCarriedRestorer" /> is set. Null when it may go on.
    /// </summary>
    public string? AcbProblem(string acbVersion, bool isCarriedRestorer)
    {
        if (MinimumAcbVersion is { } minimum && AcbVersion.Compare(acbVersion, minimum) < 0)
        {
            return $"the policy needs acb {minimum} or newer, and this is acb {acbVersion}. Check and restore it with a newer acb";
        }

        return RefuseCarriedRestorer && isCarriedRestorer
            ? "the policy does not let a backup be checked or restored by the copy of acb it carries, which whoever made the backup chose. "
              + "Run the organiser's own acb on it"
            : null;
    }

    /// <summary>Why the policy refuses an archive the reader accepted, or null.</summary>
    public string? ArchiveProblem(VerifiedArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Read with a revoked key only once the policy or an administrator agreed to; its claimed signer is all there is to compare.
        if (archive.RevokedSigner is { } claimed)
        {
            return SteamIds is not null && !SteamIds.Contains(claimed.SteamId64, StringComparer.Ordinal)
                ? $"the policy accepts backups signed by {string.Join(", ", SteamIds)}, and this one's revoked signature names {claimed.SteamId64}"
                : null;
        }

        if (archive.Level < MinimumLevel)
        {
            return $"the policy requires at least the '{ArchiveReader.Name(MinimumLevel)}' level and this backup is '{ArchiveReader.Name(archive.Level)}'";
        }

        // Only a signature proves who made a backup; the manifest's own account is what the backup says about itself.
        // A signer that was not checked, or is not named, refuses, so an identity-level reader cannot pass by default.
        if (SteamIds is not null && archive.Level != TrustLevel.Identity)
        {
            return "the policy accepts backups signed by named Steam accounts, and only an identity-level backup proves who signed it";
        }

        if (SteamIds is not null && (archive.Signer is null || !SteamIds.Contains(archive.Signer, StringComparer.Ordinal)))
        {
            return $"the policy accepts backups signed by {string.Join(", ", SteamIds)}, and this one is signed by {archive.Signer ?? "no checked Steam account"}";
        }

        return null;
    }

    /// <summary>Whether the policy lets a section be restored.</summary>
    public bool Allows(string section) => AllowSections is null || AllowSections.Contains(section, StringComparer.Ordinal);
}

internal sealed record PolicySections(IReadOnlyList<string>? Allow);

internal sealed record PolicyNvidia(IReadOnlyList<string>? Deny);

internal sealed record PolicyDocument(
    string? MinimumLevel,
    IReadOnlyList<string>? SteamIds,
    PolicySections? Sections,
    PolicyNvidia? Nvidia,
    bool? RequireCs2Closed,
    string? RevokedSignatures = null,
    bool? RefuseCarriedRestorer = null,
    string? MinimumAcbVersion = null);

/// <summary>
///     acb's versions (<c>1.2.0</c>, <c>0.1.0-alpha</c>) in order: by major, minor and patch, and a pre-release before
///     its release. Pre-release labels compare as text, which is enough for one product's own tags.
/// </summary>
public static class AcbVersion
{
    public static bool TryParse(string? text, out (int Major, int Minor, int Patch, string? Pre) version)
    {
        version = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string core = text.Split('+')[0];
        int dash = core.IndexOf('-', StringComparison.Ordinal);
        string? pre = dash < 0 ? null : core[(dash + 1)..];
        string[] parts = (dash < 0 ? core : core[..dash]).Split('.');
        if (parts.Length != 3 || pre is { Length: 0 })
        {
            return false;
        }

        int[] numbers = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (parts[i].Length is 0 or > 9 || !parts[i].All(char.IsAsciiDigit))
            {
                return false;
            }

            numbers[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        version = (numbers[0], numbers[1], numbers[2], pre);
        return true;
    }

    /// <summary>Negative when <paramref name="a" /> is older; a version that does not parse counts as the oldest.</summary>
    public static int Compare(string a, string b)
    {
        bool hasA = TryParse(a, out var x);
        bool hasB = TryParse(b, out var y);
        if (!hasA || !hasB)
        {
            return hasA == hasB ? 0 : hasA ? 1 : -1;
        }

        int byNumber = (x.Major, x.Minor, x.Patch).CompareTo((y.Major, y.Minor, y.Patch));
        if (byNumber != 0)
        {
            return byNumber;
        }

        return (x.Pre, y.Pre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(x.Pre, y.Pre)
        };
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PolicyDocument))]
internal sealed partial class PolicyJson : JsonSerializerContext;
