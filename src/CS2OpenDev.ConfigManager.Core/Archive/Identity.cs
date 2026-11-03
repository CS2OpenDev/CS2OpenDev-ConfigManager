using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>
///     One of the service's signing keys, as a delegation lists it: valid in a window, and revocable from a time.
///     <see cref="RevokedFrom" /> binds only an honest signer, because whoever signs writes the signing time.
/// </summary>
public sealed record DelegatedKey(string Keyid, string Spki, DateTimeOffset NotBefore, DateTimeOffset NotAfter, DateTimeOffset? RevokedFrom = null);

/// <summary>
///     What the root keys vouch for: the signing keys current when it was issued. A backup carries the delegation
///     current when it was signed, and is checked against that one alone: <see cref="Version" /> orders delegations
///     for the ceremony and the service, and acb does not compare it. So a key revoked in a later delegation still
///     checks in a backup that carries an earlier one; only <see cref="IdentityTrust.Revoked" />, shipped in a
///     release, stops a stolen key, and it stops every signature the key made.
/// </summary>
public sealed record Delegation(
    [property: JsonPropertyName("_type")] string Type,
    int Version,
    DateTimeOffset Issued,
    IReadOnlyList<DelegatedKey> Keys);

/// <summary>A root key, by its SubjectPublicKeyInfo (DER, base64) and the SHA-256 of that DER as its id.</summary>
public sealed record IdentityRoot(string Keyid, string Spki);

