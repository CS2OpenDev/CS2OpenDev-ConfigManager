using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     Self-contained backups through acb: <c>backup --self-contained</c> carries acb's own program file, every
///     command that opens one says whether its restorer is this acb, and a restorer unpacked beside its backup
///     offers that backup in the wizard. The "program" is a stand-in file, never the test host.
/// </summary>
public class SelfContainedTests
{
    private static readonly string ExeName = ArchiveFormat.RestorerFileName(ArchiveFormat.CurrentPlatform!);

    [Test]
    public async Task BackupSelfContained_CarriesThisAcb_AndVerifySaysItIsTheSameProgram()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        host.Executable = Program(host.Temp, "bin", "acb build 1");
        string zip = host.Temp.Sub("backup.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--self-contained"))
            .IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("It carries this acb as " + ArchiveFormat.RestorerEntry(ArchiveFormat.CurrentPlatform!));

        VerifiedArchive archive = ArchiveReader.Open(zip, new ArchiveReadOptions());
        await Assert.That(archive.Read(archive.Restorer!.Path).ToArray()).IsEquivalentTo(File.ReadAllBytes(host.Executable));
        await Assert.That(Encoding.UTF8.GetString(archive.Read(ArchiveFormat.ReadmeEntry).Span)).Contains("This backup carries its own restorer");

        using TestHost json = new() { Executable = host.Executable };
        await Assert.That(json.Run("verify", "--json", zip)).IsEqualTo(ExitCodes.Done).Because(json.All);
        CommandReport report = JsonSerializer.Deserialize(json.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.RestorerSha256).IsEqualTo(archive.Restorer.Sha256);
        await Assert.That(report.RestorerIsThisAcb).IsTrue();
        await Assert.That(json.Errors).Contains("the same program as this acb");
    }

    /// <summary>
    ///     Whoever made a signed backup chose the restorer it carries, and with it which stolen keys that restorer knows
    ///     of. So when the acb checking it is that restorer, its verdict says to check again with an acb installed on
    ///     its own; another acb's verdict does not.
    /// </summary>
    [Test]
    public async Task ASignedBackup_CheckedByTheRestorerItCarries_SaysToCheckAgainWithAnInstalledAcb()
    {
        using TestHost host = new() { IdentityService = IdentityCommandTests.Service, Interactive = true };
        using FakeSteam source = new();
        host.Executable = Program(host.Temp, "bin", "acb build 1");
        string zip = host.Temp.Sub("signed.zip");
        host.Answers.Enqueue("y");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--self-contained", "--sign"))
            .IsEqualTo(ExitCodes.Done).Because(host.All);

        using TestHost carried = new() { Executable = host.Executable };
        await Assert.That(carried.Run("verify", zip)).IsEqualTo(ExitCodes.Done).Because(carried.All);
        await Assert.That(carried.All).Contains(Archives.CarriedRestorer);
        using FakeSteam target = new();
        RestoreFlow flow = new(carried.Host()) { ArchivePath = zip, SteamPath = target.Root };
        await Assert.That(flow.Check()).IsTrue();
        await Assert.That(flow.CheckedByCarriedRestorer).IsTrue();
        await Assert.That(string.Join('\n', flow.CheckLines())).Contains(Archives.CarriedRestorer);

        using TestHost installed = new();
        installed.Executable = Program(installed.Temp, "bin", "acb build 2");
        await Assert.That(installed.Run("verify", zip)).IsEqualTo(ExitCodes.Done).Because(installed.All);
        await Assert.That(installed.All).Contains("checked against the keys revoked up to");
        await Assert.That(installed.All).DoesNotContain(Archives.CarriedRestorer);
    }

    [Test]
    public async Task Verify_ABackupCarryingAnotherBuild_SaysToCheckItBeforeRunningIt()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        host.Executable = Program(host.Temp, "bin", "acb build 1");
        string zip = host.Temp.Sub("backup.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--self-contained"))
            .IsEqualTo(ExitCodes.Done).Because(host.All);

        using TestHost other = new();
        other.Executable = Program(other.Temp, "bin", "acb build 2");
        await Assert.That(other.Run("verify", "--json", zip)).IsEqualTo(ExitCodes.Done).Because(other.All);

        CommandReport report = JsonSerializer.Deserialize(other.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.RestorerIsThisAcb).IsFalse();
        await Assert.That(other.Errors).Contains("not this acb's build");
        await Assert.That(other.Errors).Contains("SHA256SUMS");
    }

    [Test]
    public async Task Verify_ABackupWithoutARestorer_SaysNothingAboutOne()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = host.Temp.Sub("backup.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.Run("verify", "--json", zip)).IsEqualTo(ExitCodes.Done);

        await Assert.That(host.Errors).DoesNotContain("Restorer:");
        await Assert.That(host.Output).DoesNotContain("restorerSha256");
    }

    /// <summary>Under a test runner or <c>dotnet acb.dll</c>, the process is not acb, and carrying it would carry the wrong program.</summary>
    [Test]
    public async Task BackupSelfContained_FromAnythingButAPublishedAcb_IsRefused_AndWritesNothing()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        Directory.CreateDirectory(host.Temp.Path);
        host.Executable = host.Temp.Sub("testhost.exe");
        File.WriteAllText(host.Executable, "a test runner");
        string zip = host.Temp.Sub("backup.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--self-contained"))
            .IsEqualTo(ExitCodes.Refused);

        await Assert.That(host.Output).Contains("not a published " + ExeName);
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    /// <summary>Extract All puts the folder beside the zip under its name; the restorer inside offers that zip, checked as any other.</summary>
    [Test]
    public async Task TheWizard_StartedFromAnUnpackedRestorer_OffersItsBackup_AndSaysItIsTheSameProgram()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        host.Executable = Program(host.Temp, "bin", "acb build 1");
        string zip = host.Temp.Sub("AdvancedConfigBackup-20261102-180000.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--self-contained"))
            .IsEqualTo(ExitCodes.Done).Because(host.All);
        string unpacked = Path.Combine(host.Temp.Sub("AdvancedConfigBackup-20261102-180000"), "restorer", ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(unpacked)!);
        File.Copy(host.Executable, unpacked);

        using TestHost player = new() { Executable = unpacked };
        RestoreFlow flow = new(player.Host()) { SteamPath = target.Root };

        await Assert.That(flow.ArchivePath).IsEqualTo(zip);
        await Assert.That(flow.Check()).IsTrue().Because(string.Join('\n', flow.CheckLines()));
        await Assert.That(string.Join('\n', flow.CheckLines())).Contains("the same program as this acb");
    }

    [Test]
    public async Task TheWizard_StartedFromAnywhereElse_OffersNoBackup()
    {
        using TestHost host = new();
        host.Executable = Program(host.Temp, "bin", "acb");

        RestoreFlow flow = new(host.Host());

        await Assert.That(flow.ArchivePath).IsEqualTo("");
    }

    private static string Program(TempDir temp, string folder, string content)
    {
        string path = Path.Combine(temp.Sub(folder), ExeName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
