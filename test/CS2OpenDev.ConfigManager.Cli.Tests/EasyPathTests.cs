using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     The path a person takes who reads nothing: a backup dropped on acb opens on restoring it, the restore screen
///     offers the backup it can find, a picked folder means the zip beside it, and Windows is asked for an
///     administrator unless the person unticks it.
/// </summary>
public class EasyPathTests
{
    [Test]
    public async Task ABackupDroppedOnAcb_OpensTheScreensOnRestoringIt()
    {
        string? opened = null;
        using TestHost host = new() { Wizard = (_, backup) => { opened = backup; return 7; } };
        Directory.CreateDirectory(host.Temp.Path);
        string zip = host.Temp.Sub("AdvancedConfigBackup-20261101-180000.zip");
        File.WriteAllText(zip, "");

        await Assert.That(host.Run(zip)).IsEqualTo(ExitCodes.Usage).Because("without a terminal there is no screen to open");
        await Assert.That(host.Errors).Contains("acb restore \"" + zip + "\"");

        host.Interactive = true;
        await Assert.That(host.Run(zip)).IsEqualTo(7);
        await Assert.That(opened).IsEqualTo(Path.GetFullPath(zip));

        await Assert.That(host.Run(host.Temp.Sub("missing.zip"))).IsEqualTo(ExitCodes.Usage).Because("a file that is not there is no backup to open");
        await Assert.That(host.Errors).Contains("is not an acb command");
    }

    [Test]
    public async Task TheRestoreScreen_OffersTheNewestBackup_InTheUsualFolders()
    {
        using TestHost host = new();
        string folder = host.Temp.Sub("Documents");
        Directory.CreateDirectory(folder);

        RestoreFlow none = new(host.Host());
        await Assert.That(none.ArchivePath).IsEqualTo("");
        await Assert.That(none.Found).IsNull();

        host.BackupFolders.Add(folder);
        host.BackupFolders.Add(host.Temp.Sub("not there"));
        string older = Path.Combine(folder, "AdvancedConfigBackup-20261001-120000.zip");
        string newer = Path.Combine(folder, "AdvancedConfigBackup-20261101-120000.zip");
        string other = Path.Combine(folder, "holiday photos.zip");
        foreach ((string file, int day) in new[] { (older, 1), (newer, 2), (other, 3) })
        {
            File.WriteAllText(file, "");
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc));
        }

        RestoreFlow flow = new(host.Host());
        await Assert.That(flow.ArchivePath).IsEqualTo(newer);
        await Assert.That(flow.Found).Contains("The newest backup in " + folder);

        RestoreFlow dropped = new(host.Host(), older);
        await Assert.That(dropped.ArchivePath).IsEqualTo(older).Because("the backup acb was opened with comes first");
    }

    /// <summary>A restorer double-clicked inside the zip in Explorer runs from a temp copy; the zip it names is found in the usual folders.</summary>
    [Test]
    public async Task ARestorerRunFromInsideTheZip_OffersThatZip()
    {
        using TestHost host = new();
        string downloads = host.Temp.Sub("Downloads");
        Directory.CreateDirectory(downloads);
        string zip = Path.Combine(downloads, "AdvancedConfigBackup-20261101-180000.zip");
        File.WriteAllText(zip, "");
        host.BackupFolders.Add(downloads);
        host.Executable = Path.Combine(host.Temp.Sub("Temp"), "Temp1_AdvancedConfigBackup-20261101-180000.zip", "restorer", "acb.exe");

        RestoreFlow flow = new(host.Host());

        await Assert.That(flow.ArchivePath).IsEqualTo(zip);
        await Assert.That(flow.Found).IsEqualTo("The backup this restorer came in.");
    }

    [Test]
    public async Task AnUnpackedFolderPicked_MeansTheZipBesideIt()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup");
        string zip = TestArchives.FromSteam(source, host.Temp, Kit.Sections.Cs2Game);
        string unpacked = Path.ChangeExtension(zip, null);
        Directory.CreateDirectory(unpacked);

        RestoreFlow flow = new(host.Host()) { ArchivePath = unpacked, SteamPath = target.Root };

        await Assert.That(flow.Check()).IsTrue().Because(string.Join('\n', flow.CheckLines()));
        await Assert.That(flow.ArchivePath).IsEqualTo(zip);
    }

    [Test]
    public async Task AskingWindowsForAnAdministrator_IsTickedWheneverWindowsCanAsk()
    {
        using TestHost host = new() { Elevation = new ElevationState(ElevationKind.Consent, "Windows asks you to confirm") };
        await Assert.That(new RestoreFlow(host.Host()).AskWindows).IsTrue();
        await Assert.That(new UndoFlow(host.Host()).AskWindows).IsTrue();

        host.Elevation = new ElevationState(ElevationKind.Unavailable, "Windows will not ask here");
        await Assert.That(new RestoreFlow(host.Host()).AskWindows).IsFalse();
        await Assert.That(new UndoFlow(host.Host()).AskWindows).IsFalse();
    }
}