/// <summary>
///     What an <c>identity</c> signature is checked against: the root keys built into this acb, how many of them
///     must sign a delegation, and the ids of the signing keys this acb knows were stolen (shipped with each
///     release, so a backup carrying an older delegation still sees the revocation). A revoked key counts for no
///     signature at all, whatever its date, because whoever holds a stolen key can date a signature before the theft.
/// </summary>
public sealed record IdentityTrust(IReadOnlyList<IdentityRoot> Roots, int Threshold, IReadOnlySet<string> Revoked)
{
    /// <summary>
    ///     The roots this build carries. None yet: the root keys are made offline at the key ceremony
    ///     (docs/identity-service.md), and until then no identity backup checks.
    /// </summary>
    public static IdentityTrust Shipped { get; } = new([], 2, new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    ///     The day <see cref="Revoked" /> was last brought up to date for a release. A key stolen after it is unknown
    ///     to this build, so every identity verdict names it.
    /// </summary>
    public DateOnly? RevokedAsOf { get; init; }

    /// <summary>
    ///     Delegations this acb takes by their payload's SHA-256 (<see cref="Identity.DelegationDigest" />) without
    ///     counting root signatures. A release that removes a root pins every delegation made before, so backups
    ///     carrying them still check while the removed root never vouches for a new one. A pinned delegation's keys
    ///     are still checked against <see cref="Revoked" />.
    /// </summary>
    public IReadOnlySet<string> Pinned { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The trust every read uses: <see cref="Shipped" />, which only a test assembly replaces.</summary>
    internal static IdentityTrust Current { get; set; } = Shipped;
}

/// <summary>
///     The signer an identity backup names once its signature checks, and the day the revoked keys it was checked
///     against date from.
/// </summary>
public sealed record IdentitySigner(string SteamId64, DateTimeOffset SignedAt, string Keyid, DateOnly? RevokedAsOf = null)
{
    /// <summary>
    ///     How current the check was. A key stolen after the day this build's revoked keys date from still checks
    ///     here, so a check that matters is made again with the newest acb.
    /// </summary>
    public string Revocations() => RevokedAsOf is { } day
        ? string.Create(CultureInfo.InvariantCulture,
            $"checked against the keys revoked up to {day:yyyy-MM-dd}: a key stolen since then still checks here, so check again with the newest acb when it matters")
        : "checked against a list of revoked keys with no date: check again with the newest acb when it matters";

    /// <summary>What to say about a backup read although this signer's key was revoked: who it names, and what is still known.</summary>
    public string RevokedWarning() =>
        $"its signature names {Describe()}, but that key was revoked, so anyone holding it could have made this backup. "
        + "Its files all match its manifest: it is unchanged since it was made, but who made it is not proven.";

    /// <summary>The signer's account id, as Steam's userdata folders and a manifest's <c>steamAccount</c> name it.</summary>
    public string AccountId => (long.Parse(SteamId64, CultureInfo.InvariantCulture) - Identity.SteamId64Base).ToString(CultureInfo.InvariantCulture);

    /// <summary>Who signed, when, and with which key: <c>Steam account 7656..., 2026-11-02 23:30Z (key 0123456789abcdef)</c>.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture, $"Steam account (SteamID64 {SteamId64}), {SignedAt.UtcDateTime:yyyy-MM-dd HH:mm}Z (key {Keyid[..16]})");

    /// <summary>
    ///     What to say when the settings are another account's than the signer's (<paramref name="settingsAccount" />
    ///     is the manifest's own, unchecked); null when they are the signer's or name no account. Both accounts are
    ///     given in both forms, so either can be told apart from the other at a glance.
    /// </summary>
    public string? OtherAccount(string? settingsAccount) =>
        settingsAccount is not null && settingsAccount != AccountId
            ? $"the settings are from {Identity.Account(settingsAccount)}; {Identity.Account(AccountId)} signed them"
            : null;
}

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

    /// <summary>
    ///     The most signatures an identity envelope may carry. Only one may be by a P-256 key its delegation lists;
    ///     the others are not checked, so a signature of another kind can be added beside it later.
    /// </summary>
    public const int MaxSignatures = 4;

    /// <summary>How far ahead of this PC's clock a signing time may be before it is refused.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromHours(1);

    internal const long SteamId64Base = 76561197960265728L;

    /// <summary>
    ///     A P-256 SubjectPublicKeyInfo up to its point: the id-ecPublicKey and prime256v1 OIDs, then an uncompressed
    ///     point. Matched byte for byte, so every platform takes the same keys as P-256, whatever else its library reads.
    /// </summary>
    private static readonly byte[] P256SpkiPrefix = Convert.FromHexString("3059301306072a8648ce3d020106082a8648ce3d03010703420004");

    /// <summary>The SHA-256 of a key's SubjectPublicKeyInfo DER: its id in delegations and envelopes.</summary>
    public static string KeyId(ReadOnlySpan<byte> spki) => Dsse.Sha256Hex(spki);

    /// <summary>
    ///     Checks a backup's signature file (<c>signature.dsse.json</c>) as every reader does, for the manifest whose
    ///     SHA-256 is <paramref name="manifestSha256" />, against <paramref name="trust" /> at <paramref name="now" />.
    ///     Returns the signer, or throws <see cref="ArchiveRejectedException" /> naming what failed. It reads nothing
    ///     else, so the service can run it on its own answers against its test roots.
    /// </summary>
    public static IdentitySigner CheckEnvelope(byte[] envelopeJson, string manifestSha256, IdentityTrust trust, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        ArgumentNullException.ThrowIfNull(trust);
        return ArchiveReader.CheckEnvelope(envelopeJson, manifestSha256, TrustLevel.Identity, trust, now)!;
    }

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
            throw new ArchiveRejectedException("This version carries no root keys for the identity level yet, so it cannot check a backup signed for a Steam account. A newer version may.")
            {
                MayBeNewer = true
            };
        }

        Delegation delegation = VerifyDelegation(delegationEnvelope, trust).Delegation;
        if (envelope.Signatures.Count > MaxSignatures)
        {
            throw new ArchiveRejectedException(string.Create(CultureInfo.InvariantCulture,
                $"The backup's signature file carries {envelope.Signatures.Count} signatures, and at most {MaxSignatures} are read."));
        }

        // Only a signature by a P-256 key the delegation lists is checked. Any other is left alone: it never makes a
        // backup check, and a signature of another kind beside the service's does not stop this version reading it.
        List<(DsseSignature Signature, DelegatedKey Key)> ours = [];
        foreach (DsseSignature s in envelope.Signatures)
        {
            if (delegation.Keys.FirstOrDefault(k => k.Keyid == s.Keyid) is { } listed && IsP256Spki(Base64OrEmpty(listed.Spki)))
            {
                ours.Add((s, listed));
            }
        }

