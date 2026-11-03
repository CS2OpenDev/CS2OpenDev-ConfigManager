using System.Security.Cryptography;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>
///     The signing side of the identity level, for the key ceremony, the service and tests. It holds no key:
///     every key is given to it. It is not in Core, so neither acb nor the DemoViewer.NET plugin carries it.
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
    ///     A signature already under the root's id is kept only when it checks against this payload, and then the
    ///     delegation comes back unchanged; one that does not check (left from another payload, or planted) is
    ///     replaced.
    /// </summary>
    public static DsseEnvelope Cosign(DsseEnvelope delegation, ECDsa root) => Cosign(delegation, root, out _);

    /// <summary>As <see cref="Cosign(DsseEnvelope, ECDsa)" />, saying what was done.</summary>
    public static DsseEnvelope Cosign(DsseEnvelope delegation, ECDsa root, out CosignResult result)
    {
        ArgumentNullException.ThrowIfNull(delegation);
        ArgumentNullException.ThrowIfNull(root);
        if (delegation.PayloadType != Identity.DelegationPayloadType)
        {
            throw new ArgumentException("That is not a delegation.", nameof(delegation));
        }

        byte[] payload = Convert.FromBase64String(delegation.Payload);
        string keyid = Describe(root).Keyid;
        List<DsseSignature> under = [.. delegation.Signatures.Where(s => s.Keyid == keyid)];
        if (under.Any(s => Checks(root, Dsse.PreAuthEncoding(delegation.PayloadType, payload), s.Sig)))
        {
            result = CosignResult.AlreadySigned;
            return delegation;
        }

        result = under.Count > 0 ? CosignResult.Replaced : CosignResult.Added;
        DsseSignature added = Envelope(delegation.PayloadType, payload, [root]).Signatures[0];
        return delegation with { Signatures = [.. delegation.Signatures.Where(s => s.Keyid != keyid), added] };
    }

    private static bool Checks(ECDsa key, byte[] encoded, string sig)
    {
        try
        {
            return key.VerifyData(encoded, Convert.FromBase64String(sig), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

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

/// <summary>What <see cref="IdentityIssuer.Cosign(DsseEnvelope, ECDsa, out CosignResult)" /> did.</summary>
public enum CosignResult
{
    /// <summary>The root's signature was added.</summary>
    Added,

    /// <summary>The root had signed this payload already, so nothing changed.</summary>
    AlreadySigned,

    /// <summary>A signature under the root's id did not check against this payload, and was replaced.</summary>
    Replaced
}
