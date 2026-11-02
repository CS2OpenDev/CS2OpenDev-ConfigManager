using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     docs/design.md, "Failure policy": one test per row, each through acb's own command line. Headless runs take
///     the safe default and exit non-zero; interactive runs ask, and the default answer is the same safe one.
/// </summary>
public class FailurePolicyTests
{
    private const string Autoexec = "autoexec.cfg";

    /// <summary>Row 1: verification fails, the platform or format is unknown, or the policy refuses: stop, name the reason, exit 1.</summary>
    [Test]
    [Arguments("tampered")]
    [Arguments("platform")]
    [Arguments("format")]
    [Arguments("policy")]
    public async Task Row1_ARefusedArchive_StopsBeforeAnyChange_AndExits1(string why)
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        List<string> args = ["restore", "--yes", "--steam-path", target.Root];
        string expected;
        switch (why)
        {
            case "tampered":
                zip = TestArchives.Rewrite(zip, host.Temp, "files/cs2game/autoexec.cfg", t => t + "\nbind x quit");
                expected = "does not match the manifest";
                break;
            case "platform":
                string other = ArchiveFormat.CurrentPlatform == Platforms.WindowsX64 ? Platforms.LinuxX64 : Platforms.WindowsX64;
                zip = TestArchives.Craft(host.Temp, "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n}\n", platform: other);
                expected = "restore only on the platform they were made on";
                break;
            case "format":
                zip = TestArchives.Rewrite(zip, host.Temp, ArchiveFormat.ManifestEntry, t => t.Replace("\"acb/2\"", "\"acb/9\"", StringComparison.Ordinal));
                expected = "format 'acb/9'";
                break;
            default:
                string policy = host.Temp.Sub("policy.json");
                await File.WriteAllTextAsync(policy, """{ "minimumLevel": "identity" }""");
                args.AddRange(["--policy", policy]);
                expected = "at least 'identity' is required";
                break;
        }

        int code = host.Run([.. args, zip]);

        await Assert.That(code).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains(expected);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>Row 2: CS2 or Steam running. Headless: skip those sections, exit 2. Interactive: offer to wait, default skip.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Row2_Cs2OrSteamRunning_LeavesTheirSections_AndExits2(bool interactive)
    {
        using TestHost host = new() { Interactive = interactive };
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        host.Running.UnionWith(["cs2", "steam"]);
        if (interactive)
        {
            host.Answers.Enqueue("");   // the offer to wait: Enter alone leaves those parts
            host.Answers.Enqueue("y");  // restore what is left
        }

        int code = interactive
            ? host.Run("restore", "--steam-path", target.Root, zip)
            : host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("CS2 is running");
        await Assert.That(host.Output).Contains("Steam is running");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-target");
        if (interactive)
        {
            await Assert.That(host.Output).Contains("type r and Enter to check again");
        }
    }

