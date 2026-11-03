using System.IO.Compression;
using System.Text;
using CS2OpenDev.ConfigManager.Archive;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     Self-contained archives: the restorer goes in under the one name for the archive's platform, is checked like
///     every other entry, may be larger than a settings file, and is found again from where it was unpacked.
/// </summary>
public class RestorerTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 2, 18, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(Platforms.WindowsX64, "restorer/acb.exe")]
    [Arguments(Platforms.LinuxX64, "restorer/acb")]
    public async Task ARestorer_GoesInUnderItsPlatformsName_AndReadsBackAsListed(string platform, string expected)
    {
        using TempDir temp = new();
        byte[] program = Encoding.ASCII.GetBytes("not really a program");

        string zip = Write(temp, platform, program);
        VerifiedArchive archive = ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform);

        await Assert.That(archive.Restorer).IsNotNull();
        await Assert.That(archive.Restorer!.Path).IsEqualTo(expected);
        await Assert.That(archive.Restorer.Sha256).IsEqualTo(Dsse.Sha256Hex(program));
        await Assert.That(archive.Read(expected).ToArray()).IsEquivalentTo(program);
    }

    [Test]
    public async Task AnArchiveWithoutOne_HasNoRestorer()
    {
        using TempDir temp = new();

        VerifiedArchive archive = ArchiveReader.Open(ArchiveV2Tests.Write(temp), ArchiveV2Tests.AnyPlatform);

        await Assert.That(archive.Restorer).IsNull();
    }

    /// <summary>unzip on Linux gives an entry the mode stored with it, so the restorer must unpack runnable.</summary>
    [Test]
    public async Task ALinuxRestorer_IsStoredRunnable()
    {
        using TempDir temp = new();

        string zip = Write(temp, Platforms.LinuxX64, [1, 2, 3]);

        using ZipArchive archive = ZipFile.OpenRead(zip);
        int mode = (archive.GetEntry("restorer/acb")!.ExternalAttributes >> 16) & 0xFFFF;
        await Assert.That(Convert.ToString(mode, 8)).IsEqualTo("100755");
    }

    /// <summary>acb itself is about 20 MB, over a settings file's cap but within a restorer's.</summary>
    [Test]
    public async Task ARestorer_MayBeLargerThanASettingsFile_ButNotPastItsOwnCap()
    {
        using TempDir temp = new();
        byte[] big = new byte[(int)ArchiveFormat.MaxEntryBytes + 1];

        string zip = Write(temp, Platforms.WindowsX64, big);
        await Assert.That(ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform).Restorer!.Size).IsEqualTo(big.LongLength);

        using TempDir again = new();
        await Assert.That(() => Write(again, Platforms.WindowsX64, new byte[(int)ArchiveFormat.MaxRestorerBytes + 1]))
            .Throws<IOException>().WithMessageContaining("restorer/acb.exe is over");
    }

    [Test]
    public async Task TheWriter_RefusesAStagingFolderThatHoldsARestorerOfItsOwn()
    {
        using TempDir temp = new();
        string staging = Staging(temp);
        Directory.CreateDirectory(Path.Combine(staging, "restorer"));
        File.WriteAllText(Path.Combine(staging, "restorer", "acb.exe"), "planted");

        await Assert.That(() => ArchiveWriter.Write(staging, temp.Sub("out.zip"), Content(Platforms.WindowsX64), Created, CancellationToken.None))
            .Throws<IOException>().WithMessageContaining("a name the archive writes itself");
    }

    /// <summary>A Linux restorer in an archive that says Windows (manifest and signature consistent) is still refused.</summary>
    [Test]
    public async Task ARestorerUnderAnotherPlatformsName_IsRefused_EvenWhenTheManifestListsIt()
    {
        using TempDir temp = new();
        string linux = Write(temp, Platforms.LinuxX64, [1, 2, 3]);
        byte[] manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ArchiveV2Tests.Entry(linux, ArchiveFormat.ManifestEntry))
            .Replace(Platforms.LinuxX64, Platforms.WindowsX64, StringComparison.Ordinal));
        string withManifest = ArchiveV2Tests.Rewrite(linux, temp, ArchiveFormat.ManifestEntry, _ => manifest);
        string forged = ArchiveV2Tests.Rewrite(withManifest, temp, ArchiveFormat.SignatureEntry, _ => ArchiveV2Tests.Envelope(Dsse.Sha256Hex(manifest), "hash", []));

        await Assert.That(() => ArchiveReader.Open(forged, ArchiveV2Tests.AnyPlatform))
            .Throws<ArchiveRejectedException>().WithMessageContaining("The only restorer a windows-x64 backup carries is restorer/acb.exe");
    }

    [Test]
    public async Task ARestorerAddedAfterTheBackupWasMade_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Rewrite(ArchiveV2Tests.Write(temp), temp, add: ("restorer/acb.exe", [1, 2, 3]));

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform))
            .Throws<ArchiveRejectedException>().WithMessageContaining("which its manifest does not list");
    }

    [Test]
    public async Task ARestorerChangedAfterTheBackupWasMade_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Rewrite(Write(temp, Platforms.WindowsX64, [1, 2, 3]), temp, "restorer/acb.exe", _ => [1, 2, 4]);

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform))
            .Throws<ArchiveRejectedException>().WithMessageContaining("'restorer/acb.exe' does not match the manifest");
    }

    [Test]
    public async Task AnUnpackedRestorer_FindsTheZipBesideItsFolder_AndNothingElse()
    {
        using TempDir temp = new();
        string unpacked = temp.Sub("AdvancedConfigBackup-20261102-180000");
        string exe = Path.Combine(unpacked, "restorer", "acb.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");

        await Assert.That(Restorers.HomeArchive(exe)).IsNull().Because("there is no zip beside the folder yet");

        string zip = unpacked + ".zip";
        File.WriteAllText(zip, "");
        await Assert.That(Restorers.HomeArchive(exe)).IsEqualTo(zip);

        string elsewhere = Path.Combine(unpacked, "acb.exe");
        File.WriteAllText(elsewhere, "");
        await Assert.That(Restorers.HomeArchive(elsewhere)).IsNull().Because("acb not in a restorer folder came from no backup");
        await Assert.That(Restorers.HomeArchive(null)).IsNull();
    }

    [Test]
    public async Task OnlyAPublishedAcb_IsCarried()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string name = ArchiveFormat.RestorerFileName(ArchiveFormat.CurrentPlatform!);
        string acb = temp.Sub(name);
        string host = temp.Sub("testhost.exe");
        File.WriteAllText(acb, "");
        File.WriteAllText(host, "");

        string released = temp.Sub(OperatingSystem.IsWindows() ? "acb-win-x64.exe" : "acb-linux-x64");
        string dll = temp.Sub("acb.dll");
        File.WriteAllText(released, "");
        File.WriteAllText(dll, "");

        await Assert.That(Restorers.Problem(acb, ArchiveFormat.CurrentPlatform!)).IsNull();
        await Assert.That(Restorers.Problem(released, ArchiveFormat.CurrentPlatform!)).IsNull().Because("a release names it for its platform");
        await Assert.That(Restorers.Problem(host, ArchiveFormat.CurrentPlatform!)).Contains("not a published " + name);
        await Assert.That(Restorers.Problem(dll, ArchiveFormat.CurrentPlatform!)).Contains("not a published " + name).Because("dotnet acb.dll runs the runtime");
        await Assert.That(Restorers.Problem(temp.Sub("missing"), ArchiveFormat.CurrentPlatform!)).Contains("cannot find its own program file");
    }

    private static string Staging(TempDir temp)
    {
        string staging = temp.Sub("staging");
        string cfg = Path.Combine(staging, "files", "cs2", "local", "cfg");
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(staging, "capture.vdf"), "\"ConfigBackup\"\n{\n}\n");
        File.WriteAllText(Path.Combine(cfg, "cs2_user_keys_0_slot0.vcfg"), "\"config\" {}");
        return staging;
    }

    private static ArchiveContent Content(string platform) =>
        new(platform, ArchiveV2Tests.Tool, "69413322", [new ArchiveSection("cs2", "Settings")], "Advanced Config Backup\n");

    private static string Write(TempDir temp, string platform, byte[] restorer)
    {
        string zip = temp.Sub("self-contained.zip");
        ArchiveWriter.Write(Staging(temp), zip, Content(platform), Created, CancellationToken.None, restorer: restorer);
        return zip;
    }
}
