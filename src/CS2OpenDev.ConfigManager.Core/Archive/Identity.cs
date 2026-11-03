using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>One of the service's signing keys, as a delegation lists it: valid in a window, and revocable from a time.</summary>
public sealed record DelegatedKey(string Keyid, string Spki, DateTimeOffset NotBefore, DateTimeOffset NotAfter, DateTimeOffset? RevokedFrom = null);

/// <summary>What the root keys vouch for: the signing keys current when it was issued. A higher version replaces a lower.</summary>
public sealed record Delegation(
    [property: JsonPropertyName("_type")] string Type,
    int Version,
    DateTimeOffset Issued,
    IReadOnlyList<DelegatedKey> Keys);

/// <summary>A root key, by its SubjectPublicKeyInfo (DER, base64) and the SHA-256 of that DER as its id.</summary>
public sealed record IdentityRoot(string Keyid, string Spki);

/// <summary>
///     What an <c>identity</c> signature is checked against: the root keys built into this acb, how many of them
///     must sign a delegation, and the signing keys this acb knows to be revoked (shipped with each release, so a
///     backup carrying an older delegation still sees the revocation).
/// </summary>
public sealed record IdentityTrust(IReadOnlyList<IdentityRoot> Roots, int Threshold, IReadOnlyDictionary<string, DateTimeOffset> Revoked)
{
    /// <summary>
    ///     The roots this build carries. None yet: the root keys are made offline at the key ceremony
    ///     (docs/identity-service.md), and until then no identity backup checks.
    /// </summary>
    public static IdentityTrust Shipped { get; } = new([], 2, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));

    /// <summary>The trust every read uses: <see cref="Shipped" />, which only a test assembly replaces.</summary>
    internal static IdentityTrust Current { get; set; } = Shipped;
}

/// <summary>The signer an identity backup names once its signature checks.</summary>
public sealed record IdentitySigner(string SteamId64, DateTimeOffset SignedAt, string Keyid);

/// <summary>Signs a manifest for a Steam account: the CS2OpenDev service, reached by the client, or a stand-in in tests.</summary>
public interface IIdentitySigner
{
    /// <summary>The DSSE envelope over a statement naming <paramref name="manifestSha256" /> at the identity level.</summary>
    DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool);
}

/// <summary>
///     The <c>identity</c> level: a statement the CS2OpenDev service signed for a Steam account, with a key the root
///     keys delegated, checked offline. ECDSA P-256 over SHA-256, signatures as r||s (64 bytes), over DSSE's
///     pre-authentication encoding.
/// </summary>
public static class Identity
{
    public const string DelegationType = "https://cs2opendev.dev/acb/delegation/v1";
    public const string DelegationPayloadType = "application/vnd.cs2opendev.acb-delegation+json";

    /// <summary>How far ahead of this PC's clock a signing time may be before it is refused.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    private const long SteamId64Base = 76561197960265728L;

    /// <summary>The SHA-256 of a key's SubjectPublicKeyInfo DER: its id in delegations and envelopes.</summary>
    public static string KeyId(ReadOnlySpan<byte> spki) => Dsse.Sha256Hex(spki);

