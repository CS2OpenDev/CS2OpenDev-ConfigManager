using System.IO.Compression;
using System.Text;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.DemoViewer.ViewModels;

namespace CS2OpenDev.ConfigManager.DemoViewer.Tests;

/// <summary>
///     The page's "Check a backup", which reads with the restorer's own reader: a changed file means "do not
///     restore", another platform's backup is still read, and an unsigned one is never called unchanged since it
///     was made.
/// </summary>
public class CheckBackupTests
{
    [Test]
    public async Task AValidUnsignedBackup_IsCheckedAndSaidToBeUnsigned()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64);
        List<string> results = [];

        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(status).Contains("each matching its manifest");
        await Assert.That(status).Contains("anyone could have edited it");
        await Assert.That(status).DoesNotContain("unchanged since it was made");
        await Assert.That(results[0]).StartsWith("Trust level: hash (unsigned");
    }

    [Test]
    public async Task ABackupWithAChangedFile_IsNotToBeRestored()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64);
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("files/cs2game/autoexec.cfg")!.Delete();
            using StreamWriter writer = new(archive.CreateEntry("files/cs2game/autoexec.cfg").Open(), new UTF8Encoding(false));
            await writer.WriteAsync("bind x quit");
        }

        List<string> results = [];
        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(status).StartsWith("Do not restore");
        await Assert.That(results).IsEmpty();
    }

    /// <summary>A backup from a newer release is one this plugin cannot check, which it says, not that the backup is unsafe.</summary>
    [Test]
    public async Task ABackupFromANewerRelease_SaysAnUpdateMayCheckIt()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64);
        string manifest;
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = archive.GetEntry(ArchiveFormat.ManifestEntry)!;
            using (StreamReader reader = new(entry.Open()))
            {
                manifest = await reader.ReadToEndAsync();
            }

            entry.Delete();
            using StreamWriter writer = new(archive.CreateEntry(ArchiveFormat.ManifestEntry).Open(), new UTF8Encoding(false));
            await writer.WriteAsync(manifest.Replace(ArchiveFormat.Format, "acb/99", StringComparison.Ordinal));
        }

        string status = ConfigBackupPageViewModel.Check(zip, []);

        await Assert.That(status).StartsWith("This version of the plugin cannot check backup.zip");
        await Assert.That(status).Contains("A newer version may");
    }

    [Test]
    public async Task ABackupForAnotherPlatform_IsStillRead()
    {
        using Scratch scratch = new();
        string other = ArchiveFormat.CurrentPlatform == Platforms.WindowsX64 ? Platforms.LinuxX64 : Platforms.WindowsX64;
        string zip = scratch.Archive(other);
        List<string> results = [];

        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(status).Contains("each matching its manifest");
        await Assert.That(results).Contains(r => r.EndsWith("for " + other, StringComparison.Ordinal));
    }

    /// <summary>A sealed backup is checked with the page's passphrase: without one the page asks for it, and a wrong one means "do not restore".</summary>
    [Test]
    public async Task ASealedBackup_IsCheckedWithThePassphrase()
    {
        const string Passphrase = "correct horse battery staple";
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64, SealKey.Create(Passphrase));

        List<string> none = [];
        string asked = ConfigBackupPageViewModel.Check(zip, none, "");
        List<string> wrong = [];
        string refused = ConfigBackupPageViewModel.Check(zip, wrong, "not the passphrase at all");
        List<string> results = [];
        string opened = ConfigBackupPageViewModel.Check(zip, results, Passphrase);

        await Assert.That(asked).Contains("is sealed with a passphrase");
        await Assert.That(none).IsEmpty();
        await Assert.That(refused).StartsWith("Do not restore");
        await Assert.That(wrong).IsEmpty();
        await Assert.That(opened).Contains("opens with this passphrase");
        await Assert.That(results[0]).StartsWith("Trust level: seal");
    }

    /// <summary>
    ///     A backup signed for a Steam account (against this test run's roots) names the signer it checked, and says
    ///     when the settings are another account's.
    /// </summary>
    [Test]
    public async Task ASignedBackup_NamesItsSigner_AndWhoseSettingsTheyAre()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64, account: "69413322",
            identity: new CS2OpenDev.ConfigManager.Tests.TestIdentity.Signer("76561198000000001"));
        List<string> results = [];

        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(results[0]).StartsWith("Trust level: identity (signed by Steam account (SteamID64 76561198000000001), ");
        await Assert.That(results).Contains("Note: the settings are from account 69413322 (SteamID64 76561198029679050); account 39734273 (SteamID64 76561198000000001) signed them");
        await Assert.That(results).Contains("Note: checked against the keys revoked up to 2026-11-01: a key stolen since then still checks here, so check again with the newest acb when it matters");
        await Assert.That(status).Contains("was signed for Steam account (SteamID64 76561198000000001)");
    }

    /// <summary>A backup signed with a revoked key is shown with the signer it names, a warning, and that its files match; never as signed.</summary>
    [Test]
    public async Task ARevokedSignature_IsShownWithAWarning_NotAsSigned()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64, account: "69413322",
            identity: new CS2OpenDev.ConfigManager.Tests.TestIdentity.Signer());
        string digest = ArchiveReader.Open(zip, new ArchiveReadOptions { RequirePlatform = null }).ManifestSha256;
        DsseEnvelope delegation = CS2OpenDev.ConfigManager.Tests.TestIdentity.Delegate(
            [CS2OpenDev.ConfigManager.Tests.TestIdentity.Key(CS2OpenDev.ConfigManager.Tests.TestIdentity.SigningKey, revokedFrom: DateTimeOffset.UtcNow.AddDays(-1))],
            CS2OpenDev.ConfigManager.Tests.TestIdentity.Roots[0], CS2OpenDev.ConfigManager.Tests.TestIdentity.Roots[1]);
        string signature = Dsse.ToJson(new CS2OpenDev.ConfigManager.Tests.TestIdentity.Signer(delegation: delegation).Sign(digest, "", new ArchiveTool("", "")));
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry(ArchiveFormat.SignatureEntry)!.Delete();
            using StreamWriter writer = new(archive.CreateEntry(ArchiveFormat.SignatureEntry).Open(), new UTF8Encoding(false));
            await writer.WriteAsync(signature);
        }

        List<string> results = [];
        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(results[0]).StartsWith("Trust level: hash (its identity signature does not count");
        await Assert.That(results[1]).Contains("who made it is not proven");
        await Assert.That(status).Contains("unchanged since it was made");
        await Assert.That(status).DoesNotContain("was signed for");
    }

    /// <summary>A temp folder holding a hand-made v2 archive of one cfg file.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dv-acb-plugin-" + Guid.NewGuid().ToString("N"));

        public string Archive(string platform, SealKey? seal = null, string? account = null, IIdentitySigner? identity = null)
        {
            string staging = Path.Combine(_dir, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "files", "cs2game"));
            File.WriteAllText(Path.Combine(staging, ArchiveFormat.CaptureEntry), "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n}\n");
            File.WriteAllText(Path.Combine(staging, "files", "cs2game", "autoexec.cfg"), "echo autoexec");
            string zip = Path.Combine(_dir, "backup.zip");
            ArchiveWriter.Write(staging, zip, new ArchiveContent(platform, new ArchiveTool("Advanced Config Backup", "test"), account, [], "test\n"),
                DateTimeOffset.UtcNow, CancellationToken.None, seal, identity: identity, otherSigner: _ => true);
            return zip;
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
    }
}
