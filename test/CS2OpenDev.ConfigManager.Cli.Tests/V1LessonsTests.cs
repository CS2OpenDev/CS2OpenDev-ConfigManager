using System.IO.Compression;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     What v1 taught, each held by a test of acb itself: rules kept from v1 (Keep_), and v1 bugs that must not come
///     back (NotAgain_). See also MachineSectionsTests.Nvidia_WritesOnlyTheCs2Profile_OnRestoreAndUndo and
///     UndoStoreTests.AStoreFolderAPlayerMadeFirst_IsRefused in Core's suite.
/// </summary>
public class V1LessonsTests
{
    private const string Autoexec = "autoexec.cfg";

    /// <summary>Restore into the backup's own Steam account only, never into whoever signed in last.</summary>
    [Test]
    public async Task Keep_RestoresOnlyIntoTheBackupsOwnAccount_NeverTheOneThatSignedInLast()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(accountId: "12345678", launchOptions: "-stranger");
        string strangerConvars = target.Read(FakeSteam.User("12345678", "local/cfg/cs2_user_convars_0_slot0.vcfg"));
        string zip = TestArchives.FromSteam(source, host.Temp);

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("has not signed in to Steam on this PC");
        await Assert.That(target.Read(FakeSteam.User("12345678", "local/cfg/cs2_user_convars_0_slot0.vcfg"))).IsEqualTo(strangerConvars);
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions("12345678")).IsEqualTo("-stranger");
        await Assert.That(target.Exists(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"))).IsFalse();
    }

    /// <summary>Steam Cloud's remote/ and remotecache.vdf are never captured, and never written even when an archive names them.</summary>
    [Test]
    public async Task Keep_SteamCloudsFolder_IsNeverCapturedNorWritten()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = host.Temp.Sub("made.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);
        using (ZipArchive archive = ZipFile.OpenRead(zip))
        {
            await Assert.That(archive.Entries.Select(e => e.FullName).Where(n => n.Contains("remote", StringComparison.Ordinal))).IsEmpty();
        }

        using FakeSteam target = new();
        string cloud = target.Read(FakeSteam.User(FakeSteam.AccountId, "remote/cs2_user_keys.vcfg"));
        string crafted = TestArchives.Craft(host.Temp, Capture(Sections.Cs2, "remote/cs2_user_keys.vcfg", "bind x quit", account: true),
            new Dictionary<string, string> { ["files/cs2/remote/cs2_user_keys.vcfg"] = "bind x quit" });

        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, crafted)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("left alone, as files this tool does not restore: remote/cs2_user_keys.vcfg");
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "remote/cs2_user_keys.vcfg"))).IsEqualTo(cloud);
    }

    /// <summary>
    ///     NVIDIA settings go only through the administrator's run, which takes nothing but the sections that need
    ///     an administrator (Core's suite holds the allowlist and the CS2-profile-only rule).
    /// </summary>
    [Test]
    public async Task Keep_NvidiaSettings_GoOnlyToTheAdministratorsRun_AndItTakesNothingElse()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("only Windows has a section that needs an administrator");
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);
        host.Elevator.Answer = new ElevatedResult(false, ExitCodes.Done,
            new CommandReport { Sections = [new SectionLine(Sections.Nvidia, Sections.Label(Sections.Nvidia), "done", "changed 1 settings on the CS2 profile")] }, null);

        int code = host.Run("restore", "--yes", "--elevate", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        IReadOnlyList<string> call = host.Elevator.Calls.Single();
        await Assert.That(call[0]).IsEqualTo("__elevated");
        await Assert.That(call[call.ToList().IndexOf("--only") + 1]).IsEqualTo(Sections.Nvidia);
        await Assert.That(host.Output).Contains("changed 1 settings on the CS2 profile");

        host.Elevation = new ElevationState(ElevationKind.Elevated, "acb runs as an administrator");
        await Assert.That(host.Run("__elevated", "restore", zip, "--digest", ArchiveReader.Open(zip, new ArchiveReadOptions()).ManifestSha256, "--only", "nvidia,cs2game"))
            .IsEqualTo(ExitCodes.Usage).Because("the administrator's run applies only what needs an administrator");
    }

    /// <summary>Hardware keys keep the target's values: its GPU and monitor stay named, and the source's never land.</summary>
    [Test]
    public async Task Keep_HardwareKeys_KeepTheTargetsValues()
    {
        using TestHost host = new();
        using FakeSteam source = new(vendorId: "4318");
        using FakeSteam target = new(vendorId: "4098");
        string zip = TestArchives.FromSteam(source, host.Temp);

        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        KvNode video = KvNode.Load(target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt")));
        await Assert.That(video.Get("VendorID")).IsEqualTo("4098");
        await Assert.That(video.Get("setting.defaultres")).IsEqualTo("2560").Because("the player's own choices travel");
    }

    /// <summary>Secrets are removed at capture, the archived password convar in *_convars.vcfg included.</summary>
    [Test]
    public async Task Keep_SecretsAreLeftOutOfTheBackup_TheArchivedPasswordConvarIncluded()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg(Autoexec), "echo hi\nrcon_password hunter2\nbind f \"connect 1.2.3.4; password club\"\n");
        source.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"),
            "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"password\"\t\t\"club\"\n\t\t\"tv_password\"\t\t\"tv\"\n\t\t\"sensitivity\"\t\t\"1.2\"\n\t}\n}\n");
        string zip = host.Temp.Sub("made.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2,cs2game", "--steam-path", source.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);

        VerifiedArchive archive = ArchiveReader.Open(zip, new ArchiveReadOptions());
        string all = string.Join("\n", archive.Manifest.Entries.Select(e => ConfigBackup.Text(archive.Read(e.Path).ToArray())));
        await Assert.That(all).DoesNotContain("hunter2");
        await Assert.That(all).DoesNotContain("club");
        await Assert.That(all).DoesNotContain("\"tv\"");
        await Assert.That(all).Contains("sensitivity");
    }

    /// <summary>The undo record is written before each change: when a part fails partway, the change it made is already on record.</summary>
    [Test]
    public async Task Keep_TheUndoRecordIsWrittenBeforeEachChange()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp);
        FailurePolicyTests.BlockLaterWrite(target);
        KvNode? onRecord = null;
        host.BeforeRollback = dir => onRecord = ConfigBackup.ReadManifest(dir);

        await Assert.That(host.Run("restore", "--yes", "--only", "cs2", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.PartFailed).Because(host.All);

        KvNode files = onRecord!.Path(Sections.Cs2, "files")!;
        await Assert.That(files.Child("local/cfg/cs2_machine_convars.vcfg")).IsNotNull().Because("written before the failure, so recorded before it was written");
        await Assert.That(files.Child("local/cfg/cs2_user_keys_0_slot0.vcfg")).IsNotNull().Because("the write that failed was recorded first too");
    }

    /// <summary>Undo applies newest first, and an older record's part waits while a newer record still holds it.</summary>
    [Test]
    public async Task Keep_UndoGoesNewestFirst_WithTheHoldBackRule()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        FailurePolicyTests.RestoreTwice(host, target);
        host.Running.Add("cs2");

        await Assert.That(host.Run("undo", "--yes", "--steam-path", target.Root)).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("waiting: a newer record of the same part has to be put back first");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo B");

        host.Running.Clear();
        await Assert.That(host.Run("undo", "--yes", "--steam-path", target.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
    }

    /// <summary>
    ///     v1 read restore-or-undo from the manifest, so a backup saying kind "undo" skipped the path rules. acb's
    ///     command decides: an archive is always restored under the rules, and undo takes no archive at all.
    /// </summary>
    [Test]
    public async Task NotAgain_AnArchiveCannotMakeItselfAnUndo()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        string valve = target.Read(FakeSteam.Cfg("gamemode_competitive.cfg"));
        string crafted = TestArchives.Craft(host.Temp, Capture(Sections.Cs2Game, "gamemode_competitive.cfg", "mp_maxrounds 1", kind: "undo"),
            new Dictionary<string, string> { ["files/cs2game/gamemode_competitive.cfg"] = "mp_maxrounds 1" });

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, crafted);

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("left alone, as files this tool does not restore: gamemode_competitive.cfg");
        await Assert.That(target.Read(FakeSteam.Cfg("gamemode_competitive.cfg"))).IsEqualTo(valve);
        await Assert.That(host.Run("undo", crafted)).IsEqualTo(ExitCodes.Usage).Because("undo reads only the restorer's own records");
    }

    /// <summary>
    ///     v1's elevated rerun judged the backup folder by the elevated account's SID, which refused an organiser's
    ///     password. acb's administrator's run reads no folder the player owns: it takes the archive's bytes over its
    ///     pipe and never opens the path it is shown (here a share that does not exist, which it would otherwise sign
    ///     in to as the administrator), re-checks them against the digest it was given, and records only into the
    ///     administrator's store, which it checks itself.
    /// </summary>
    [Test]
    public async Task NotAgain_TheAdministratorsRun_TrustsNoFolderThePlayerOwns()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("only Windows has an administrator's run");
            return;
        }

        using TestHost host = new() { Elevation = new ElevationState(ElevationKind.Elevated, "acb runs as an administrator") };
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);
        string digest = ArchiveReader.Open(zip, new ArchiveReadOptions()).ManifestSha256;
        const string share = @"\\192.0.2.1\backups\x.zip";

        int unsent = host.Run("__elevated", "restore", zip, "--digest", digest, "--only", "nvidia");

        await Assert.That(unsent).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("never from a path");
        await Assert.That(host.MachineStoreChecks).IsEmpty();

        host.ElevatedInput = [File.ReadAllBytes(zip), null, null];
        int swapped = host.Run("__elevated", "restore", share, "--digest", new string('0', 64), "--only", "nvidia", "--pipe", "acb-test");

        await Assert.That(swapped).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("not the one the first run checked");
        await Assert.That(host.MachineStoreChecks).IsEmpty();

        host.MachineStoreProblem = new UnauthorizedAccessException("C:\\ProgramData\\CS2OpenDev is owned by PC\\player, not by administrators");
        int planted = host.Run("__elevated", "restore", share, "--digest", digest, "--only", "nvidia", "--pipe", "acb-test");

        await Assert.That(planted).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.MachineStoreChecks.Single()).IsEqualTo(host.MachineStore).Because("the administrator's store, never the player's");
        await Assert.That(Directory.Exists(host.UserStore)).IsFalse().Because("the administrator's run never touches the player's store");
        await Assert.That(host.Channels[^1].Channel.Sent.Single().Notes.Exists(n => n.Contains(@"is owned by PC\player", StringComparison.Ordinal))).IsTrue()
            .Because("its report goes back over the pipe");

        host.Elevation = new ElevationState(ElevationKind.Credentials, "Windows asks for an administrator's name and password");
        await Assert.That(host.Run("__elevated", "restore", share, "--digest", digest, "--only", "nvidia", "--pipe", "acb-test")).IsEqualTo(ExitCodes.Refused)
            .Because("started without an administrator, it does nothing");
    }

    /// <summary>v1's dry run toggled read-only attributes. acb plan changes nothing at all.</summary>
    [Test]
    public async Task NotAgain_ADryRunTogglesNoAttribute()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp);
        string video = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        File.SetAttributes(video, FileAttributes.ReadOnly);
        try
        {
            FileAttributes before = File.GetAttributes(video);

            await Assert.That(host.Run("plan", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
            await Assert.That(File.GetAttributes(video)).IsEqualTo(before);
        }
        finally
        {
            File.SetAttributes(video, FileAttributes.Normal);
        }
    }

    /// <summary>v1 left one unreadable snapshot blocking every older one forever. acb undo --set-aside is the way out.</summary>
    [Test]
    public async Task NotAgain_AnUnreadableRecordBlocksForever()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        FailurePolicyTests.RestoreTwice(host, target);
        UndoRecord newest = host.Records()[0];
        await File.WriteAllTextAsync(Path.Combine(newest.Dir, ConfigBackup.ManifestName), "{{{");

        await Assert.That(host.Run("undo", "--yes", "--steam-path", target.Root)).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        await Assert.That(host.Run("undo", "--set-aside", newest.Name)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Run("undo", "--yes", "--steam-path", target.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
    }

    /// <summary>v1 left a hundred half-made snapshot folders after a claim failed on a full disk. Three failed restores leave none.</summary>
    [Test]
    public async Task NotAgain_AFailedClaimLeavesJunkFolders()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp);
        using (FailurePolicyTests.RefuseRecords(host.UserStore))
        {
            for (int i = 0; i < 3; i++)
            {
                await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused).Because(host.All);
            }
        }

        await Assert.That(Directory.GetFileSystemEntries(host.UserStore)).IsEmpty();
    }

    /// <summary>v1 moved a whole snapshot into applied after a partial undo. A record is filed only once nothing is left in it.</summary>
    [Test]
    public async Task NotAgain_APartialUndoFilesTheWholeRecord()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        int code = host.Run("undo", "--yes", "--only", "cs2game", "--steam-path", target.Root);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because("the PC is not all the way back: " + host.All);
        await Assert.That(host.Records().Single().Parts).IsEquivalentTo([Sections.Cs2, Sections.Launch]);
        await Assert.That(Directory.Exists(Path.Combine(host.UserStore, UndoStore.AppliedFolder))).IsFalse();
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-novid -high")
            .Because("the launch options were not part of this undo");
    }

    /// <summary>A capture.vdf holding one file in one section, written by hand.</summary>
    internal static string Capture(string section, string relative, string content, bool account = false, string kind = "backup") => $$"""
        "ConfigBackup"
        {
        	"format"		"1"
        	"kind"		"{{kind}}"
        	"{{section}}"
        	{
        {{(account ? $"\t\t\"account\"\t\t\"{FakeSteam.AccountId}\"\n" : "")}}		"files"
        		{
        			"{{relative}}"		"{{TestArchives.Sha(content)}}"
        		}
        	}
        }
        """;
}
