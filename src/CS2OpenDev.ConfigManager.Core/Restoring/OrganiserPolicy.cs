using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

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
        IReadOnlySet<uint> nvidiaDeny, bool requireCs2Closed, string sha256)
    {
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

        return new OrganiserPolicy(level, document.SteamIds, document.Sections?.Allow, deny, document.RequireCs2Closed ?? false, Dsse.Sha256Hex(bytes));
    }

    /// <summary>Why the policy refuses an archive the reader accepted, or null.</summary>
    public string? ArchiveProblem(VerifiedArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
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
    bool? RequireCs2Closed);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PolicyDocument))]
internal sealed partial class PolicyJson : JsonSerializerContext;