        if (ours.Count == 0)
        {
            throw new ArchiveRejectedException("The backup is signed with a key its delegation does not list as a P-256 key.");
        }

        if (ours.Count > 1)
        {
            throw new ArchiveRejectedException("The backup carries more than one signature by its delegation's keys, and exactly one is read.");
        }

        (DsseSignature signature, DelegatedKey key) = ours[0];

        if (signedAt < key.NotBefore || signedAt >= key.NotAfter)
        {
            throw new ArchiveRejectedException("The backup was signed outside its signing key's validity window.");
        }

        // Checked before revocation, so a revoked key is reported only for a signature that key really made.
        if (!Verifies(key.Spki, key.Keyid, Dsse.PreAuthEncoding(envelope.PayloadType, payload), signature.Sig))
        {
            throw new ArchiveRejectedException("The backup's signature does not check: it was changed after it was signed.");
        }

        IdentitySigner claimed = new(steamId, signedAt, key.Keyid, trust.RevokedAsOf);
        if (trust.Revoked.Contains(key.Keyid))
        {
            throw new RevokedSignatureException("The backup's signing key was revoked as stolen, so its signature counts for nothing, whatever its date: "
                                                + "anyone holding that key could have signed it for " + claimed.Describe() + ".", claimed);
        }

        if (key.RevokedFrom is { } from && signedAt >= from)
        {
            throw new RevokedSignatureException("The backup's signing key was revoked before it was signed, so its signature counts for nothing: "
                                                + "it names " + claimed.Describe() + ".", claimed);
        }

