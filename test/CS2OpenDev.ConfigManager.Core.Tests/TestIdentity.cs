using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using CS2OpenDev.ConfigManager.Archive;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     A stand-in for the CS2OpenDev service and its key ceremony: three root keys made for this test run, a
///     delegation two of them sign, and one signing key in it. The test assemblies check identity backups against
///     these roots, never against any real ones.
/// </summary>
internal static class TestIdentity
{
    /// <summary>Account 69413322 (FakeSteam's) as a SteamID64.</summary>
    public const string SteamId = "76561198029679050";

    public static readonly ECDsa[] Roots = [ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDsa.Create(ECCurve.NamedCurves.nistP256)];

    public static readonly ECDsa SigningKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static readonly DateTimeOffset NotBefore = DateTimeOffset.UtcNow.AddDays(-30);

    public static readonly DateTimeOffset NotAfter = DateTimeOffset.UtcNow.AddDays(365);

    public static IdentityTrust Trust { get; } = TrustIn(Roots);

    /// <summary>The delegation two of the three roots signed, listing <see cref="SigningKey" />.</summary>
    public static DsseEnvelope Delegation { get; } = Delegate([Key(SigningKey)], Roots[0], Roots[2]);

    [ModuleInitializer]
    internal static void Install() => IdentityTrust.Current = Trust;

    public static IdentityTrust TrustIn(IEnumerable<ECDsa> roots, IReadOnlyDictionary<string, DateTimeOffset>? revoked = null) =>
        new([.. roots.Select(r => IdentityIssuer.Describe(r)).Select(d => new IdentityRoot(d.Keyid, d.Spki))], 2,
            revoked ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));

    public static DelegatedKey Key(ECDsa key, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, DateTimeOffset? revokedFrom = null)
    {
        (string spki, string keyid) = IdentityIssuer.Describe(key);
        return new DelegatedKey(keyid, spki, notBefore ?? NotBefore, notAfter ?? NotAfter, revokedFrom);
    }

    public static DsseEnvelope Delegate(IReadOnlyList<DelegatedKey> keys, params ECDsa[] roots) =>
        IdentityIssuer.Delegate(1, DateTimeOffset.UtcNow.AddDays(-30), keys, roots);

    /// <summary>Signs as the service would, for <see cref="SteamId" /> now, unless told otherwise.</summary>
    internal sealed class Signer(string steamId = SteamId, DsseEnvelope? delegation = null, ECDsa? key = null, DateTimeOffset? at = null) : IIdentitySigner
    {
        public List<string> Signed { get; } = [];

        public DsseEnvelope Sign(string manifestSha256, string platform, ArchiveTool tool)
        {
            Signed.Add(manifestSha256);
            return IdentityIssuer.Sign(manifestSha256, steamId, at ?? DateTimeOffset.UtcNow, delegation ?? Delegation, key ?? SigningKey);
        }
    }
}