    /// <summary>
    ///     Checks an identity envelope against <paramref name="trust" /> and returns its signer, or throws
    ///     <see cref="ArchiveRejectedException" /> naming what failed. The statement must already name the manifest.
    /// </summary>
    public static IdentitySigner Verify(DsseEnvelope envelope, byte[] payload, TrustPredicate predicate, IdentityTrust trust, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(trust);
        if (predicate.Kdf is not null || predicate.SteamId64 is not { } steamId || predicate.SignedAt is not { } signedAt || predicate.Delegation is not { } delegationEnvelope)
        {
            throw new ArchiveRejectedException("The backup says it is signed for a Steam account but does not carry the account, the time and the delegation.");
        }

        if (!IsSteamId64(steamId))
        {
            throw new ArchiveRejectedException($"The backup is signed for '{steamId}', which is not a Steam account id.");
        }

        if (signedAt > now + ClockSkew)
        {
            throw new ArchiveRejectedException("The backup's signature is dated in the future; check this PC's clock.");
        }

        if (trust.Roots.Count == 0)
        {
            throw new ArchiveRejectedException("This acb carries no root keys for the identity level yet, so it cannot check a backup signed for a Steam account.");
        }

        Delegation delegation = VerifyDelegation(delegationEnvelope, trust);
        if (envelope.Signatures is not [{ } signature])
        {
            throw new ArchiveRejectedException("The backup says it is signed for a Steam account but does not carry exactly one signature.");
        }

        DelegatedKey? key = delegation.Keys.FirstOrDefault(k => k.Keyid == signature.Keyid);
        if (key is null)
        {
            throw new ArchiveRejectedException("The backup is signed with a key its delegation does not list.");
        }

        if (signedAt < key.NotBefore || signedAt >= key.NotAfter)
        {
            throw new ArchiveRejectedException("The backup was signed outside its signing key's validity window.");
        }

        DateTimeOffset? revoked = trust.Revoked.TryGetValue(key.Keyid, out DateTimeOffset known)
            ? key.RevokedFrom is { } listed && listed < known ? listed : known
            : key.RevokedFrom;
        if (revoked is { } from && signedAt >= from)
        {
            throw new ArchiveRejectedException("The backup's signing key was revoked before it was signed.");
        }

        if (!Verifies(key.Spki, key.Keyid, Dsse.PreAuthEncoding(envelope.PayloadType, payload), signature.Sig))
        {
            throw new ArchiveRejectedException("The backup's signature does not check: it was changed after it was signed.");
        }

        return new IdentitySigner(steamId, signedAt, key.Keyid);
    }

