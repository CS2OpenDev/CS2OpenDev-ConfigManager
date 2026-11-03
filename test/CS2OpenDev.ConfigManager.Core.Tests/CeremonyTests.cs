using System.Security.Cryptography;
using CS2OpenDev.ConfigManager.Archive;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The key ceremony (<c>acb-identity-keys</c>) in temp folders: root keys kept encrypted, a delegation of a
///     public signing key, two holders' signatures, the check, and the lines a release takes for its roots and
///     revocations. Nothing here goes near a real key.
/// </summary>
public class CeremonyTests
{
    private const string Passphrase = "correct horse battery staple";

    [Test]
    public async Task TheCeremony_MakesEncryptedRoots_DelegatesAPublicKey_AndEveryHolderSignsIt()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        List<string> spkis = [];
        for (int i = 1; i <= 3; i++)
        {
            (int code, string said, _) = Run([Passphrase, Passphrase], "new-key", temp.Sub($"root{i}.pem"));
            await Assert.That(code).IsEqualTo(0);
            spkis.Add(said.Split('\n').Single(l => l.StartsWith("spki", StringComparison.Ordinal))["spki".Length..].Trim());
        }

        await Assert.That(File.ReadAllText(temp.Sub("root1.pem"))).StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----");
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetUnixFileMode(temp.Sub("root1.pem"))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        string signing = PublicPem(temp, out ECDsa key);
        using (key)
        {
            string delegation = temp.Sub("delegation.json");
            await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", signing + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z").Code).IsEqualTo(0);
            (int first, string shown, _) = Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root1.pem"));
            await Assert.That(first).IsEqualTo(0);
            await Assert.That(shown).Contains("delegation version 1").And.Contains(IdentityIssuer.Describe(key).Keyid).And.Contains("No root has signed it yet");
            await Assert.That(Run(["1", "not the passphrase"], "cosign", delegation, "--root", temp.Sub("root2.pem")).Code).IsEqualTo(Ceremony.Refused);
            await Assert.That(Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root3.pem")).Said).Contains("Signatures on it already");

            (int partial, string partly, string why) = Run([], "check", delegation, "--root-public", spkis[0], "--root-public", spkis[1], "--root-public", spkis[2]);
            await Assert.That(partial).IsEqualTo(Ceremony.Refused).Because("a root replaced later would leave a delegation two of three signed below the threshold");
            await Assert.That(partly).Contains("signed by 2 of 3 roots (2 needed)");
            await Assert.That(why).Contains("1 of the roots checked did not sign it");

            await Assert.That(Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root2.pem")).Code).IsEqualTo(0);
            (int checkedCode, string report, _) = Run([], "check", delegation, "--root-public", spkis[0], "--root-public", spkis[1], "--root-public", spkis[2]);
            await Assert.That(checkedCode).IsEqualTo(0);
            await Assert.That(report).Contains("version 1");
            await Assert.That(report).Contains("signed by 3 of 3 roots (2 needed)");
            await Assert.That(report).Contains(IdentityIssuer.Describe(key).Keyid);
            await Assert.That(File.Exists(delegation + ".new")).IsFalse();

            IdentityTrust trust = new([.. spkis.Select(s => new IdentityRoot(Identity.KeyId(Convert.FromBase64String(s)), s))], 2,
                new HashSet<string>(StringComparer.Ordinal));
            await Assert.That(Identity.CheckDelegation(Dsse.FromJson(File.ReadAllText(delegation)), trust).Delegation.Keys.Single().Keyid).IsEqualTo(IdentityIssuer.Describe(key).Keyid);
            await Assert.That(Run([], "trust", spkis[0]).Said).StartsWith($"new IdentityRoot(\"{Identity.KeyId(Convert.FromBase64String(spkis[0]))}\"");
        }
    }

    [Test]
    public async Task ARootKey_NeedsAStrongPassphrase_TypedTheSameTwice()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);

        await Assert.That(Run(["short"], "new-key", temp.Sub("a.pem")).Error).Contains("no key was made");
        await Assert.That(Run([Passphrase, Passphrase + "!"], "new-key", temp.Sub("b.pem")).Error).Contains("differ");
        await Assert.That(Run([], "new-key", temp.Sub("c.pem")).Code).IsEqualTo(Ceremony.Refused);
        await Assert.That(Directory.GetFiles(temp.Path)).IsEmpty();
    }

    /// <summary>The service's signing key goes in as its public key only, in a window that ends after it starts.</summary>
    [Test]
    public async Task ADelegation_TakesOnlyAPublicKey_InAWindowThatMakesSense()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string privatePem = temp.Sub("signing-private.pem");
        File.WriteAllText(privatePem, key.ExportPkcs8PrivateKeyPem());
        string publicPem = PublicPem(temp, out ECDsa other);
        other.Dispose();

        await Assert.That(Run([], "delegate", temp.Sub("d1.json"), "--version", "1", "--key", privatePem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z").Error)
            .Contains("not a public key PEM");
        await Assert.That(Run([], "delegate", temp.Sub("d2.json"), "--version", "1", "--key", publicPem + ",2030-01-01T00:00:00Z,2026-01-01T00:00:00Z").Error)
            .Contains("notBefore must be before notAfter");
        await Assert.That(File.Exists(temp.Sub("d1.json")) || File.Exists(temp.Sub("d2.json"))).IsFalse();
    }

    /// <summary>A later delegation names the current one, and its version must be above it.</summary>
    [Test]
    public async Task ALaterDelegation_MustBeAboveTheOneBeforeIt()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string publicPem = PublicPem(temp, out ECDsa key);
        key.Dispose();
        string first = temp.Sub("v1.json");
        string window = publicPem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z";
        await Assert.That(Run([], "delegate", first, "--version", "1", "--key", window).Code).IsEqualTo(0);

        await Assert.That(Run([], "delegate", temp.Sub("v2a.json"), "--version", "2", "--key", window).Error).Contains("needs --previous");
        await Assert.That(Run([], "delegate", temp.Sub("v2b.json"), "--version", "1", "--previous", first, "--key", window).Error).Contains("not above");
        await Assert.That(Run([], "delegate", temp.Sub("v2.json"), "--version", "2", "--previous", first, "--key", window).Code).IsEqualTo(0);
    }

    /// <summary>
    ///     A stolen signing key is stopped by a release's revocation list; the ceremony prints the line for it, with no
    ///     time, since the thief writes the signing time.
    /// </summary>
    [Test]
    public async Task Revoke_PrintsTheReleasesRevocationLine_WithNoTime()
    {
        string keyid = new('a', 64);

        (int code, string said, _) = Run([], "revoke", keyid);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(said.Trim()).IsEqualTo($"\"{keyid}\",");
        await Assert.That(Run([], "revoke", "ABC").Code).IsEqualTo(Ceremony.Refused);
        await Assert.That(Run([], "revoke", keyid, "2027-03-04T05:06:07Z").Code).IsEqualTo(Ceremony.Refused);
    }

    /// <summary>
    ///     A holder signs only what they were shown and confirmed: not without typing its version, not something
    ///     that is not a delegation, and not a key on another curve or under another key's id.
    /// </summary>
    [Test]
    public async Task Cosign_SignsOnlyADelegationTheHolderSawAndConfirmed()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        await Assert.That(Run([Passphrase, Passphrase], "new-key", temp.Sub("root.pem")).Code).IsEqualTo(0);
        string publicPem = PublicPem(temp, out ECDsa key);
        string delegation = temp.Sub("delegation.json");
        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", publicPem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z").Code).IsEqualTo(0);
        string unsigned = File.ReadAllText(delegation);

        await Assert.That(Run(["2", Passphrase], "cosign", delegation, "--root", temp.Sub("root.pem")).Error).Contains("nothing was signed");
        await Assert.That(Run([], "cosign", delegation, "--root", temp.Sub("root.pem")).Code).IsEqualTo(Ceremony.Refused);
        await Assert.That(File.ReadAllText(delegation)).IsEqualTo(unsigned);

        using ECDsa brainpool = ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1);
        DelegatedKey real = new(IdentityIssuer.Describe(key).Keyid, IdentityIssuer.Describe(key).Spki, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
        key.Dispose();
        string otherCurve = temp.Sub("curve.json");
        File.WriteAllText(otherCurve, Dsse.ToJson(IdentityIssuer.Delegate(3, DateTimeOffset.UtcNow,
            [real with { Keyid = IdentityIssuer.Describe(brainpool).Keyid, Spki = IdentityIssuer.Describe(brainpool).Spki }], [])));
        string mislabelled = temp.Sub("label.json");
        File.WriteAllText(mislabelled, Dsse.ToJson(IdentityIssuer.Delegate(3, DateTimeOffset.UtcNow, [real with { Keyid = new string('a', 64) }], [])));
        string notDelegation = temp.Sub("statement.json");
        File.WriteAllText(notDelegation, Dsse.ToJson(new DsseEnvelope(ArchiveFormat.PayloadType, Convert.ToBase64String("{}"u8.ToArray()), [])));

        await Assert.That(Run(["3", Passphrase], "cosign", otherCurve, "--root", temp.Sub("root.pem")).Error).Contains("not a P-256 key");
        await Assert.That(Run(["3", Passphrase], "cosign", mislabelled, "--root", temp.Sub("root.pem")).Error).Contains("not a P-256 key under its own id");
        await Assert.That(Run(["3", Passphrase], "cosign", notDelegation, "--root", temp.Sub("root.pem")).Code).IsEqualTo(Ceremony.Refused);
    }

    /// <summary>
    ///     Nothing in the file reaches the holder's terminal as control characters: a signature whose id would move
    ///     the cursor over the keys shown is refused before anything is shown, and a refusal quoting the file is escaped.
    /// </summary>
    [Test]
    public async Task Cosign_RefusesIdsThatCouldPaintOverWhatItShows()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        await Assert.That(Run([Passphrase, Passphrase], "new-key", temp.Sub("root.pem")).Code).IsEqualTo(0);
        string publicPem = PublicPem(temp, out ECDsa key);
        key.Dispose();
        string delegation = temp.Sub("delegation.json");
        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", publicPem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z").Code).IsEqualTo(0);
        DsseEnvelope unsigned = Dsse.FromJson(File.ReadAllText(delegation));
        string painted = Dsse.ToJson(unsigned with { Signatures = [new DsseSignature("\u001b[3A\u001b[2K\r  " + new string('a', 64), "AAAA")] });
        File.WriteAllText(delegation, painted);

        (int code, string said, string error) = Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root.pem"));

        await Assert.That(code).IsEqualTo(Ceremony.Refused);
        await Assert.That(said).IsEmpty();
        await Assert.That(error).Contains("not a key id");
        await Assert.That(error).Contains("\\u001b[3A");
        await Assert.That(error.TrimEnd().Any(c => c is '\u001b' or '\r' or '\n')).IsFalse();
        await Assert.That(File.ReadAllText(delegation)).IsEqualTo(painted);

        string badKey = temp.Sub("badkey.json");
        DelegatedKey real = Identity.ReadUnchecked(unsigned).Keys[0];
        File.WriteAllText(badKey, Dsse.ToJson(IdentityIssuer.Delegate(2, DateTimeOffset.UtcNow, [real with { Keyid = "\u001b[2K" + real.Keyid }], [])));
        string refused = Run(["2", Passphrase], "cosign", badKey, "--root", temp.Sub("root.pem")).Error;
        await Assert.That(refused).Contains("\\u001b[2K");
        await Assert.That(refused).DoesNotContain("\u001b");
    }

    /// <summary>check never passes a delegation with fewer than two roots, whatever --threshold says.</summary>
    [Test]
    public async Task Check_RefusesAThresholdBelowTwo()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string publicPem = PublicPem(temp, out ECDsa key);
        key.Dispose();
        string delegation = temp.Sub("delegation.json");
        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", publicPem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z").Code).IsEqualTo(0);
        using ECDsa root = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string spki = IdentityIssuer.Describe(root).Spki;

        (int code, _, string error) = Run([], "check", delegation, "--root-public", spki, "--threshold", "0");

        await Assert.That(code).IsEqualTo(Ceremony.Refused);
        await Assert.That(error).Contains("below 2");
        await Assert.That(Run([], "check", delegation, "--root-public", spki).Error).Contains("checks no delegation");
    }

    /// <summary>
    ///     A holder who signed already is told so and nothing changes; a signature under their id that does not check
    ///     against this payload (left from another, or planted) is replaced, and they are told that instead.
    /// </summary>
    [Test]
    public async Task Cosign_ChecksASignatureAlreadyUnderTheHoldersId()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        (_, string made, _) = Run([Passphrase, Passphrase], "new-key", temp.Sub("root.pem"));
        string keyid = made.Split('\n').Single(l => l.StartsWith("keyid", StringComparison.Ordinal))["keyid".Length..].Trim();
        string spki = made.Split('\n').Single(l => l.StartsWith("spki", StringComparison.Ordinal))["spki".Length..].Trim();
        string publicPem = PublicPem(temp, out ECDsa key);
        key.Dispose();
        string delegation = temp.Sub("delegation.json");
        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", publicPem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z").Code).IsEqualTo(0);
        DsseEnvelope unsigned = Dsse.FromJson(File.ReadAllText(delegation));

        await Assert.That(Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root.pem")).Said).Contains("signed by " + keyid);
        string signed = File.ReadAllText(delegation);
        await Assert.That(Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root.pem")).Said).Contains("already signed by you");
        await Assert.That(File.ReadAllText(delegation)).IsEqualTo(signed);

        File.WriteAllText(delegation, Dsse.ToJson(unsigned with { Signatures = [new DsseSignature(keyid, Convert.ToBase64String(new byte[64]))] }));
        (int code, string said, _) = Run(["1", Passphrase], "cosign", delegation, "--root", temp.Sub("root.pem"));
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(said).Contains("did not check against this delegation, so yours replaced it; 1 signature(s) now");
        IdentityTrust one = new([new IdentityRoot(keyid, spki)], 1, new HashSet<string>(StringComparer.Ordinal));
        await Assert.That(Identity.CheckDelegation(Dsse.FromJson(File.ReadAllText(delegation)), one).SignedBy).IsEquivalentTo([keyid]);
    }

    /// <summary>
    ///     The ceremony never drops an argument without a word: a flag left without its value, a one-value flag given
    ///     twice, or a number out of range is refused, and nothing is written.
    /// </summary>
    [Test]
    public async Task TheArguments_AreRefused_RatherThanDroppedOrCrashedOn()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string publicPem = PublicPem(temp, out ECDsa key);
        key.Dispose();
        string window = publicPem + ",2026-01-01T00:00:00Z,2030-01-01T00:00:00Z";
        string delegation = temp.Sub("d.json");

        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", window, window).Error).Contains("has no value after it");
        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--version", "2", "--key", window).Error).Contains("--version is given twice");
        (int code, _, string error) = Run([], "delegate", delegation, "--version", "99999999999", "--key", window);
        await Assert.That(code).IsEqualTo(Ceremony.Refused);
        await Assert.That(error).Contains("not a whole number");
        await Assert.That(File.Exists(delegation)).IsFalse();

        await Assert.That(Run([], "delegate", delegation, "--version", "1", "--key", window).Code).IsEqualTo(0);
        using ECDsa root = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string spki = IdentityIssuer.Describe(root).Spki;
        await Assert.That(Run([], "check", delegation, "--root-public", spki, "--threshold").Error).Contains("has no value after it");
        await Assert.That(Run([], "check", delegation, "--root-public", spki, "--threshold", "2", "--threshold", "3").Error).Contains("given twice");
        await Assert.That(Run([], "check", delegation, "--root-public", spki, "--threshold", "99999999999").Error).Contains("not a whole number");
    }

    /// <summary>A root that is not a P-256 public key never reaches a release's trust, nor counts in check.</summary>
    [Test]
    public async Task Trust_AndCheck_TakeOnlyP256Roots_EachOnce()
    {
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string good = IdentityIssuer.Describe(p256).Spki;
        string truncated = good[..^8];

        await Assert.That(Run([], "trust", good).Code).IsEqualTo(0);
        await Assert.That(Run([], "trust", good, IdentityIssuer.Describe(p384).Spki).Error).Contains("is not a P-256 public key");
        await Assert.That(Run([], "trust", good, truncated).Code).IsEqualTo(Ceremony.Refused);
        await Assert.That(Run([], "trust", good, good).Error).Contains("given twice");
        await Assert.That(Run([], "trust", good, IdentityIssuer.Describe(p384).Spki).Said).IsEmpty();
        await Assert.That(Run([], "check", "any.json", "--root-public", IdentityIssuer.Describe(p384).Spki).Error).Contains("is not a P-256 public key");
    }

    /// <summary>
    ///     check takes the roots the build ships without being told them, so a holder who did not cosign cannot pass
    ///     by being left off the command line; leaving a shipped root out takes --without, which is printed.
    /// </summary>
    [Test]
    public async Task Check_ChecksTheShippedRoots_UnlessOneIsLeftOutByName()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string delegation = temp.Sub("delegation.json");
        File.WriteAllText(delegation, Dsse.ToJson(TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[0], TestIdentity.Roots[1])));
        IdentityTrust shipped = TestIdentity.TrustIn(TestIdentity.Roots);
        string unsigned = IdentityIssuer.Describe(TestIdentity.Roots[2]).Keyid;

        (int code, string said, string error) = RunShipping(shipped, [], "check", delegation);
        await Assert.That(code).IsEqualTo(Ceremony.Refused);
        await Assert.That(said).Contains("signed by 2 of 3 roots (2 needed)");
        await Assert.That(error).Contains("1 of the roots checked did not sign it");

        (int without, string told, _) = RunShipping(shipped, [], "check", delegation, "--without", unsigned);
        await Assert.That(without).IsEqualTo(0);
        await Assert.That(told).Contains("left out by --without: shipped root " + unsigned).And.Contains("signed by 2 of 2 roots");
        await Assert.That(RunShipping(shipped, [], "check", delegation, "--without", new string('e', 64)).Error).Contains("not a root this build ships");
    }

    /// <summary>pin prints a delegation's digest only once it checks against the shipped roots, so only one the holders made is pinned.</summary>
    [Test]
    public async Task Pin_PrintsTheDigest_OfADelegationTheShippedRootsSigned()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        DsseEnvelope made = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots);
        string delegation = temp.Sub("delegation.json");
        File.WriteAllText(delegation, Dsse.ToJson(made));
        string lone = temp.Sub("lone.json");
        File.WriteAllText(lone, Dsse.ToJson(TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey)], TestIdentity.Roots[0])));
        IdentityTrust shipped = TestIdentity.TrustIn(TestIdentity.Roots);

        (int code, string said, _) = RunShipping(shipped, [], "pin", delegation);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(said).StartsWith($"\"{Identity.DelegationDigest(made)}\", // delegation version 1");
        await Assert.That(RunShipping(shipped, [], "pin", lone).Error).Contains("signed by 1 of");
        await Assert.That(RunShipping(TestIdentity.TrustIn([]), [], "pin", delegation).Error).Contains("ships no roots");
    }

    private static string PublicPem(TempDir temp, out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string path = temp.Sub($"signing-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, key.ExportSubjectPublicKeyInfoPem());
        return path;
    }

    private static (int Code, string Said, string Error) Run(string[] secrets, params string[] args) => RunShipping(TestIdentity.TrustIn([]), secrets, args);

    /// <summary>Runs the tool as a build shipping <paramref name="shipped" /> would, or this build when null.</summary>
    private static (int Code, string Said, string Error) RunShipping(IdentityTrust? shipped, string[] secrets, params string[] args)
    {
        using StringWriter said = new();
        using StringWriter error = new();
        Queue<string> typed = new(secrets);
        string? Next(string _) => typed.Count > 0 ? typed.Dequeue() : null;
        int code = new Ceremony(said, error, Next, Next, shipped).Run(args);
        return (code, said.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), error.ToString());
    }
}
