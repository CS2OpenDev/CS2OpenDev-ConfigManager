using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Testing;

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
            .Throws<ArchiveRejectedException>().WithMessageContaining("signed by 1 of this version's root keys, and 2 are needed");
        await Assert.That(Refusal(() => Check(TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[1]))).MayBeNewer)
            .IsTrue().Because("a delegation under roots this version does not know may come from a newer release");

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

    /// <summary>
    ///     A delegation's revocation binds the signatures dated from it on. A key this acb shipped as stolen counts for
    ///     nothing at all, so a forgery dated before the theft, at the start of the key's window, is refused too.
    /// </summary>
    [Test]
    public async Task ASignatureAfterItsKeyWasRevoked_IsRefused_AndAStolenKeySignsNothing_HoweverItIsDated()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DsseEnvelope revokedInDelegation = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, revokedFrom: now.AddDays(-1))],
            TestIdentity.Roots[0], TestIdentity.Roots[1]);
        await Assert.That(() => Check(revokedInDelegation)).Throws<ArchiveRejectedException>().WithMessageContaining("revoked before it was signed");

        string keyid = IdentityIssuer.Describe(TestIdentity.SigningKey).Keyid;
        IdentityTrust stolen = TestIdentity.TrustIn(TestIdentity.Roots, new HashSet<string>(StringComparer.Ordinal) { keyid });
        await Assert.That(() => Check(TestIdentity.Delegation, trust: stolen))
            .Throws<ArchiveRejectedException>().WithMessageContaining("revoked as stolen");
        await Assert.That(() => Check(TestIdentity.Delegation, at: TestIdentity.NotBefore, trust: stolen))
            .Throws<ArchiveRejectedException>().WithMessageContaining("revoked as stolen").Because("a thief dates a forgery whenever they like");
    }

    /// <summary>A trust that asks for no root, or for more roots than it has, checks no delegation, whoever signed it.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(4)]
    public async Task ATrustThatNoDelegationCanMeetSafely_ChecksNone(int threshold)
    {
        DsseEnvelope unsigned = IdentityIssuer.Delegate(1, DateTimeOffset.UtcNow, [TestIdentity.Key(TestIdentity.SigningKey)], []);

        await Assert.That(() => Check(unsigned, trust: TestIdentity.TrustIn(TestIdentity.Roots, threshold: threshold)))
            .Throws<ArchiveRejectedException>().WithMessageContaining("checks no delegation");
        await Assert.That(() => Check(TestIdentity.Delegation, trust: TestIdentity.TrustIn(TestIdentity.Roots, threshold: threshold)))
            .Throws<ArchiveRejectedException>().WithMessageContaining("checks no delegation");
    }

    /// <summary>
    ///     A delegated key on another 256-bit curve, which some platforms' libraries read and others do not, checks
    ///     nothing on any of them: P-256 is matched by its encoding, not by its size.
    /// </summary>
    [Test]
    public async Task ADelegatedKeyOnAnother256BitCurve_ChecksNothing()
    {
        using ECDsa brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        await Assert.That(brainpool.KeySize).IsEqualTo(256);
        await Assert.That(Identity.IsP256Spki(brainpool.ExportSubjectPublicKeyInfo())).IsFalse();
        await Assert.That(Identity.IsP256Spki(TestIdentity.SigningKey.ExportSubjectPublicKeyInfo())).IsTrue();

        DsseEnvelope delegation = TestIdentity.Delegate([TestIdentity.Key(brainpool)], TestIdentity.Roots[0], TestIdentity.Roots[1]);
        await Assert.That(() => Check(delegation, key: brainpool)).Throws<ArchiveRejectedException>().WithMessageContaining("as a P-256 key");
    }

    /// <summary>
    ///     A signature for another account than the one whose settings these are (another person's sign-in reached
    ///     acb first) is written only when the person says yes, and never when nobody is asked.
    /// </summary>
    [Test]
    public async Task TheWriter_AsksBeforeWritingASignatureForAnotherAccount()
    {
        using TempDir temp = new();
        TestIdentity.Signer other = new("76561198000000001");
        List<string> asked = [];

        await Assert.That(() => ArchiveV2Tests.Write(temp, temp.Sub("unasked.zip"), identity: other)).Throws<IOException>().WithMessageContaining("No backup was written");
        await Assert.That(() => ArchiveV2Tests.Write(temp, temp.Sub("no.zip"), identity: other, otherSigner: said =>
        {
            asked.Add(said);
            return false;
        })).Throws<IOException>();
        await Assert.That(File.Exists(temp.Sub("unasked.zip")) || File.Exists(temp.Sub("no.zip"))).IsFalse();
        await Assert.That(asked.Single()).Contains("signed this backup for Steam account 39734273 (SteamID64 76561198000000001), but the settings are from account 69413322 (SteamID64 76561198029679050)");

        string zip = ArchiveV2Tests.Write(temp, temp.Sub("yes.zip"), identity: other, otherSigner: _ => true);
        await Assert.That(ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform).Signer).IsEqualTo("76561198000000001");
        await Assert.That(ArchiveReader.Open(Write(temp, new TestIdentity.Signer(), temp.Sub("own.zip")), ArchiveV2Tests.AnyPlatform).Signer)
            .IsEqualTo(TestIdentity.SteamId).Because("the settings' own account is never asked about");
    }

    /// <summary>
    ///     Settings that name no Steam account (display, power or NVIDIA only) give nothing to compare the signer
    ///     with, so the signer is named and asked about, and nothing is written without a yes.
    /// </summary>
    [Test]
    public async Task TheWriter_AsksAboutTheSigner_WhenTheSettingsNameNoAccount()
    {
        using TempDir temp = new();
        List<string> asked = [];

        await Assert.That(() => ArchiveV2Tests.Write(temp, temp.Sub("unasked.zip"), identity: new TestIdentity.Signer(), account: null))
            .Throws<IOException>().WithMessageContaining("No backup was written");
        await Assert.That(() => ArchiveV2Tests.Write(temp, temp.Sub("no.zip"), identity: new TestIdentity.Signer(), otherSigner: said =>
        {
            asked.Add(said);
            return false;
        }, account: null)).Throws<IOException>();
        await Assert.That(File.Exists(temp.Sub("unasked.zip")) || File.Exists(temp.Sub("no.zip"))).IsFalse();
        await Assert.That(asked.Single()).Contains($"signed this backup for Steam account 69413322 (SteamID64 {TestIdentity.SteamId}), and the settings name no Steam account");

        string zip = ArchiveV2Tests.Write(temp, temp.Sub("yes.zip"), identity: new TestIdentity.Signer(), otherSigner: _ => true, account: null);
        await Assert.That(ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform).Signer).IsEqualTo(TestIdentity.SteamId);
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

        DsseEnvelope twice = IdentityIssuer.Cosign(Dsse.FromJson(Dsse.ToJson(once)), TestIdentity.Roots[2]);
        await Assert.That(Identity.CheckDelegation(twice, TestIdentity.Trust).Delegation.Version).IsEqualTo(2);
        await Assert.That(Identity.CheckDelegation(twice, TestIdentity.Trust).SignedBy.Count).IsEqualTo(2);
        await Assert.That(Check(twice).SteamId64).IsEqualTo(TestIdentity.SteamId);
    }

    /// <summary>
    ///     A statement changed after the service signed it, keeping the subject (another account, another time), fails
    ///     on the service's signature itself, not on anything checked before it.
    /// </summary>
    [Test]
    public async Task AStatementChangedAfterSigning_FailsOnTheServicesSignature()
    {
        DsseEnvelope genuine = IdentityIssuer.Sign(new string('a', 64), TestIdentity.SteamId, DateTimeOffset.UtcNow, TestIdentity.Delegation, TestIdentity.SigningKey);
        await Assert.That(VerifyEnvelope(genuine).SteamId64).IsEqualTo(TestIdentity.SteamId);

        DsseEnvelope otherAccount = Restate(genuine, p => p with { SteamId64 = "76561198000000001" });
        DsseEnvelope otherTime = Restate(genuine, p => p with { SignedAt = p.SignedAt!.Value.AddDays(-1) });

        await Assert.That(() => VerifyEnvelope(otherAccount)).Throws<ArchiveRejectedException>().WithMessageContaining("signature does not check");
        await Assert.That(() => VerifyEnvelope(otherTime)).Throws<ArchiveRejectedException>().WithMessageContaining("signature does not check");
    }

    /// <summary>
    ///     A delegation changed after the roots signed it (an attacker's key added), or carrying real root key ids
    ///     over signatures that are not theirs, counts no root at all, so the attacker's key signs nothing. It is
    ///     refused as changed, never as one a newer version may read.
    /// </summary>
    [Test]
    public async Task ADelegationTheRootsDidNotSign_CountsNoRoot()
    {
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Delegation genuine = Payload(TestIdentity.Delegation, ArchiveJson.Strict.Delegation);
        DsseEnvelope widened = TestIdentity.Delegation with
        {
            Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(genuine with { Keys = [.. genuine.Keys, TestIdentity.Key(attacker)] },
                ArchiveJson.Strict.Delegation))
        };
        DsseEnvelope junk = TestIdentity.Delegation with
        {
            Signatures = [.. TestIdentity.Delegation.Signatures.Select(s => s with { Sig = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) })]
        };
        DsseEnvelope short63 = TestIdentity.Delegation with
        {
            Signatures = [.. TestIdentity.Delegation.Signatures.Select(s => s with { Sig = Convert.ToBase64String(Convert.FromBase64String(s.Sig)[..63]) })]
        };

        foreach (DsseEnvelope forged in new[] { widened, junk, short63 })
        {
            ArchiveRejectedException refusal = Refusal(() => Check(forged, key: forged == widened ? attacker : null));
            await Assert.That(refusal.Message).Contains("changed after the roots signed it");
            await Assert.That(refusal.MayBeNewer).IsFalse().Because("a root this version knows checks the same in a newer one");
        }
    }

    /// <summary>
    ///     A junk signature under a known root's id, put before or after that root's real one, does not turn a
    ///     delegation a newer version may read into one called changed: the order of the signatures does not matter.
    /// </summary>
    [Test]
    public async Task AJunkDuplicateUnderAKnownRoot_DoesNotDependOnOrder()
    {
        using ECDsa later = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DsseEnvelope genuine = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[0], later);
        DsseSignature valid = genuine.Signatures[0];
        DsseSignature junk = valid with { Sig = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) };

        foreach (DsseSignature[] order in new[] { new[] { junk, valid, genuine.Signatures[1] }, new[] { valid, junk, genuine.Signatures[1] } })
        {
            ArchiveRejectedException refusal = Refusal(() => Check(genuine with { Signatures = order }));
            await Assert.That(refusal.MayBeNewer).IsTrue().Because(refusal.Message);
        }
    }

    /// <summary>A delegated key listed under another key's id, or a key that is not P-256, never checks a signature.</summary>
    [Test]
    public async Task ADelegatedKeyThatIsNotWhatItSays_OrNotP256_ChecksNothing()
    {
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        DelegatedKey real = TestIdentity.Key(TestIdentity.SigningKey);
        DsseEnvelope mislabelled = TestIdentity.Delegate([real with { Spki = TestIdentity.Key(other).Spki }], TestIdentity.Roots[0], TestIdentity.Roots[1]);
        DsseEnvelope wrongCurve = TestIdentity.Delegate([TestIdentity.Key(p384)], TestIdentity.Roots[0], TestIdentity.Roots[1]);

        await Assert.That(() => Check(mislabelled)).Throws<ArchiveRejectedException>().WithMessageContaining("signature does not check");
        await Assert.That(() => Check(mislabelled, key: other)).Throws<ArchiveRejectedException>().WithMessageContaining("does not list");
        await Assert.That(() => Check(wrongCurve, key: p384)).Throws<ArchiveRejectedException>().WithMessageContaining("as a P-256 key");
    }

    [Test]
    public async Task AnIdentityPredicateWithoutItsParts_OrWithASealsKdf_IsRefused()
    {
        DsseEnvelope genuine = IdentityIssuer.Sign(new string('a', 64), TestIdentity.SteamId, DateTimeOffset.UtcNow, TestIdentity.Delegation, TestIdentity.SigningKey);
        TrustPredicate predicate = Payload(genuine, ArchiveJson.Strict.Statement).Predicate;
        TrustPredicate[] broken =
        [
            predicate with { Kdf = new SealKdf("argon2id", 65536, 3, 1, Convert.ToBase64String(new byte[16])) },
            predicate with { SteamId64 = null },
            predicate with { SignedAt = null },
            predicate with { Delegation = null }
        ];

        foreach (TrustPredicate p in broken)
        {
            await Assert.That(() => Identity.Verify(genuine, Convert.FromBase64String(genuine.Payload), p, TestIdentity.Trust, DateTimeOffset.UtcNow))
                .Throws<ArchiveRejectedException>().WithMessageContaining("does not carry the account, the time and the delegation");
        }
    }

    /// <summary>
    ///     Exactly one signature by a P-256 key the delegation lists is read. Two of them are refused, so the signer is
    ///     never in doubt; a signature by a key the delegation does not list, or does not list as P-256 (a post-quantum
    ///     one added later, say), is left alone and never makes a backup check on its own; more than four are refused.
    /// </summary>
    [Test]
    public async Task AnEnvelope_ReadsOneDelegatedP256Signature_AndLeavesSignaturesOfOtherKindsAlone()
    {
        using ECDsa second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        DsseEnvelope delegation = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey), TestIdentity.Key(second), TestIdentity.Key(p384)],
            TestIdentity.Roots[0], TestIdentity.Roots[1]);
        DsseEnvelope genuine = IdentityIssuer.Sign(new string('a', 64), TestIdentity.SteamId, DateTimeOffset.UtcNow, delegation, TestIdentity.SigningKey);
        DsseSignature bySecond = IdentityIssuer.Sign(new string('a', 64), TestIdentity.SteamId, DateTimeOffset.UtcNow, delegation, second).Signatures[0];
        DsseSignature otherKind = new(new string('e', 64), Convert.ToBase64String(new byte[2420]));
        DsseSignature byP384 = new(TestIdentity.Key(p384).Keyid, Convert.ToBase64String(new byte[96]));

        await Assert.That(VerifyEnvelope(genuine with { Signatures = [otherKind, genuine.Signatures[0], byP384] }).Keyid)
            .IsEqualTo(IdentityIssuer.Describe(TestIdentity.SigningKey).Keyid);
        await Assert.That(() => VerifyEnvelope(genuine with { Signatures = [genuine.Signatures[0], genuine.Signatures[0]] }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("more than one signature");
        await Assert.That(() => VerifyEnvelope(genuine with { Signatures = [genuine.Signatures[0], bySecond] }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("more than one signature");
        await Assert.That(() => VerifyEnvelope(genuine with { Signatures = [otherKind, byP384] }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("does not list as a P-256 key");
        await Assert.That(() => VerifyEnvelope(genuine with { Signatures = [genuine.Signatures[0], otherKind, otherKind, otherKind, otherKind] }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("at most 4");
    }

    /// <summary>
    ///     A backup keeps its delegation for good, so one every root signed still checks when any one root is replaced
    ///     in a later release, and one signed under the new roots still checks in an acb that has the old ones.
    /// </summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ADelegationEveryRootSigned_StillChecks_WhenAnyOneRootIsReplaced(int replaced)
    {
        using ECDsa newcomer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECDsa[] after = [.. TestIdentity.Roots.Select((r, i) => i == replaced ? newcomer : r)];
        DsseEnvelope signedByAll = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots);
        DsseEnvelope signedDuringTheChange = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], [.. TestIdentity.Roots, newcomer]);

        await Assert.That(Check(signedByAll, trust: TestIdentity.TrustIn(after)).SteamId64).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(Check(signedDuringTheChange, trust: TestIdentity.TrustIn(after)).SteamId64).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(Check(signedDuringTheChange, trust: TestIdentity.Trust).SteamId64).IsEqualTo(TestIdentity.SteamId);
    }

    /// <summary>
    ///     The check every reader runs is one function of the signature file, the manifest's digest, the trust and
    ///     the time, so the service can run it on its answers against its own test roots, with no reader set up.
    /// </summary>
    [Test]
    public async Task CheckEnvelope_UsesOnlyTheTrustAndTimeItIsGiven()
    {
        ECDsa[] roots = [ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDsa.Create(ECCurve.NamedCurves.nistP256)];
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DateTimeOffset at = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        DsseEnvelope delegation = IdentityIssuer.Delegate(1, at.AddDays(-1), [TestIdentity.Key(key, at.AddDays(-1), at.AddDays(30))], roots);
        string digest = new('d', 64);
        byte[] file = System.Text.Encoding.UTF8.GetBytes(Dsse.ToJson(IdentityIssuer.Sign(digest, TestIdentity.SteamId, at, delegation, key)));
        IdentityTrust theirs = TestIdentity.TrustIn(roots) with { RevokedAsOf = new DateOnly(2026, 5, 31) };

        IdentitySigner signer = Identity.CheckEnvelope(file, digest, theirs, at);

        await Assert.That(signer.SteamId64).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(signer.RevokedAsOf).IsEqualTo(new DateOnly(2026, 5, 31));
        await Assert.That(() => Identity.CheckEnvelope(file, digest, TestIdentity.Trust, at)).Throws<ArchiveRejectedException>().WithMessageContaining("signed by 0 of");
        await Assert.That(() => Identity.CheckEnvelope(file, new string('e', 64), theirs, at)).Throws<ArchiveRejectedException>().WithMessageContaining("does not match");
        await Assert.That(() => Identity.CheckEnvelope(file, digest, theirs, at.AddHours(-2))).Throws<ArchiveRejectedException>().WithMessageContaining("dated in the future");
        foreach (ECDsa root in roots)
        {
            root.Dispose();
        }
    }

    /// <summary>
    ///     The trust this build ships checks against the delegations the holders made, checked in under
    ///     ShippedDelegations: see <see cref="ShippedTrustProblems" />. With no roots yet it passes trivially; it is
    ///     the check a release with roots must pass.
    /// </summary>
    [Test]
    public async Task TheShippedTrust_IsWellFormed()
    {
        string folder = Path.Combine(RepoRoot(), "test", "CS2OpenDev.ConfigManager.Core.Tests", "ShippedDelegations");
        List<DsseEnvelope> delegations = Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*.json").Select(f => Dsse.FromJson(File.ReadAllText(f)))]
            : [];

        await Assert.That(ShippedTrustProblems(IdentityTrust.Shipped, delegations)).IsEmpty();
    }

    /// <summary>
    ///     The shipped trust's check catches what a release could get wrong while still compiling: a valid key that is
    ///     the wrong root, a revoked id or a pin that is a typo, and a release with roots but no delegations to check.
    /// </summary>
    [Test]
    public async Task TheShippedTrustCheck_CatchesAWrongRoot_ARevocationTypo_AndAPinTypo()
    {
        DsseEnvelope delegation = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots);
        string keyid = IdentityIssuer.Describe(TestIdentity.SigningKey).Keyid;
        IdentityTrust good = TestIdentity.TrustIn(TestIdentity.Roots, new HashSet<string>(StringComparer.Ordinal) { keyid }) with
        {
            RevokedAsOf = TestIdentity.RevokedAsOf,
            Pinned = new HashSet<string>(StringComparer.Ordinal) { Identity.DelegationDigest(delegation) }
        };
        using ECDsa stray = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IdentityTrust wrongRoot = TestIdentity.TrustIn([TestIdentity.Roots[0], TestIdentity.Roots[1], stray]) with { RevokedAsOf = TestIdentity.RevokedAsOf };

        await Assert.That(ShippedTrustProblems(good, [delegation])).IsEmpty();
        await Assert.That(string.Join("\n", ShippedTrustProblems(wrongRoot, [delegation]))).Contains("did not sign the newest delegation");
        await Assert.That(string.Join("\n", ShippedTrustProblems(good with { Revoked = new HashSet<string>(StringComparer.Ordinal) { new('e', 64) } }, [delegation])))
            .Contains("is in no checked-in delegation");
        await Assert.That(string.Join("\n", ShippedTrustProblems(good with { Pinned = new HashSet<string>(StringComparer.Ordinal) { new('e', 64) } }, [delegation])))
            .Contains("is no checked-in delegation's digest");
        await Assert.That(string.Join("\n", ShippedTrustProblems(good, []))).Contains("no delegation is checked in");
    }

    /// <summary>
    ///     A root removed in a release (its holder left, its key is lost or may be stolen) never counts again, so two
    ///     former holders together vouch for nothing; the delegations made before stay readable because the release
    ///     pins them by digest, and a pinned delegation's stolen key is still refused.
    /// </summary>
    [Test]
    public async Task ARemovedRoot_VouchesForNothingNew_WhileThePinnedDelegationsBeforeItStillCheck()
    {
        using ECDsa d = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa e = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa thief = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DsseEnvelope before = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots);
        IdentityTrust unpinned = TestIdentity.TrustIn([TestIdentity.Roots[0], d, e]);
        IdentityTrust later = unpinned with { Pinned = new HashSet<string>(StringComparer.Ordinal) { Identity.DelegationDigest(before) } };
        DsseEnvelope byFormerHolders = TestIdentity.Delegate([TestIdentity.Key(thief)], TestIdentity.Roots[1], TestIdentity.Roots[2]);

        await Assert.That(Check(before, trust: later).SteamId64).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(() => Check(before, trust: unpinned)).Throws<ArchiveRejectedException>().WithMessageContaining("signed by 1 of");
        await Assert.That(() => Check(byFormerHolders, key: thief, trust: later)).Throws<ArchiveRejectedException>().WithMessageContaining("signed by 0 of");
        await Assert.That(() => Check(before, trust: later with { Revoked = new HashSet<string>(StringComparer.Ordinal) { IdentityIssuer.Describe(TestIdentity.SigningKey).Keyid } }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("revoked as stolen");
    }

    /// <summary>
    ///     Only a test assembly replaces the trust or the service address every run uses: no shipped source sets
    ///     either, so no option or setting can widen what acb trusts.
    /// </summary>
    [Test]
    public async Task NoShippedSource_ReplacesTheTrustOrTheServiceAddress()
    {
        string repo = RepoRoot();
        List<string> setters = [];
        foreach (string folder in new[] { "src", "plugin", "tools" })
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(repo, folder), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(IdentityTrust|IdentityServiceAddress)\.Current\s*=[^=]"))
                {
                    setters.Add(file);
                }
            }
        }

        await Assert.That(setters).IsEmpty();
    }

    /// <summary>Every verdict says how current the stolen keys it was checked against are.</summary>
    [Test]
    public async Task ASigner_SaysTheDayItsRevokedKeysDateFrom()
    {
        IdentitySigner signer = Check(TestIdentity.Delegation);

        await Assert.That(signer.RevokedAsOf).IsEqualTo(TestIdentity.RevokedAsOf);
        await Assert.That(signer.Revocations()).Contains("revoked up to 2026-11-01").And.Contains("newest acb");
        await Assert.That((signer with { RevokedAsOf = null }).Revocations()).Contains("no date");
    }

    /// <summary>A delegation of another type, another payload type, one that is not base64, or with a member this version does not know.</summary>
    [Test]
    public async Task ADelegationThisVersionDoesNotRead_IsRefused()
    {
        Delegation genuine = Payload(TestIdentity.Delegation, ArchiveJson.Strict.Delegation);
        DsseEnvelope otherType = RootsSign(JsonSerializer.SerializeToUtf8Bytes(genuine with { Type = "https://example.test/delegation/v9" }, ArchiveJson.Strict.Delegation));
        string text = Encoding.UTF8.GetString(Convert.FromBase64String(TestIdentity.Delegation.Payload));
        DsseEnvelope extraMember = RootsSign(Encoding.UTF8.GetBytes(text.Insert(text.IndexOf('{', StringComparison.Ordinal) + 1, "\"keysUrl\": \"https://example.test\",")));

        await Assert.That(() => Check(TestIdentity.Delegation with { PayloadType = "application/json" }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("delegation is not one this version reads");
        await Assert.That(() => Check(otherType)).Throws<ArchiveRejectedException>().WithMessageContaining("delegation is not one this version reads");
        await Assert.That(() => Check(TestIdentity.Delegation with { Payload = "not base64!" })).Throws<ArchiveRejectedException>().WithMessageContaining("not base64");
        await Assert.That(() => Check(extraMember)).Throws<ArchiveRejectedException>().WithMessageContaining("delegation cannot be read");
        await Assert.That(Refusal(() => Check(otherType)).MayBeNewer).IsTrue();
        await Assert.That(Refusal(() => Check(extraMember)).Message).Contains("from a newer one, which may read it");
        await Assert.That(Refusal(() => Check(TestIdentity.Delegation with { Payload = "not base64!" })).MayBeNewer).IsFalse();
    }

    /// <summary>
    ///     A statement with a member this version does not know, a level it does not know or another payload type may
    ///     come from a later service, so the reader says a newer version may read it, and the writer says to update.
    /// </summary>
    [Test]
    public async Task AStatementThisVersionDoesNotRead_MayBeNewer_AndTheWriterSaysToUpdate()
    {
        string sha = new('a', 64);
        DsseEnvelope genuine = IdentityIssuer.Sign(sha, TestIdentity.SteamId, DateTimeOffset.UtcNow, TestIdentity.Delegation, TestIdentity.SigningKey);
        JsonObject statement = JsonNode.Parse(Convert.FromBase64String(genuine.Payload))!.AsObject();
        statement["predicate"]!["keysUrl"] = "https://example.test";
        DsseEnvelope extraMember = genuine with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement.ToJsonString())) };
        DsseEnvelope otherLevel = Restate(genuine, p => p with { Level = "notary" });
        DsseEnvelope otherType = genuine with { PayloadType = "application/vnd.example+json" };

        foreach (DsseEnvelope envelope in new[] { extraMember, otherLevel, otherType })
        {
            ArchiveRejectedException refusal = Refusal(() => Identity.CheckEnvelope(Encoding.UTF8.GetBytes(Dsse.ToJson(envelope)), sha, TestIdentity.Trust, DateTimeOffset.UtcNow));
            await Assert.That(refusal.MayBeNewer).IsTrue().Because(refusal.Message);
            await Assert.That(refusal.Message).Contains("newer");
        }

        using TempDir temp = new();
        string zip = temp.Sub("signed.zip");
        await Assert.That(() => Write(temp, new Restating(e =>
            {
                JsonObject s = JsonNode.Parse(Convert.FromBase64String(e.Payload))!.AsObject();
                s["predicate"]!["keysUrl"] = "https://example.test";
                return e with { Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(s.ToJsonString())) };
            }), zip))
            .Throws<IOException>().WithMessageContaining("update to it and sign again");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    /// <summary>
    ///     Keys last about a month and are then destroyed, so nearly every backup is read after its key's window has
    ///     ended. It still checks years later; only a key shipped as stolen stops it, and then the refusal says that
    ///     honest backups are refused too.
    /// </summary>
    [Test]
    public async Task AnHonestBackup_StillChecksYearsAfterItsKeysWindow_UnlessTheKeyIsShippedAsStolen()
    {
        DateTimeOffset notBefore = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset signedAt = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        DsseEnvelope delegation = IdentityIssuer.Delegate(1, notBefore,
            [TestIdentity.Key(TestIdentity.SigningKey, notBefore, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero))], [TestIdentity.Roots[0], TestIdentity.Roots[1]]);
        string digest = new('d', 64);
        byte[] file = Encoding.UTF8.GetBytes(Dsse.ToJson(IdentityIssuer.Sign(digest, TestIdentity.SteamId, signedAt, delegation, TestIdentity.SigningKey)));
        DateTimeOffset years = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        IdentitySigner signer = Identity.CheckEnvelope(file, digest, TestIdentity.Trust, years);

        await Assert.That(signer.SignedAt).IsEqualTo(signedAt);
        IdentityTrust stolen = TestIdentity.TrustIn(TestIdentity.Roots, new HashSet<string>(StringComparer.Ordinal) { signer.Keyid });
        await Assert.That(() => Identity.CheckEnvelope(file, digest, stolen, years))
            .Throws<RevokedSignatureException>().WithMessageContaining("revoked as stolen");
    }

    /// <summary>
    ///     A signature file with a member missing or null where the format has none is refused as unreadable, never
    ///     read as nulls that would stop acb unexpectedly.
    /// </summary>
    [Test]
    [Arguments("sig-null")]
    [Arguments("payload-null")]
    [Arguments("signature-null")]
    [Arguments("delegation-signatures-missing")]
    [Arguments("delegation-sig-null")]
    public async Task ASignatureFileWithMissingOrNullMembers_IsRefused(string fault)
    {
        using TempDir temp = new();
        string zip = Write(temp, new TestIdentity.Signer());
        string forged = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, b => Encoding.UTF8.GetBytes(Break(Encoding.UTF8.GetString(b), fault)));

        await Assert.That(() => ArchiveReader.Open(forged, ArchiveV2Tests.AnyPlatform)).Throws<ArchiveRejectedException>();
    }

    [Test]
    public async Task AManifestWithAMemberMissing_IsRefused()
    {
        using TempDir temp = new();
        string zip = Write(temp, new TestIdentity.Signer());
        string forged = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.ManifestEntry, b =>
        {
            JsonObject manifest = JsonNode.Parse(b)!.AsObject();
            manifest.Remove("entries");
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        });

        await Assert.That(() => ArchiveReader.Open(forged, ArchiveV2Tests.AnyPlatform)).Throws<ArchiveRejectedException>().WithMessageContaining("cannot be read");
    }

    /// <summary>The words every surface uses for the signer, and the warning when the settings are another account's.</summary>
    [Test]
    public async Task TheSigner_IsDescribed_AndAnotherAccountsSettingsAreSaidToBeSo()
    {
        IdentitySigner signer = new(TestIdentity.SteamId, new DateTimeOffset(2026, 11, 2, 23, 30, 0, TimeSpan.Zero), new string('b', 64));

        await Assert.That(signer.Describe()).IsEqualTo($"Steam account (SteamID64 {TestIdentity.SteamId}), 2026-11-02 23:30Z (key bbbbbbbbbbbbbbbb)");
        await Assert.That(signer.AccountId).IsEqualTo("69413322");
        await Assert.That(signer.OtherAccount("69413322")).IsNull();
        await Assert.That(signer.OtherAccount(null)).IsNull();
        await Assert.That(signer.OtherAccount("11111111")).IsEqualTo("the settings are from account 11111111 (SteamID64 76561197971376839); account 69413322 (SteamID64 76561198029679050) signed them");
        await Assert.That(Identity.Account("not a number")).IsEqualTo("account not a number");
    }

    /// <summary>
    ///     What is wrong with a trust a release would ship, given every delegation the holders made: roots that are
    ///     not P-256 under their own ids, or given twice; ids that are not key ids; and, once there are roots, a
    ///     threshold out of range, no RevokedAsOf, a delegation that does not check, a shipped root that did not sign
    ///     the newest delegation (a wrong key pasted, or a holder who never cosigned), a revoked key no delegation
    ///     lists, or a pin that is no delegation's digest. With no roots, nothing may be revoked or pinned.
    /// </summary>
    private static List<string> ShippedTrustProblems(IdentityTrust trust, List<DsseEnvelope> delegations)
    {
        List<string> problems = [];
        foreach (IdentityRoot root in trust.Roots)
        {
            byte[] spki = Convert.FromBase64String(root.Spki);
            if (!Identity.IsP256Spki(spki) || Identity.KeyId(spki) != root.Keyid)
            {
                problems.Add($"root {root.Keyid} is not a P-256 key under its own id");
            }
        }

        if (trust.Roots.Select(r => r.Keyid).Distinct(StringComparer.Ordinal).Count() != trust.Roots.Count)
        {
            problems.Add("a root is given twice");
        }

        foreach (string id in trust.Revoked.Concat(trust.Pinned).Where(id => id.Length != 64 || !id.All(char.IsAsciiHexDigitLower)))
        {
            problems.Add($"'{id}' is not 64 lowercase hex digits");
        }

        if (trust.Roots.Count == 0)
        {
            if (trust.Revoked.Count > 0 || trust.Pinned.Count > 0)
            {
                problems.Add("keys are revoked or delegations pinned with no roots to check them against");
            }

            return problems;
        }

        if (trust.Threshold < 2 || trust.Threshold > trust.Roots.Count)
        {
            problems.Add($"the threshold {trust.Threshold} is not from 2 to {trust.Roots.Count}");
        }

        if (trust.RevokedAsOf is null)
        {
            problems.Add("RevokedAsOf is not set; every identity verdict names the day the stolen keys date from");
        }

        if (delegations.Count == 0)
        {
            problems.Add("no delegation is checked in under ShippedDelegations, so nothing shows the roots are the right ones");
            return problems;
        }

        List<(Delegation Delegation, IReadOnlyCollection<string> SignedBy)> checkedOnes = [];
        foreach (DsseEnvelope delegation in delegations)
        {
            try
            {
                checkedOnes.Add(Identity.CheckDelegation(delegation, trust));
            }
            catch (ArchiveRejectedException ex)
            {
                problems.Add($"a checked-in delegation does not check: {ex.Message}");
            }
        }

        if (checkedOnes.Count > 0)
        {
            IReadOnlyCollection<string> newest = checkedOnes.MaxBy(c => c.Delegation.Version).SignedBy;
            problems.AddRange(trust.Roots.Where(r => !newest.Contains(r.Keyid)).Select(r => $"root {r.Keyid} did not sign the newest delegation"));
        }

        HashSet<string> listed = [.. delegations.SelectMany(d => Identity.ReadUnchecked(d).Keys).Select(k => k.Keyid)];
        problems.AddRange(trust.Revoked.Where(id => !listed.Contains(id)).Select(id => $"revoked key {id} is in no checked-in delegation"));
        HashSet<string> digests = [.. delegations.Select(Identity.DelegationDigest)];
        problems.AddRange(trust.Pinned.Where(id => !digests.Contains(id)).Select(id => $"pin {id} is no checked-in delegation's digest"));
        return problems;
    }

    private static string RepoRoot() => Repository.Root;

    /// <summary>One way of breaking a signature file's JSON, by <paramref name="fault" />.</summary>
    private static string Break(string json, string fault)
    {
        JsonObject envelope = JsonNode.Parse(json)!.AsObject();
        switch (fault)
        {
            case "sig-null":
                envelope["signatures"]![0]!["sig"] = null;
                return envelope.ToJsonString();
            case "payload-null":
                envelope["payload"] = null;
                return envelope.ToJsonString();
            case "signature-null":
                envelope["signatures"]!.AsArray().Add(null);
                return envelope.ToJsonString();
        }

        JsonObject statement = JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!.AsObject();
        JsonObject delegation = statement["predicate"]!["delegation"]!.AsObject();
        if (fault == "delegation-signatures-missing")
        {
            delegation.Remove("signatures");
        }
        else
        {
            delegation["signatures"]![0]!["sig"] = null;
        }

        envelope["payload"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(statement.ToJsonString()));
        return envelope.ToJsonString();
    }

    private static T Payload<T>(DsseEnvelope envelope, JsonTypeInfo<T> type) => JsonSerializer.Deserialize(Convert.FromBase64String(envelope.Payload), type)!;

    /// <summary><paramref name="envelope" /> with its statement's predicate changed and its signature kept.</summary>
    private static DsseEnvelope Restate(DsseEnvelope envelope, Func<TrustPredicate, TrustPredicate> change)
    {
        Statement statement = Payload(envelope, ArchiveJson.Strict.Statement);
        return envelope with
        {
            Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(statement with { Predicate = change(statement.Predicate) }, ArchiveJson.Strict.Statement))
        };
    }

    /// <summary>A delegation payload as given, signed by two of the test roots.</summary>
    private static DsseEnvelope RootsSign(byte[] payload) =>
        IdentityIssuer.Cosign(IdentityIssuer.Cosign(new DsseEnvelope(Identity.DelegationPayloadType, Convert.ToBase64String(payload), []), TestIdentity.Roots[0]),
            TestIdentity.Roots[1]);

    /// <summary>Checks an envelope as the reader does, against the test roots.</summary>
    private static IdentitySigner VerifyEnvelope(DsseEnvelope envelope)
    {
        byte[] payload = Convert.FromBase64String(envelope.Payload);
        return Identity.Verify(envelope, payload, JsonSerializer.Deserialize(payload, ArchiveJson.Strict.Statement)!.Predicate, TestIdentity.Trust, DateTimeOffset.UtcNow);
    }

    /// <summary>Signs as the test service does, then changes the envelope it returns.</summary>
    private sealed class Restating(Func<DsseEnvelope, DsseEnvelope> change) : IIdentitySigner
    {
        public DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool) =>
            change(new TestIdentity.Signer().Sign(manifestSha256, platform, tool));
    }

    private sealed class WrongDigest : IIdentitySigner
    {
        public DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool) =>
            new TestIdentity.Signer().Sign(new string('0', 64), platform, tool);
    }

    private static string Write(TempDir temp, IIdentitySigner signer, string? zip = null) => ArchiveV2Tests.Write(temp, zip, identity: signer);

    /// <summary>The refusal <paramref name="check" /> throws.</summary>
    private static ArchiveRejectedException Refusal(Action check)
    {
        try
        {
            check();
        }
        catch (ArchiveRejectedException ex)
        {
            return ex;
        }

        throw new InvalidOperationException("It was not refused.");
    }

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