    /// <summary>
    ///     Row 3: administrator needed. Headless: skip, exit 2, and no prompt. Interactive: offer UAC, default skip.
    ///     When Windows would not ask, the player is told so up front and never offered a prompt bound to fail.
    /// </summary>
    [Test]
    [Arguments("headless")]
    [Arguments("interactive-default")]
    [Arguments("unavailable")]
    public async Task Row3_AdministratorNeeded_IsSkippedByDefault_AndExits2(string how)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("only Windows has a section that needs an administrator");
            return;
        }

        using TestHost host = new() { Interactive = how != "headless" };
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = NvidiaArchive(host.Temp);
        if (how == "unavailable")
        {
            host.Elevation = new ElevationState(ElevationKind.Unavailable, "this PC turns down every request for an administrator from a standard user");
        }

        host.Answers.Enqueue("y");  // restore
        host.Answers.Enqueue("");   // the administrator question: Enter alone is no
        int code = how == "headless"
            ? host.Run("restore", "--yes", "--steam-path", target.Root, zip)
            : host.Run("restore", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Elevator.Calls).IsEmpty().Because("nobody asked for the Windows prompt");
        await Assert.That(host.Output).Contains("needs an administrator");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec").Because("the player's own sections still apply");
        if (how == "unavailable")
        {
            await Assert.That(host.Output).Contains("turns down every request");
            await Assert.That(host.Output).DoesNotContain("Ask Windows now?");
        }
    }

    /// <summary>Row 4: a target that is read-only to everyone, locked or denied: name it, skip the section, exit 2.</summary>
    [Test]
    public async Task Row4_ATargetThatCannotBeWritten_SkipsItsSection_AndExits2()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string video = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        string before = await File.ReadAllTextAsync(video);
        string zip = TestArchives.FromSteam(source, host.Temp);
        int code;
        using (Unwritable(video))
        {
            code = host.Run("restore", "--yes", "--only", "cs2,cs2game", "--steam-path", target.Root, zip);
        }

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("blocked");
        await Assert.That(host.Output).Contains("local/cfg/cs2_video.txt cannot be written");
        await Assert.That(await File.ReadAllTextAsync(video)).IsEqualTo(before);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec\nexec sd_core").Because("the other section still applies");
    }

    /// <summary>
    ///     Row 4 for a folder that refuses new files, as a cafe may lock the cfg folder: every write first adds a
    ///     temporary file there, so the plan already shows the part blocked, and the restore writes nothing and
    ///     exits 2 rather than failing partway.
    /// </summary>
    [Test]
    public async Task Row4_AFolderThatRefusesNewFiles_BlocksItsSection_InThePlanAndTheRestore()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        File.Delete(target.PathOf(FakeSteam.Cfg(Autoexec)));
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        string cfg = Path.GetDirectoryName(target.PathOf(FakeSteam.Cfg(Autoexec)))!;
        int plan;
        int restore;
        using (Permissions.NoNewFiles(cfg))
        {
            plan = host.Run("plan", "--steam-path", target.Root, zip);
            restore = host.Run("restore", "--yes", "--steam-path", target.Root, zip);
        }

        await Assert.That(plan).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(restore).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("blocked");
        await Assert.That(host.Output).Contains("does not let this user add files");
        await Assert.That(host.Output).DoesNotContain("failed");
        await Assert.That(File.Exists(target.PathOf(FakeSteam.Cfg(Autoexec)))).IsFalse();
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>
    ///     Row 4's folder lock does not hold up an undo that only removes a file the restore added: removing a file
    ///     adds nothing to the folder, so the end-of-session undo still takes the player's autoexec.cfg away.
    /// </summary>
    [Test]
    public async Task Row4_AFolderThatRefusesNewFiles_StillLetsUndoRemoveAFileTheRestoreAdded()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("on Linux, removing a file needs the same folder right as adding one");
        }

        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string autoexec = target.PathOf(FakeSteam.Cfg(Autoexec));
        File.Delete(autoexec);
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(File.Exists(autoexec)).IsTrue();
        int undo;
        using (Permissions.NoNewFiles(Path.GetDirectoryName(autoexec)!))
        {
            undo = host.Run("undo", "--yes", "--steam-path", target.Root);
        }

        await Assert.That(undo).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(File.Exists(autoexec)).IsFalse();
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>Row 5: a section that fails partway is rolled back from its undo record and reported; exit 3.</summary>
    [Test]
    public async Task Row5_ASectionThatFailsPartway_IsRolledBack_AndExits3()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string machine = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_machine_convars.vcfg");
        source.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.1\"\n\t}\n}\n");
        string before = target.Read(machine);
        string zip = TestArchives.FromSteam(source, host.Temp);
        BlockLaterWrite(target);

        int code = host.Run("restore", "--yes", "--only", "cs2", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("what it had changed was put back");
        await Assert.That(target.Read(machine)).IsEqualTo(before);
        await Assert.That(host.Records()).IsEmpty().Because("a record whose every change was rolled back has nothing left, and is filed");
    }

    /// <summary>Row 6: the rollback itself fails: stop everything, show the record, keep it; exit 4.</summary>
    [Test]
    public async Task Row6_ARollbackThatFails_StopsEverything_KeepsTheRecord_AndExits4()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        BlockLaterWrite(target);
        // The record's copy of the file already written is lost before the rollback reads it.
        host.BeforeRollback = dir => File.Delete(Path.Combine(dir, "files", "cs2", "local", "cfg", "cs2_machine_convars.vcfg"));

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        await Assert.That(host.Output).Contains("could not be put back");
        await Assert.That(host.Records().Single().Parts).Contains(Sections.Cs2).Because("the record is kept for acb undo");
        await Assert.That(host.Output).Contains(host.Records().Single().Dir);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target").Because("nothing after the failed part ran");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-target");
    }

    /// <summary>
    ///     Row 7: an undo record that cannot be read. Headless: leave it, exit 4. Interactive: offer retry, set
    ///     aside or leave; leave is the default, and set aside lets the older records go ahead.
    /// </summary>
    [Test]
    [Arguments("headless")]
    [Arguments("interactive-default")]
    [Arguments("interactive-set-aside")]
    public async Task Row7_AnUnreadableUndoRecord_IsLeftByDefault_AndExits4(string how)
    {
        using TestHost host = new() { Interactive = how != "headless" };
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        RestoreTwice(host, target);
        UndoRecord newest = host.Records()[0];
        await File.WriteAllTextAsync(Path.Combine(newest.Dir, ConfigBackup.ManifestName), "{{{ damaged");
        host.Answers.Enqueue("y");
        host.Answers.Enqueue(how == "interactive-set-aside" ? "s" : "");

        int code = how == "headless"
            ? host.Run("undo", "--yes", "--steam-path", target.Root)
            : host.Run("undo", "--steam-path", target.Root);

        if (how == "interactive-set-aside")
        {
            // The older record went back, but what the one set aside changed stays, so the PC is not as it was.
            await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
            await Assert.That(host.Output).Contains("Records set aside were not put back");
            await Assert.That(host.Output).DoesNotContain("the PC is back as it was");
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
            await Assert.That(Directory.Exists(Path.Combine(host.UserStore, UndoStore.SetAsideFolder, newest.Name))).IsTrue();
            return;
        }

        await Assert.That(code).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        await Assert.That(host.Output).Contains("cannot be read");
        await Assert.That(host.Output).Contains("acb undo --set-aside " + newest.Name);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo B").Because("the older record waits for the unreadable one");
        await Assert.That(Directory.Exists(newest.Dir)).IsTrue();
    }

    /// <summary>Row 8: an undo record that cannot be claimed (a full disk): delete the half-made claim, stop before any change, exit 1.</summary>
    [Test]
    public async Task Row8_AnUndoRecordThatCannotBeClaimed_ChangesNothing_AndExits1()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        int code;
        using (RefuseRecords(host.UserStore))
        {
            code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);
        }

        await Assert.That(code).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("Stopped before the first change");
        await Assert.That(Directory.GetFileSystemEntries(host.UserStore)).IsEmpty().Because("no half-made claim is left");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-target");
    }

    /// <summary>Row 9: a dry run or plan changes nothing, file attributes included, and claims no record.</summary>
    [Test]
    [Arguments("plan")]
    [Arguments("restore --dry-run")]
    [Arguments("undo --dry-run")]
    public async Task Row9_ADryRun_ChangesNothing_AttributesIncluded(string command)
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp);
        if (command.StartsWith("undo", StringComparison.Ordinal))
        {
            await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        }

        string video = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        string autoexec = target.PathOf(FakeSteam.Cfg(Autoexec));
        File.SetAttributes(video, FileAttributes.ReadOnly);
        File.SetAttributes(autoexec, FileAttributes.ReadOnly);
        DateTime videoWritten = File.GetLastWriteTimeUtc(video);
        string videoText = await File.ReadAllTextAsync(video);
        string autoexecText = await File.ReadAllTextAsync(autoexec);
        int records = host.Records().Count;
        try
        {
            string[] args = [.. command.Split(' '), "--steam-path", target.Root, .. command.StartsWith("undo", StringComparison.Ordinal) ? Array.Empty<string>() : [zip]];
            int code = host.Run(args);

            await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
            await Assert.That(host.Output).Contains("would");
            await Assert.That(File.GetAttributes(video).HasFlag(FileAttributes.ReadOnly)).IsTrue();
            await Assert.That(File.GetAttributes(autoexec).HasFlag(FileAttributes.ReadOnly)).IsTrue();
            await Assert.That(File.GetLastWriteTimeUtc(video)).IsEqualTo(videoWritten);
            await Assert.That(await File.ReadAllTextAsync(video)).IsEqualTo(videoText);
            await Assert.That(await File.ReadAllTextAsync(autoexec)).IsEqualTo(autoexecText);
            await Assert.That(host.Records().Count).IsEqualTo(records).Because("a dry run claims no record and files none away");
        }
        finally
        {
            File.SetAttributes(video, FileAttributes.Normal);
            File.SetAttributes(autoexec, FileAttributes.Normal);
        }
    }

    // -- Helpers ------------------------------------------------------------------------------------------

    /// <summary>A hand-made archive with the player's cfg and the NVIDIA section, which only an administrator applies.</summary>
    internal static string NvidiaArchive(TempDir temp, SealKey? seal = null)
    {
        const string autoexec = "echo autoexec";
        return TestArchives.Craft(temp, $$"""
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"cs2game"
            	{
            		"files"
            		{
            			"autoexec.cfg"		"{{TestArchives.Sha(autoexec)}}"
            		}
            	}
            	"nvidia"
            	{
            		"cs2"
            		{
            			"settings"
            			{
            				"0x10835002"
            				{
            					"name"		"Frame Rate Limiter"
            					"type"		"dword"
            					"value"		"0"
            				}
            			}
            		}
            	}
            }
            """, new Dictionary<string, string> { ["files/cs2game/autoexec.cfg"] = autoexec }, seal: seal);
    }

    /// <summary>
    ///     A folder where cs2_user_keys_0_slot0.vcfg goes: no check before the first write looks for it, so the cs2
    ///     section writes cs2_machine_convars.vcfg and then fails.
    /// </summary>
    internal static void BlockLaterWrite(FakeSteam target)
    {
        string keys = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg"));
        File.Delete(keys);
        Directory.CreateDirectory(keys);
    }

    /// <summary>Two restores of autoexec.cfg, A then B, with the player's store holding both records.</summary>
    internal static void RestoreTwice(TestHost host, FakeSteam target)
    {
        foreach (string tag in new[] { "A", "B" })
        {
            using FakeSteam source = new();
            source.Write(FakeSteam.Cfg(Autoexec), "echo " + tag);
            string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
            int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);
            if (code != ExitCodes.Done)
            {
                throw new InvalidOperationException(host.All);
            }
        }
    }

    internal static IDisposable Unwritable(string path) => Permissions.Unwritable(path);

    internal static IDisposable RefuseRecords(string root) => Permissions.RefuseRecords(root);
}