        return claimed;
    }

    /// <summary>
    ///     An account id as people meet it both ways: <c>account 69413322 (SteamID64 76561198029679050)</c>, or the id
    ///     alone when it is not a 32-bit account id.
    /// </summary>
    public static string Account(string accountId) =>
        uint.TryParse(accountId, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) && id > 0
            ? string.Create(CultureInfo.InvariantCulture, $"account {accountId} (SteamID64 {SteamId64Base + id})")
            : $"account {accountId}";

    /// <summary>A SteamID64 of an individual account: 17 digits, above the individual-account base.</summary>
    public static bool IsSteamId64(string? id) =>
        id is { Length: 17 } && id.All(char.IsAsciiDigit) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long n)
        && n > SteamId64Base && n < SteamId64Base + uint.MaxValue;

    /// <summary>
    ///     A delegation's contents once enough of <paramref name="trust" />'s roots signed it, and the ids of the roots
    ///     that did; for the key ceremony's own check.
    /// </summary>
    public static (Delegation Delegation, IReadOnlyCollection<string> SignedBy) CheckDelegation(DsseEnvelope envelope, IdentityTrust trust) =>
        VerifyDelegation(envelope, trust);

    /// <summary>
    ///     A delegation's contents with no signature checked, refused unless this version reads it; for the key
    ///     ceremony, to show a holder what they are about to sign. Nothing it returns is vouched for.
    /// </summary>
    public static Delegation ReadUnchecked(DsseEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Parse(Payload(envelope));
    }

    /// <summary>A delegation's id for <see cref="IdentityTrust.Pinned" />: the SHA-256 of its payload, lowercase hex.</summary>
    public static string DelegationDigest(DsseEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return Dsse.Sha256Hex(Payload(envelope));
    }

    /// <summary>Whether <paramref name="spki" /> is a P-256 public key, in the one encoding a delegation may use.</summary>
    public static bool IsP256Spki(ReadOnlySpan<byte> spki) => spki.Length == P256SpkiPrefix.Length + 64 && spki.StartsWith(P256SpkiPrefix);

    /// <summary>
    ///     The delegation's payload, once at least <see cref="IdentityTrust.Threshold" /> distinct root keys signed it,
    ///     or it is one of <see cref="IdentityTrust.Pinned" />.
    /// </summary>
    private static (Delegation Delegation, IReadOnlyCollection<string> SignedBy) VerifyDelegation(DsseEnvelope envelope, IdentityTrust trust)
    {
        int roots = trust.Roots.Select(r => r.Keyid).Distinct(StringComparer.Ordinal).Count();
        if (trust.Threshold < 1 || trust.Threshold > roots)
        {
            throw new ArchiveRejectedException(string.Create(CultureInfo.InvariantCulture,
                $"This version's trust asks for {trust.Threshold} of {roots} root keys, which is no check at all or one nothing can meet, so it checks no delegation."));
        }

        byte[] payload = Payload(envelope);
        byte[] encoded = Dsse.PreAuthEncoding(envelope.PayloadType, payload);
        HashSet<string> signedBy = new(StringComparer.Ordinal);
        HashSet<string> tried = new(StringComparer.Ordinal);
        foreach (DsseSignature signature in envelope.Signatures)
        {
            // Unknown ids are skipped, not refused: a delegation may carry a root a later acb adds.
            if (trust.Roots.FirstOrDefault(r => r.Keyid == signature.Keyid) is { } root && !signedBy.Contains(root.Keyid))
            {
                tried.Add(root.Keyid);
                if (Verifies(root.Spki, root.Keyid, encoded, signature.Sig))
                {
                    signedBy.Add(root.Keyid);
                }
            }
        }

        // Order does not matter: a root fails only when none of its signatures checks.
        bool failed = tried.Except(signedBy).Any();

        // A keyid is the key's own hash, so a root this version knows that does not check never checks in a newer one.
        if (failed && signedBy.Count < trust.Threshold && !trust.Pinned.Contains(Dsse.Sha256Hex(payload)))
        {
            throw new ArchiveRejectedException("The backup's delegation carries a root key's signature that does not check, so it was changed after the "
                                               + "roots signed it: it is not one the CS2OpenDev roots signed.");
        }

        if (signedBy.Count < trust.Threshold && !trust.Pinned.Contains(Dsse.Sha256Hex(payload)))
        {
            throw new ArchiveRejectedException(string.Create(CultureInfo.InvariantCulture,
                                                   $"The backup's delegation is signed by {signedBy.Count} of this version's root keys, and {trust.Threshold} are needed.")
                                               + " A delegation made after this version may be signed by root keys it does not know, so a newer version may "
                                               + "read it; if none does, it is not one the CS2OpenDev roots signed.")
            {
                MayBeNewer = true
            };
        }

        return (Parse(payload), signedBy);
    }

    /// <summary>The delegation envelope's payload, once its payload type is the delegation's and it lists no null signature.</summary>
    private static byte[] Payload(DsseEnvelope envelope)
    {
        if (envelope.PayloadType != DelegationPayloadType)
        {
            throw new ArchiveRejectedException("The backup's delegation is not one this version reads. A newer version may.") { MayBeNewer = true };
        }

        if (envelope.Signatures.Any(s => s is null))
        {
            throw new ArchiveRejectedException("The backup's delegation lists a null signature.");
        }

        try
        {
            return Convert.FromBase64String(envelope.Payload);
        }
        catch (FormatException)
        {
            throw new ArchiveRejectedException("The backup's delegation is not base64.");
        }
    }

    private static Delegation Parse(byte[] payload)
    {
        Delegation delegation;
        try
        {
            delegation = JsonSerializer.Deserialize(payload, ArchiveJson.Strict.Delegation)
                         ?? throw new ArchiveRejectedException("The backup's delegation is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArchiveRejectedException("The backup's delegation cannot be read by this version, so it is damaged or from a newer one, which may read it: "
                                               + ex.Message) { MayBeNewer = true };
        }

        if (delegation.Type != DelegationType)
        {
            throw new ArchiveRejectedException("The backup's delegation is not one this version reads. A newer version may.") { MayBeNewer = true };
        }

        if (delegation.Keys.Count == 0 || delegation.Keys.Any(k => k is null))
        {
            throw new ArchiveRejectedException("The backup's delegation lists no signing key, or a null one.");
        }

        return delegation;
    }

    private static byte[] Base64OrEmpty(string text)
    {
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return [];
        }
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
            return read == spki.Length && IsP256Spki(spki)
                   && key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
