using System.Security.Cryptography;
using System.Text;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     A backup signed with a revoked key: refused unless the reader is told to accept it, then read at the hash
///     level with the signer it claims; and the policy fields that decide, and that limit which acb checks.
/// </summary>
public class RevokedSignatureTests
{
    [Test]
    public async Task ARevokedSignature_IsRefusedSayingTheFilesMatch_AndAcceptingItReadsItAtTheHashLevel()
    {
        using TempDir temp = new();
        string zip = Revoked(temp);

        RevokedSignatureException refusal = (RevokedSignatureException)Refusal(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform));
        await Assert.That(refusal.Message).Contains("revoked");
        await Assert.That(refusal.Message).Contains("Its files all match its manifest");
        await Assert.That(refusal.Claimed.SteamId64).IsEqualTo(TestIdentity.SteamId);

        VerifiedArchive archive = ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { AcceptRevoked = true, MinimumLevel = TrustLevel.Identity });
        await Assert.That(archive.Level).IsEqualTo(TrustLevel.Hash);
        await Assert.That(archive.Signer).IsNull().Because("a revoked signature proves no signer");
        await Assert.That(archive.IdentitySigner).IsNull();
        await Assert.That(archive.RevokedSigner!.SteamId64).IsEqualTo(TestIdentity.SteamId);
        await Assert.That(archive.RevokedSigner.RevokedWarning()).Contains("who made it is not proven");
    }

    /// <summary>"Unchanged" is said only when it is true: a file changed after signing is refused as changed, accepted or not.</summary>
    [Test]
    public async Task ARevokedSignatureOnChangedFiles_IsRefusedAsChanged_EvenWhenAccepted()
    {
        using TempDir temp = new();
        string zip = Revoked(temp);
        string changed = ArchiveV2Tests.Rewrite(zip, temp, "files/cs2/local/cfg/cs2_user_keys_0_slot0.vcfg", _ => Encoding.UTF8.GetBytes("\"config\" { \"x\" \"1\" }"));

        foreach (bool accept in new[] { false, true })
        {
            ArchiveRejectedException refusal = Refusal(() => ArchiveReader.Open(changed, ArchiveV2Tests.AnyPlatform with { AcceptRevoked = accept }));
            await Assert.That(refusal is RevokedSignatureException).IsFalse().Because(refusal.Message);
        }
    }

    /// <summary>A revoked key is named only for a signature that key really made; any other is just a signature that does not check.</summary>
    [Test]
    public async Task ABrokenSignatureUnderARevokedKey_IsNotReportedAsRevoked()
    {
        using TempDir temp = new();
        DsseEnvelope delegation = RevokedDelegation();
        DsseEnvelope signed = new TestIdentity.Signer(delegation: delegation).Sign(new string('a', 64), "", new ArchiveTool("", ""));
        DsseEnvelope broken = signed with { Signatures = [signed.Signatures[0] with { Sig = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) }] };

        ArchiveRejectedException refusal = Refusal(() => Identity.CheckEnvelope(Encoding.UTF8.GetBytes(Dsse.ToJson(broken)), new string('a', 64), TestIdentity.Trust,
            DateTimeOffset.UtcNow));
        await Assert.That(refusal is RevokedSignatureException).IsFalse();
        await Assert.That(refusal.Message).Contains("does not check");
    }

    [Test]
    public async Task RevokedSignatures_DefaultsToRefuseOnlyWhenThePolicyNeedsASignature()
    {
        await Assert.That(Policy("""{ "minimumLevel": "identity" }""").Revoked).IsEqualTo(RevokedSignatures.Refuse);
        await Assert.That(Policy($$"""{ "steamIds": ["{{TestIdentity.SteamId}}"] }""").Revoked).IsEqualTo(RevokedSignatures.Refuse);
        await Assert.That(Policy("""{ "minimumLevel": "hash" }""").Revoked).IsEqualTo(RevokedSignatures.Ask);
        await Assert.That(Policy("""{ "minimumLevel": "identity", "revokedSignatures": "ask" }""").Revoked).IsEqualTo(RevokedSignatures.Ask);
        await Assert.That(() => Policy("""{ "revokedSignatures": "allow" }""")).Throws<PolicyException>().WithMessageContaining("not refuse or ask");
        await Assert.That(() => Policy("""{ "minimumAcbVersion": "1.2" }""")).Throws<PolicyException>().WithMessageContaining("not a version");
    }

    /// <summary>Under a refusing policy only an administrator's --accept-revoked gets through; otherwise acceptance or a yes does.</summary>
    [Test]
    public async Task TheRevokedRule_LetsOnlyAnAdministratorOverrideARefusingPolicy()
    {
        RevokedSignatureException revoked = new("revoked.", new IdentitySigner(TestIdentity.SteamId, DateTimeOffset.UtcNow, new string('a', 64)));
        OrganiserPolicy refusing = Policy("""{ "minimumLevel": "identity" }""");
        OrganiserPolicy asking = Policy("""{ "minimumLevel": "identity", "revokedSignatures": "ask" }""");

        await Assert.That(RestoreSession.RevokedRule(refusing, accept: true, asAdministrator: false, ask: _ => true)(revoked).Accept).IsFalse();
        await Assert.That(RestoreSession.RevokedRule(refusing, accept: false, asAdministrator: true, ask: _ => true)(revoked).Accept).IsFalse()
            .Because("an administrator overrides only by saying so");
        await Assert.That(RestoreSession.RevokedRule(refusing, accept: true, asAdministrator: true, ask: null)(revoked).Accept).IsTrue();
        await Assert.That(RestoreSession.RevokedRule(asking, accept: false, asAdministrator: false, ask: _ => true)(revoked).Accept).IsTrue();
        await Assert.That(RestoreSession.RevokedRule(null, accept: false, asAdministrator: false, ask: _ => false)(revoked).Accept).IsFalse();
        await Assert.That(RestoreSession.RevokedRule(null, accept: false, asAdministrator: false, ask: null)(revoked).Hint).Contains("--accept-revoked");
        await Assert.That(RestoreSession.RevokedRule(refusing, accept: false, asAdministrator: false, ask: null)(revoked).Hint).Contains("an administrator");
    }

    /// <summary>With a revoked signature accepted, a policy's Steam IDs are compared with the account it claims.</summary>
    [Test]
    public async Task APolicysSteamIds_AreComparedWithTheAccountARevokedSignatureClaims()
    {
        using TempDir temp = new();
        VerifiedArchive archive = ArchiveReader.Open(Revoked(temp), ArchiveV2Tests.AnyPlatform with { AcceptRevoked = true });

        await Assert.That(Policy($$"""{ "steamIds": ["{{TestIdentity.SteamId}}"] }""").ArchiveProblem(archive)).IsNull();
        await Assert.That(Policy("""{ "steamIds": ["76561198000000001"] }""").ArchiveProblem(archive)).Contains("revoked signature names");
    }

    [Test]
    public async Task AcbVersions_AreOrdered_AndThePolicyRefusesAnOlderOrTheCarriedAcb()
    {
        await Assert.That(AcbVersion.Compare("1.2.0", "1.10.0")).IsLessThan(0);
        await Assert.That(AcbVersion.Compare("0.1.0-alpha", "0.1.0")).IsLessThan(0);
        await Assert.That(AcbVersion.Compare("0.1.0+abc", "0.1.0")).IsEqualTo(0);
        await Assert.That(AcbVersion.Compare("not a version", "0.0.1")).IsLessThan(0);

        OrganiserPolicy policy = Policy("""{ "minimumAcbVersion": "1.0.0", "refuseCarriedRestorer": true }""");
        await Assert.That(policy.AcbProblem("0.9.0", isCarriedRestorer: false)).Contains("needs acb 1.0.0 or newer");
        await Assert.That(policy.AcbProblem("1.0.0", isCarriedRestorer: true)).Contains("copy of acb it carries");
        await Assert.That(policy.AcbProblem("1.0.0", isCarriedRestorer: false)).IsNull();
        await Assert.That(Policy("{}").AcbProblem("0.0.1", isCarriedRestorer: true)).IsNull();
    }

    private static OrganiserPolicy Policy(string json) => OrganiserPolicy.Parse(Encoding.UTF8.GetBytes(json));

    private static DsseEnvelope RevokedDelegation() =>
        TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, revokedFrom: DateTimeOffset.UtcNow.AddDays(-1))], TestIdentity.Roots[0], TestIdentity.Roots[1]);

    /// <summary>A backup signed honestly, then given a signature by a key its delegation lists as revoked before then.</summary>
    private static string Revoked(TempDir temp)
    {
        string zip = ArchiveV2Tests.Write(temp, identity: new TestIdentity.Signer());
        string digest = ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform).ManifestSha256;
        DsseEnvelope signature = new TestIdentity.Signer(delegation: RevokedDelegation()).Sign(digest, "", new ArchiveTool("", ""));
        return ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, _ => Encoding.UTF8.GetBytes(Dsse.ToJson(signature)));
    }

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

        throw new InvalidOperationException("nothing was refused");
    }
}
