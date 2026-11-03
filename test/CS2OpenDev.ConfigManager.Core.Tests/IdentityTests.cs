using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The identity level, checked offline: a statement signed for a Steam account with a key that enough root keys
///     delegated, inside its window and not revoked. Every other combination is refused, and says why.
/// </summary>
public class IdentityTests
{
    [Test]
    public async Task ASignedBackup_ReadsAtTheIdentityLevel_NamingItsSigner()
    {
        using TempDir temp = new();
        TestIdentity.Signer signer = new();

        string zip = Write(temp, signer);
        VerifiedArchive archive = ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform);

        await Assert.That(archive.Level).IsEqualTo(TrustLevel.Identity);
        await Assert.That(archive.Signer).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(archive.IdentitySigner!.Keyid).IsEqualTo(IdentityIssuer.Describe(TestIdentity.SigningKey).Keyid);
        await Assert.That(signer.Signed).IsEquivalentTo([archive.ManifestSha256]).Because("only the manifest's digest is sent to be signed");
    }

    [Test]
    public async Task AManifestChangedAfterSigning_IsRefused()
    {
        using TempDir temp = new();
        string zip = Write(temp, new TestIdentity.Signer());

        string changed = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.ManifestEntry,
            b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("69413322", "11111111", StringComparison.Ordinal)));

        await Assert.That(() => ArchiveReader.Open(changed, ArchiveV2Tests.AnyPlatform))
            .Throws<ArchiveRejectedException>().WithMessageContaining("changed after the backup was made");
    }

    [Test]
    public async Task ADelegationSignedByOneRoot_IsRefused_AndTwoOfThreeAreEnough()
    {
        await Assert.That(() => Check(TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[1])))
            .Throws<ArchiveRejectedException>().WithMessageContaining("signed by 1 of this acb's root keys, and 2 are needed");

        IdentitySigner signer = Check(TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[1], TestIdentity.Roots[2]));
        await Assert.That(signer.SteamId64).IsEqualTo(TestIdentity.SteamId);
    }

    /// <summary>The same root signing twice is one root, and a key acb does not know counts for nothing.</summary>
    [Test]
    public async Task OneRootTwice_OrAnUnknownKey_DoesNotMakeTwo()
    {
        using ECDsa stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DsseEnvelope twice = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[0], TestIdentity.Roots[0]);
        DsseEnvelope withStranger = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[0], stranger);

        await Assert.That(() => Check(twice)).Throws<ArchiveRejectedException>().WithMessageContaining("signed by 1 of");
        await Assert.That(() => Check(withStranger)).Throws<ArchiveRejectedException>().WithMessageContaining("signed by 1 of");
    }

    [Test]
    public async Task AKeyTheDelegationDoesNotList_IsRefused()
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        await Assert.That(() => Check(TestIdentity.Delegation, key: other))
            .Throws<ArchiveRejectedException>().WithMessageContaining("a key its delegation does not list");
    }

    [Test]
    public async Task ASignatureOutsideTheKeysWindow_IsRefused()
    {
        DsseEnvelope expired = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, notAfter: DateTimeOffset.UtcNow.AddDays(-1))],
            TestIdentity.Roots[0], TestIdentity.Roots[1]);

        await Assert.That(() => Check(expired)).Throws<ArchiveRejectedException>().WithMessageContaining("outside its signing key's validity window");
    }

    /// <summary>Revoked from a time, by the delegation it carries or by what this acb shipped knowing: the earlier holds.</summary>
    [Test]
    public async Task ASignatureAfterItsKeyWasRevoked_IsRefused_HoweverAcbLearnedOfIt()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DsseEnvelope revokedInDelegation = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, revokedFrom: now.AddDays(-1))],
            TestIdentity.Roots[0], TestIdentity.Roots[1]);
        await Assert.That(() => Check(revokedInDelegation)).Throws<ArchiveRejectedException>().WithMessageContaining("revoked before it was signed");

        string keyid = IdentityIssuer.Describe(TestIdentity.SigningKey).Keyid;
        IdentityTrust shippedRevocation = TestIdentity.TrustIn(TestIdentity.Roots, new Dictionary<string, DateTimeOffset> { [keyid] = now.AddDays(-1) });
        await Assert.That(() => Check(TestIdentity.Delegation, trust: shippedRevocation))
            .Throws<ArchiveRejectedException>().WithMessageContaining("revoked before it was signed");

        IdentitySigner earlier = Check(TestIdentity.Delegation, at: now.AddDays(-2), trust: shippedRevocation);
        await Assert.That(earlier.SteamId64).IsEqualTo(TestIdentity.SteamId).Because("a signature from before the revocation still counts");
    }

    [Test]
    public async Task ThisBuildsOwnTrust_HoldsNoRootsYet_AndRefusesEveryIdentityBackup()
    {
        await Assert.That(IdentityTrust.Shipped.Roots).IsEmpty();
        await Assert.That(() => Check(TestIdentity.Delegation, trust: IdentityTrust.Shipped))
            .Throws<ArchiveRejectedException>().WithMessageContaining("carries no root keys for the identity level yet");
    }

    [Test]
    [Arguments("76561197960265728", "not a Steam account id")]
    [Arguments("7656119802967905", "not a Steam account id")]
    [Arguments("7656119802967905x", "not a Steam account id")]
    public async Task ASignerThatIsNotASteamAccount_IsRefused(string steamId, string expected)
    {
        await Assert.That(() => Check(TestIdentity.Delegation, steamId: steamId)).Throws<ArchiveRejectedException>().WithMessageContaining(expected);
    }

    [Test]
    public async Task ASignatureDatedInTheFuture_IsRefused()
    {
        DsseEnvelope wide = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, notAfter: DateTimeOffset.UtcNow.AddYears(5))],
            TestIdentity.Roots[0], TestIdentity.Roots[1]);

        await Assert.That(() => Check(wide, at: DateTimeOffset.UtcNow.AddDays(2))).Throws<ArchiveRejectedException>().WithMessageContaining("dated in the future");
    }

    /// <summary>A hash- or seal-level statement that also names a signer claims more than its level; it is refused, not read as either.</summary>
    [Test]
    public async Task ALowerLevelStatementNamingASigner_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp);
        byte[] manifest = ArchiveV2Tests.Entry(zip, ArchiveFormat.ManifestEntry);
        Statement statement = new(ArchiveFormat.StatementType,
            [new StatementSubject(ArchiveFormat.ManifestEntry, new DigestSet(Dsse.Sha256Hex(manifest)))], ArchiveFormat.PredicateType,
            new TrustPredicate("hash", SteamId64: TestIdentity.SteamId));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(statement, ArchiveJson.Strict.Statement);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new DsseEnvelope(ArchiveFormat.PayloadType, Convert.ToBase64String(payload), []),
            ArchiveJson.Strict.DsseEnvelope);
        string forged = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, _ => envelope);

        await Assert.That(() => ArchiveReader.Open(forged, ArchiveV2Tests.AnyPlatform))
            .Throws<ArchiveRejectedException>().WithMessageContaining("says it is unsigned but carries");
    }

    /// <summary>The writer checks what the service returned before writing it, so a backup its own acb would refuse never goes out.</summary>
    [Test]
    public async Task TheWriter_RefusesASignatureForAnotherManifest_AndWritesNothing()
    {
        using TempDir temp = new();
        string zip = temp.Sub("signed.zip");

        await Assert.That(() => Write(temp, new WrongDigest(), zip)).Throws<IOException>().WithMessageContaining("does not check");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    [Test]
    public async Task ABackupIsSealedOrSigned_NotBoth()
    {
        using TempDir temp = new();

        await Assert.That(() => ArchiveV2Tests.Write(temp, seal: SealKey.Create("correct horse battery staple"), identity: new TestIdentity.Signer()))
            .Throws<ArgumentException>();
    }

    /// <summary>The ceremony: an unsigned delegation, then each holder adds a signature on their own; one holder twice is still one.</summary>
    [Test]
    public async Task ADelegationCosignedByTwoHolders_Checks_AndOneHolderTwiceDoesNot()
    {
        DsseEnvelope unsigned = IdentityIssuer.Delegate(2, DateTimeOffset.UtcNow, [TestIdentity.Key(TestIdentity.SigningKey)], []);
        DsseEnvelope once = IdentityIssuer.Cosign(unsigned, TestIdentity.Roots[1]);

        await Assert.That(IdentityIssuer.Cosign(once, TestIdentity.Roots[1]).Signatures.Count).IsEqualTo(1);
        await Assert.That(() => Identity.CheckDelegation(once, TestIdentity.Trust)).Throws<ArchiveRejectedException>();

        DsseEnvelope twice = IdentityIssuer.Cosign(IdentityIssuer.FromJson(IdentityIssuer.ToJson(once)), TestIdentity.Roots[2]);
        await Assert.That(Identity.CheckDelegation(twice, TestIdentity.Trust).Version).IsEqualTo(2);
        await Assert.That(Check(twice).SteamId64).IsEqualTo(TestIdentity.SteamId);
    }

    private sealed class WrongDigest : IIdentitySigner
    {
        public DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool) =>
            new TestIdentity.Signer().Sign(new string('0', 64), platform, tool);
    }

    private static string Write(TempDir temp, IIdentitySigner signer, string? zip = null) => ArchiveV2Tests.Write(temp, zip, identity: signer);

    /// <summary>Signs a statement as the service would and checks it, as the reader does, against <paramref name="trust" />.</summary>
    private static IdentitySigner Check(DsseEnvelope delegation, ECDsa? key = null, DateTimeOffset? at = null, IdentityTrust? trust = null,
        string steamId = TestIdentity.SteamId)
    {
        DsseEnvelope envelope = IdentityIssuer.Sign(new string('a', 64), steamId, at ?? DateTimeOffset.UtcNow, delegation, key ?? TestIdentity.SigningKey);
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        Statement statement = JsonSerializer.Deserialize(payload, ArchiveJson.Strict.Statement)!;
        return Identity.Verify(envelope, payload, statement.Predicate, trust ?? TestIdentity.Trust, DateTimeOffset.UtcNow);
    }
}
