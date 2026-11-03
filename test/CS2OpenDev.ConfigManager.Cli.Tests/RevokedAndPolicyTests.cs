using System.IO.Compression;
using System.Text;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     A backup signed with a revoked key through acb's commands and the restore screen, the policy fields that
///     decide what happens to it and which acb may check, and <c>backup --policy</c>.
/// </summary>
public class RevokedAndPolicyTests
{
    private static readonly string ExeName = ArchiveFormat.RestorerFileName(ArchiveFormat.CurrentPlatform!);

    /// <summary>
    ///     Every command shows who the revoked signature names and that the files still match. Nothing is restored
    ///     without a yes or --accept-revoked, and --yes alone is not one.
    /// </summary>
    [Test]
    public async Task ARevokedBackup_IsShownWithAWarning_AndRestoredOnlyOnceAccepted()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        string zip = Revoked(host);

        await Assert.That(host.Run("show", zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("Revoked:  its signature names Steam account (SteamID64 " + TestIdentity.SteamId);
        await Assert.That(host.Output).Contains("unchanged since it was made");

        await Assert.That(host.Run("verify", zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("Revoked:");
        await Assert.That(host.All).Contains("--accept-revoked accepts it anyway");
        await Assert.That(host.Run("verify", zip, "--accept-revoked")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("accepted at the hash level");

        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Records()).IsEmpty();
        await Assert.That(host.Run("restore", "--yes", "--accept-revoked", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Records().Count).IsEqualTo(1);
    }

    [Test]
    public async Task AtATerminal_ARevokedBackupIsAskedAbout_AndANoRestoresNothing()
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam target = new();
        string zip = Revoked(host);

        host.Answers.Enqueue("n");
        await Assert.That(host.Run("restore", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("Use it anyway, checked only as unchanged");
        await Assert.That(host.Records()).IsEmpty();

        host.Answers.Enqueue("y");
        host.Answers.Enqueue("y");
        await Assert.That(host.Run("restore", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Records().Count).IsEqualTo(1);
    }

    /// <summary>A policy that needs a signature refuses a revoked one; --accept-revoked overrides it only for an administrator.</summary>
    [Test]
    public async Task UnderAPolicyNeedingASignature_OnlyAnAdministratorCanAcceptARevokedOne()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        string zip = Revoked(host);
        string policy = host.Temp.Sub("event.json");
        File.WriteAllText(policy, $$"""{ "minimumLevel": "identity", "steamIds": ["{{TestIdentity.SteamId}}"] }""");
        string[] restore = ["restore", "--yes", "--accept-revoked", "--policy", policy, "--steam-path", target.Root, zip];

        await Assert.That(host.Run(restore)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("only when acb runs as an administrator");
        await Assert.That(host.Records()).IsEmpty();

        host.Administrator = true;
        await Assert.That(host.Run(restore)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("Revoked:");

        string others = host.Temp.Sub("others.json");
        File.WriteAllText(others, """{ "minimumLevel": "identity", "steamIds": ["76561198000000001"] }""");
        await Assert.That(host.Run("verify", zip, "--accept-revoked", "--policy", others)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("revoked signature names " + TestIdentity.SteamId);
    }

    /// <summary>A policy that says "ask" lets the person decide, as with no policy.</summary>
    [Test]
    public async Task APolicyThatAsks_LetsAcceptRevokedThroughWithoutAnAdministrator()
    {
        using TestHost host = new();
        string zip = Revoked(host);
        string policy = host.Temp.Sub("event.json");
        File.WriteAllText(policy, """{ "minimumLevel": "identity", "revokedSignatures": "ask" }""");

        await Assert.That(host.Run("verify", zip, "--policy", policy, "--accept-revoked")).IsEqualTo(ExitCodes.Done).Because(host.All);
    }

    [Test]
    public async Task TheRestoreScreen_AsksAboutARevokedBackup_AndUsesItOnAYes()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        string zip = Revoked(host);
        List<string> asked = [];

        foreach (bool yes in new[] { false, true })
        {
            using RestoreScreen screen = new(new RestoreFlow(host.Host()) { ArchivePath = zip, SteamPath = target.Root })
            {
                AskRevoked = why =>
                {
                    asked.Add(why);
                    return yes;
                }
            };
            await Assert.That(screen.Shell.Next()).IsEqualTo(yes).Because(screen.Shell.Notice);
            if (yes)
            {
                await Assert.That(string.Join('\n', screen.Summary())).Contains("revoked signature (hash)");
            }
        }

        await Assert.That(asked.Count).IsEqualTo(2);
        await Assert.That(asked[0]).Contains("unchanged since it was made");
    }

    /// <summary>
    ///     A policy can refuse the copy of acb a backup carries, and an acb older than it names, for checking and
    ///     restoring; another acb checks the same backup.
    /// </summary>
    [Test]
    public async Task APolicy_RefusesTheCarriedAcb_AndAnOlderOne()
    {
        using TestHost host = new();
        host.Executable = Program(host);
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = host.Temp.Sub("carried.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--self-contained")).IsEqualTo(ExitCodes.Done).Because(host.All);
        string carried = host.Temp.Sub("carried.json");
        File.WriteAllText(carried, """{ "refuseCarriedRestorer": true }""");
        string newer = host.Temp.Sub("newer.json");
        File.WriteAllText(newer, """{ "minimumAcbVersion": "999.0.0" }""");

        await Assert.That(host.Run("verify", zip, "--policy", carried)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("copy of acb it carries");
        await Assert.That(host.Run("restore", "--yes", "--policy", carried, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Records()).IsEmpty();

        using TestHost other = new();
        other.Executable = Program(other, "acb build 2");
        await Assert.That(other.Run("verify", zip, "--policy", carried)).IsEqualTo(ExitCodes.Done).Because(other.All);

        await Assert.That(other.Run("restore", "--yes", "--policy", newer, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(other.All).Contains("needs acb 999.0.0 or newer");
    }

    /// <summary><c>backup --policy</c> refuses up front a backup the event's policy would refuse, and says what it would leave.</summary>
    [Test]
    public async Task BackupWithAPolicy_RefusesWhatThePolicyWouldRefuse_AndSaysWhatItLeaves()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        Directory.CreateDirectory(host.Temp.Path);
        string signed = host.Temp.Sub("signed.json");
        File.WriteAllText(signed, """{ "minimumLevel": "identity" }""");
        string named = host.Temp.Sub("named.json");
        File.WriteAllText(named, """{ "steamIds": ["76561198000000001"] }""");
        string narrow = host.Temp.Sub("narrow.json");
        File.WriteAllText(narrow, """{ "sections": { "allow": ["cs2"] } }""");

        string zip = host.Temp.Sub("b.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--policy", signed)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("Add --sign");
        await Assert.That(host.Run("backup", "-o", zip, "--steam-path", source.Root, "--seal", "--policy", named)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("signed by the Steam accounts it names");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--policy", narrow)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("restores none of the parts --only names");
        await Assert.That(File.Exists(zip)).IsFalse();

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2,cs2game", "--steam-path", source.Root, "--policy", narrow)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.All).Contains("The policy does not restore");
    }

    /// <summary>With --sign and a policy naming accounts, a signature for an account it does not name writes no backup.</summary>
    [Test]
    public async Task BackupSignWithAPolicy_WritesNothingForAnAccountItDoesNotName()
    {
        using TestHost host = new() { IdentityService = IdentityCommandTests.Service, Interactive = true };
        using FakeSteam source = new();
        Directory.CreateDirectory(host.Temp.Path);
        string named = host.Temp.Sub("named.json");
        File.WriteAllText(named, """{ "steamIds": ["76561198000000001"] }""");
        string zip = host.Temp.Sub("b.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root, "--sign", "--policy", named)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.All).Contains("which the policy does not name");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    /// <summary>A backup signed honestly, then given a signature by a key its delegation lists as revoked a day ago.</summary>
    private static string Revoked(TestHost host)
    {
        using FakeSteam source = new();
        string zip = host.Temp.Sub("revoked.zip");
        using (TestHost maker = new() { IdentityService = IdentityCommandTests.Service, Interactive = true })
        {
            maker.Answers.Enqueue("y");
            if (maker.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--sign") != ExitCodes.Done)
            {
                throw new InvalidOperationException(maker.All);
            }
        }

        string digest = ArchiveReader.Open(zip, new ArchiveReadOptions { RequirePlatform = null }).ManifestSha256;
        DsseEnvelope delegation = TestIdentity.Delegate([TestIdentity.Key(TestIdentity.SigningKey, revokedFrom: DateTimeOffset.UtcNow.AddDays(-1))],
            TestIdentity.Roots[0], TestIdentity.Roots[1]);
        string signature = Dsse.ToJson(new TestIdentity.Signer(delegation: delegation).Sign(digest, "", new ArchiveTool("", "")));
        using ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Update);
        archive.GetEntry(ArchiveFormat.SignatureEntry)!.Delete();
        using StreamWriter writer = new(archive.CreateEntry(ArchiveFormat.SignatureEntry).Open(), new UTF8Encoding(false));
        writer.Write(signature);
        return zip;
    }

    private static string Program(TestHost host, string content = "acb build 1")
    {
        string path = Path.Combine(host.Temp.Sub("bin-" + content.Replace(' ', '-')), ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