    /// <summary>A SteamID64 of an individual account: 17 digits, above the individual-account base.</summary>
    public static bool IsSteamId64(string? id) =>
        id is { Length: 17 } && id.All(char.IsAsciiDigit) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long n)
        && n > SteamId64Base && n < SteamId64Base + uint.MaxValue;

    /// <summary>A delegation's contents once enough of <paramref name="trust" />'s roots signed it; for the key ceremony's own check.</summary>
    public static Delegation CheckDelegation(DsseEnvelope envelope, IdentityTrust trust) => VerifyDelegation(envelope, trust);

    /// <summary>The delegation's payload, once at least <see cref="IdentityTrust.Threshold" /> distinct root keys signed it.</summary>
    private static Delegation VerifyDelegation(DsseEnvelope envelope, IdentityTrust trust)
    {
        if (envelope.PayloadType != DelegationPayloadType)
        {
            throw new ArchiveRejectedException("The backup's delegation is not one this version reads.");
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException)
        {
            throw new ArchiveRejectedException("The backup's delegation is not base64.");
        }

        byte[] encoded = Dsse.PreAuthEncoding(envelope.PayloadType, payload);
        HashSet<string> signedBy = new(StringComparer.Ordinal);
        foreach (DsseSignature signature in envelope.Signatures)
        {
            // Unknown ids are skipped, not refused: a delegation may carry a root a later acb adds.
            if (trust.Roots.FirstOrDefault(r => r.Keyid == signature.Keyid) is { } root && !signedBy.Contains(root.Keyid)
                && Verifies(root.Spki, root.Keyid, encoded, signature.Sig))
            {
                signedBy.Add(root.Keyid);
            }
        }

        if (signedBy.Count < trust.Threshold)
        {
            throw new ArchiveRejectedException($"The backup's delegation is signed by {signedBy.Count} of this acb's root keys, and {trust.Threshold} are needed.");
        }

        Delegation delegation;
        try
        {
            delegation = JsonSerializer.Deserialize(payload, ArchiveJson.Strict.Delegation)
                         ?? throw new ArchiveRejectedException("The backup's delegation is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArchiveRejectedException("The backup's delegation cannot be read: " + ex.Message);
        }

        return delegation.Type == DelegationType ? delegation : throw new ArchiveRejectedException("The backup's delegation is not one this version reads.");
    }

    private static bool Verifies(string spkiBase64, string keyid, byte[] data, string sigBase64)
    {
        try
        {
            byte[] spki = Convert.FromBase64String(spkiBase64);
            byte[] sig = Convert.FromBase64String(sigBase64);
            if (KeyId(spki) != keyid || sig.Length != 64)
            {
                return false;
            }

            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out int read);
            return read == spki.Length && key.KeySize == 256
                   && key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>
///     The signing side of the identity level, for the service and for tests. It holds no key: every key is given
///     to it. acb never calls it.
/// </summary>
public static class IdentityIssuer
{
    /// <summary>A key's SubjectPublicKeyInfo, base64, and its id.</summary>
    public static (string Spki, string Keyid) Describe(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        byte[] spki = key.ExportSubjectPublicKeyInfo();
        return (Convert.ToBase64String(spki), Identity.KeyId(spki));
    }

    /// <summary>A delegation of <paramref name="keys" />, signed by each of <paramref name="roots" />.</summary>
    public static DsseEnvelope Delegate(int version, DateTimeOffset issued, IReadOnlyList<DelegatedKey> keys, IReadOnlyList<ECDsa> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Delegation(Identity.DelegationType, version, issued.ToUniversalTime(), keys),
            ArchiveJson.Strict.Delegation);
        return Envelope(Identity.DelegationPayloadType, payload, roots);
    }

    /// <summary>
    ///     The envelope a backup carries: a statement naming <paramref name="manifestSha256" /> at the identity level for
    ///     <paramref name="steamId64" />, signed with <paramref name="key" />, carrying <paramref name="delegation" />.
    /// </summary>
    public static DsseEnvelope Sign(string manifestSha256, string steamId64, DateTimeOffset signedAt, DsseEnvelope delegation, ECDsa key)
    {
        Statement statement = new(ArchiveFormat.StatementType,
            [new StatementSubject(ArchiveFormat.ManifestEntry, new DigestSet(manifestSha256))], ArchiveFormat.PredicateType,
            new TrustPredicate("identity", SteamId64: steamId64, SignedAt: signedAt.ToUniversalTime(), Delegation: delegation));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(statement, ArchiveJson.Strict.Statement);
        return Envelope(ArchiveFormat.PayloadType, payload, [key]);
    }

    /// <summary>
    ///     <paramref name="delegation" /> with one more root's signature, so each holder signs on their own machine.
    ///     A root that already signed is not added twice.
    /// </summary>
    public static DsseEnvelope Cosign(DsseEnvelope delegation, ECDsa root)
    {
        ArgumentNullException.ThrowIfNull(delegation);
        if (delegation.PayloadType != Identity.DelegationPayloadType)
        {
            throw new ArgumentException("That is not a delegation.", nameof(delegation));
        }

        DsseSignature added = Envelope(delegation.PayloadType, Convert.FromBase64String(delegation.Payload), [root]).Signatures[0];
        return delegation.Signatures.Any(s => s.Keyid == added.Keyid)
            ? delegation
            : delegation with { Signatures = [.. delegation.Signatures, added] };
    }

    public static string ToJson(DsseEnvelope envelope) => JsonSerializer.Serialize(envelope, ArchiveJson.Strict.DsseEnvelope);

    public static DsseEnvelope FromJson(string json) =>
        JsonSerializer.Deserialize(json, ArchiveJson.Strict.DsseEnvelope) ?? throw new JsonException("The envelope is empty.");

    private static DsseEnvelope Envelope(string payloadType, byte[] payload, IReadOnlyList<ECDsa> keys)
    {
        byte[] encoded = Dsse.PreAuthEncoding(payloadType, payload);
        List<DsseSignature> signatures = [];
        foreach (ECDsa key in keys)
        {
            signatures.Add(new DsseSignature(Describe(key).Keyid,
                Convert.ToBase64String(key.SignData(encoded, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))));
        }

        return new DsseEnvelope(payloadType, Convert.ToBase64String(payload), signatures);
    }
}
