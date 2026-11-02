using System.IO.Compression;
using CS2OpenDev.ConfigManager.Kit;
using KitBackup = CS2OpenDev.ConfigManager.Kit.ConfigBackup;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     How capture, restore and undo cope with files that are read-only or locked, how undo snapshots are named
///     and ordered, and the checks every value passes before it reaches the driver or powercfg. Restores here go
///     only into fake Steam folders, over the file sections.
/// </summary>
public class RobustnessTests
{
    [Test]
    public async Task AReadOnlySourceCfg_IsFilteredBeforeItIsStaged_AndTheBackupSucceeds()
    {
        using FakeSteam steam = new();
        steam.Write(FakeSteam.Cfg("practice/prac.cfg"), "rcon_password hunter2\nsv_cheats 1\n");
        string prac = steam.PathOf(FakeSteam.Cfg("practice/prac.cfg"));
        string video = steam.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        File.SetAttributes(prac, FileAttributes.ReadOnly);
        File.SetAttributes(video, FileAttributes.ReadOnly);
        using TempDir temp = new();
        try
        {
            CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test" };
            options.Include.Clear();
            options.Include.AddRange([Sections.Cs2, Sections.Cs2Game]);
            string zip = temp.Sub("backup.zip");

            RunReport report = BackupBuilder.Create(options, zip, ArchiveV2Tests.Tool, DateTimeOffset.UtcNow, CancellationToken.None);

            await Assert.That(report.Count("failed")).IsEqualTo(0).Because(string.Join("; ", report.Results));
            using ZipArchive archive = ZipFile.OpenRead(zip);
            ZipArchiveEntry? staged = archive.GetEntry("files/cs2game/practice/prac.cfg");
            await Assert.That(staged).IsNotNull();
            using StreamReader reader = new(staged!.Open());
            await Assert.That(await reader.ReadToEndAsync()).DoesNotContain("hunter2");
            await Assert.That(archive.GetEntry("files/cs2/local/cfg/cs2_video.txt")).IsNotNull();
        }
        finally
        {
            File.SetAttributes(prac, FileAttributes.Normal);
            File.SetAttributes(video, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task APartThatFailsPartway_IsLeftOutOfTheBackupWhole()
    {
        using FakeSteam steam = new();
        using TempDir temp = new();
        CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test" };
        options.Include.Clear();
        options.Include.AddRange([Sections.Cs2, Sections.Cs2Game]);
        string zip = temp.Sub("backup.zip");
        RunReport report;
        // sd_core.cfg sorts after autoexec.cfg, so the part has staged a file before it fails.
        using (new FileStream(steam.PathOf(FakeSteam.Cfg("sd_core.cfg")), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = BackupBuilder.Create(options, zip, ArchiveV2Tests.Tool, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2Game).Outcome).IsEqualTo("failed");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Outcome).IsEqualTo("done");
        using ZipArchive archive = ZipFile.OpenRead(zip);
        await Assert.That(archive.Entries.Select(e => e.FullName).Where(n => n.StartsWith("files/cs2game/", StringComparison.Ordinal))).IsEmpty();
        using StreamReader capture = new(archive.GetEntry(CS2OpenDev.ConfigManager.Archive.ArchiveFormat.CaptureEntry)!.Open());
        KvNode record = KitBackup.ParseManifest(await capture.ReadToEndAsync(), "capture.vdf");
        await Assert.That(record.Child(Sections.Cs2Game)).IsNull();
        await Assert.That(record.Child(Sections.Cs2)).IsNotNull();
    }

    /// <summary>Cafes and players make cs2_video.txt read-only so CS2 cannot reset it; restore and undo both keep that.</summary>
    [Test]
    public async Task AReadOnlyTarget_IsRestoredAndUndone_AndStaysReadOnly()
    {
        using FakeSteam source = new(vendorId: "4318");
        using FakeSteam target = new(vendorId: "1002");
        string videoRelative = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt");
        string keysRelative = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg");
        source.Write(keysRelative, "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"F\"\t\t\"+use\"\n\t}\n}\n");
        target.Write(videoRelative, FakeSteam.Video("1002", "1", "1920", "1080"));
        string originalVideo = target.Read(videoRelative);
        string originalKeys = target.Read(keysRelative);
        string video = target.PathOf(videoRelative);
        string keys = target.PathOf(keysRelative);
        File.SetAttributes(video, FileAttributes.ReadOnly);
        File.SetAttributes(keys, FileAttributes.ReadOnly);
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        options.Only = [Sections.Cs2];

        RunReport restored = Engine.Restore(temp.Path, options);

        await Assert.That(restored.Results.Single().Outcome).IsEqualTo("done").Because(string.Join("; ", restored.Results));
        await Assert.That(target.Read(videoRelative)).Contains("2560");
        await Assert.That(target.Read(keysRelative)).Contains("+use");
        await Assert.That(File.GetAttributes(video).HasFlag(FileAttributes.ReadOnly)).IsTrue();
        foreach (string copy in Directory.GetFiles(restored.UndoDir!, "*", SearchOption.AllDirectories))
        {
            await Assert.That(File.GetAttributes(copy).HasFlag(FileAttributes.ReadOnly)).IsFalse().Because(copy);
        }

        RunReport undone = Engine.Undo(restored.UndoDir!, options);

        await Assert.That(undone.Pending).IsEqualTo(0).Because(string.Join("; ", undone.Results));
        await Assert.That(target.Read(videoRelative)).IsEqualTo(originalVideo);
        await Assert.That(target.Read(keysRelative)).IsEqualTo(originalKeys);
        await Assert.That(File.GetAttributes(video).HasFlag(FileAttributes.ReadOnly)).IsTrue();
        await Assert.That(File.GetAttributes(keys).HasFlag(FileAttributes.ReadOnly)).IsTrue();
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(video)!, "*.acb-tmp")).IsEmpty();
        File.SetAttributes(video, FileAttributes.Normal);
        File.SetAttributes(keys, FileAttributes.Normal);
    }

    [Test]
    public async Task ALockedTarget_StopsThePartBeforeAnyWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("Linux has no share-mode locks: a file another process holds open does not stop a write");
        }

        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        string machine = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_machine_convars.vcfg");
        target.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.9\"\n\t}\n}\n");
        string before = target.Read(machine);
        using TempDir temp = new();
        source.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.1\"\n\t}\n}\n");
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        options.Only = [Sections.Cs2];
        RunReport report;
        // cs2_video.txt sorts after cs2_machine_convars.vcfg, so a check made only at its write would be too late.
        using (new FileStream(target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt")), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            report = Engine.Restore(temp.Path, options);
        }

        await Assert.That(report.Results.Single().Outcome).IsEqualTo("blocked");
        await Assert.That(report.Results.Single().Detail).Contains("nothing in this part was written");
        await Assert.That(target.Read(machine)).IsEqualTo(before);
        await Assert.That(report.UndoDir).IsNull().Because("nothing changed, so no snapshot was claimed");
    }

    [Test]
    public async Task AReadOnlyLockedTarget_StopsThePartBeforeAnyWrite_AndStaysReadOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("Linux has no share-mode locks: a file another process holds open does not stop a write");
        }

        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        string machine = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_machine_convars.vcfg");
        target.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.9\"\n\t}\n}\n");
        string before = target.Read(machine);
        using TempDir temp = new();
        source.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.1\"\n\t}\n}\n");
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        options.Only = [Sections.Cs2];
        string video = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        File.SetAttributes(video, FileAttributes.ReadOnly);
        RunReport report;
        try
        {
            using (new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                report = Engine.Restore(temp.Path, options);
            }

            await Assert.That(File.GetAttributes(video).HasFlag(FileAttributes.ReadOnly)).IsTrue().Because("the check puts the attribute back");
        }
        finally
        {
            File.SetAttributes(video, FileAttributes.Normal);
        }

        await Assert.That(report.Results.Single().Outcome).IsEqualTo("blocked");
        await Assert.That(report.Results.Single().Detail).Contains("nothing in this part was written");
        await Assert.That(target.Read(machine)).IsEqualTo(before);
        await Assert.That(report.UndoDir).IsNull();
    }

    [Test]
    public async Task AReadOnlyTargetAnAclDeniesWriting_StopsThePartBeforeAnyWrite()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("file ACLs are Windows only");
            return;
        }

        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        string machine = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_machine_convars.vcfg");
        target.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.9\"\n\t}\n}\n");
        string before = target.Read(machine);
        using TempDir temp = new();
        source.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.1\"\n\t}\n}\n");
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        options.Only = [Sections.Cs2];
        string video = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        FileInfo info = new(video);
        System.Security.AccessControl.FileSystemAccessRule deny = new(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.AccessControlType.Deny);
        System.Security.AccessControl.FileSecurity acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        File.SetAttributes(video, FileAttributes.ReadOnly);
        RunReport report;
        try
        {
            report = Engine.Restore(temp.Path, options);
        }
        finally
        {
            File.SetAttributes(video, FileAttributes.Normal);
            acl = info.GetAccessControl();
            acl.RemoveAccessRule(deny);
            info.SetAccessControl(acl);
        }

        await Assert.That(report.Results.Single().Outcome).IsEqualTo("blocked");
        await Assert.That(report.Results.Single().Detail).Contains("nothing in this part was written");
        await Assert.That(target.Read(machine)).IsEqualTo(before);
    }

    [Test]
    public async Task ASequenceAnotherFolderCarries_IsTaken_WhateverItsTime()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        string undo = options.UndoRoot!;
        Directory.CreateDirectory(Path.Combine(undo, "000001-20991231-235959Z"));
        await Assert.That(KitBackup.OtherSnapshotWithSequence(undo, "000001", null)).IsEqualTo("000001-20991231-235959Z");
        await Assert.That(KitBackup.OtherSnapshotWithSequence(undo, "000001", "000001-20991231-235959Z")).IsNull();
        await Assert.That(KitBackup.OtherSnapshotWithSequence(undo, "000002", null)).IsNull();
        options.Only = [Sections.Cs2Game];
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo one");
        RunReport report = Engine.Restore(temp.Path, options);
        await Assert.That(Path.GetFileName(report.UndoDir!)[..6]).IsEqualTo("000002");
    }

    [Test]
    public async Task ANvidiaValueTheUndoCouldNotPutBack_IsNeverReplaced()
    {
        await Assert.That(KitBackup.CanRecordNvidia("0x1057EB71", new NvSetting(0x1057EB71, "x", "dword", "3"))).IsTrue();
        await Assert.That(KitBackup.CanRecordNvidia("0x1057EB71", new NvSetting(0x1057EB71, "x", "qword", "3"))).IsFalse();
        await Assert.That(KitBackup.CanRecordNvidia("0x1057EB71", new NvSetting(0x1057EB71, "x", "binary", "00ff"))).IsFalse();
        await Assert.That(KitBackup.CanRecordNvidia("0x1057EB71", new NvSetting(0x1057EB71, null!, "dword", "-1"))).IsFalse();
    }

    [Test]
    public async Task UndoSnapshots_AreNumberedInTheOrderTheyRan_AndNeverShareAFolder()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        options.Only = [Sections.Cs2Game];
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo one");
        RunReport first = Engine.Restore(temp.Path, options);
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo two");
        RunReport second = Engine.Restore(temp.Path, options);
        // The first is undone and filed into applied/, as acb undo does; its number is still taken.
        string applied = Path.Combine(options.UndoRoot!, "applied");
        Directory.CreateDirectory(applied);
        Directory.Move(first.UndoDir!, Path.Combine(applied, Path.GetFileName(first.UndoDir!)));
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo three");
        RunReport third = Engine.Restore(temp.Path, options);

        string[] names = [Path.GetFileName(first.UndoDir!), Path.GetFileName(second.UndoDir!), Path.GetFileName(third.UndoDir!)];
        await Assert.That(names.All(KitBackup.IsUndoSnapshotName)).IsTrue().Because(string.Join(", ", names));
        await Assert.That(names.Select(n => n[..6])).IsEquivalentTo(["000001", "000002", "000003"]).Because("applied snapshots count too");
        await Assert.That(KitBackup.ReadManifest(third.UndoDir!).Get("sequence")).IsEqualTo("000003");
        // A later restore whose clock reads earlier still sorts as newer.
        await Assert.That(KitBackup.NewestFirst(["000002-20991231-235959Z", "000010-20000101-000000Z", "applied", "20261101-014500"]))
            .IsEquivalentTo(["000010-20000101-000000Z", "000002-20991231-235959Z"]);
        await Assert.That(KitBackup.NewestFirst(["000002-20991231-235959Z", "000010-20000101-000000Z"])[0]).IsEqualTo("000010-20000101-000000Z");
    }

    [Test]
    public async Task EverySection_HasOneDeclaredScope_AndTheRulesFollowIt()
    {
        foreach (string section in Sections.All)
        {
            SectionScope scope = Sections.Scope(section);
            await Assert.That(Sections.Label(section)).IsNotEqualTo(section);
            await Assert.That(Sections.IsOwnAccount(section)).IsEqualTo(scope == SectionScope.OwnAccount);
            await Assert.That(Sections.AffectsEveryone(section)).IsEqualTo(scope != SectionScope.OwnAccount);
            await Assert.That(Sections.IsPerUser(section)).IsEqualTo(scope == SectionScope.PerUser);
            await Assert.That(Sections.IsMachineWide(section)).IsEqualTo(scope is SectionScope.PerUser or SectionScope.Machine);
        }

        await Assert.That(Sections.All.Where(Sections.IsOwnAccount)).IsEquivalentTo([Sections.Cs2, Sections.Launch]);
        await Assert.That(() => Sections.Scope("display")).Throws<ArgumentException>();
    }

    [Test]
    public async Task NvidiaEntries_MustBeTheTypeTheDriverDeclares()
    {
        static KvNode Entry(string id, string? type, string? value)
        {
            KvNode node = new(id);
            if (type is not null)
            {
                node.Set("type", type);
            }

            if (value is not null)
            {
                node.Set("value", value);
            }

            return node;
        }

        await Assert.That(KitBackup.NvidiaEntryProblem(Entry("0x1057EB71", "dword", "1"))).IsNull();
        await Assert.That(KitBackup.NvidiaEntryProblem(Entry("0x1057EB71", "binary", new string('a', 10000)))).Contains("declares it dword");
        await Assert.That(KitBackup.NvidiaEntryProblem(Entry("0x1057EB71", "wstring", "x"))).IsNotNull();
        await Assert.That(KitBackup.NvidiaEntryProblem(Entry("0x1057EB71", "dword", "-1"))).Contains("not a number");
        await Assert.That(KitBackup.NvidiaEntryProblem(Entry("0x1057EB71", "dword", "1 2"))).IsNotNull();
        KvNode absent = Entry("0x1057EB71", null, null);
        absent.Set("absent", "1");
        await Assert.That(KitBackup.NvidiaEntryProblem(absent)).IsNull();
        await Assert.That(KitBackup.NvidiaEntryProblem(Entry("0x12345678", "binary", "00"))).IsNull()
            .Because("an id the tool does not carry is refused by the restore, entry by entry");
    }

    [Test]
    public async Task APowerPlanTheUndoRemoves_MustBeACopyItMade()
    {
        const string copy = "0b7c1a7e-1111-4222-8333-944445555666";
        await Assert.That(KitBackup.CreatedPlanProblem(copy, "381b4222-f694-41f0-9685-ff5bb260df2e")).IsNull();
        await Assert.That(KitBackup.CreatedPlanProblem("e9a42b02-d5df-448d-aa00-03f14749eb61", null)).Contains("Windows template");
        await Assert.That(KitBackup.CreatedPlanProblem(copy + " /x", null)).Contains("not a power plan GUID");
        await Assert.That(KitBackup.CreatedPlanProblem(null!, null)).IsNotNull();
        await Assert.That(KitBackup.CreatedPlanProblem(copy, copy)).Contains("active");
        await Assert.That(WindowsSettings.DeletePowerPlan("scheme_current")).Contains("not a plan this tool can remove");
        await Assert.That(WindowsSettings.DeletePowerPlan("381b4222-f694-41f0-9685-ff5bb260df2e")).Contains("not a plan this tool can remove");
    }

    [Test]
    [Arguments(0xA000000Cu, true)] // symbolic link
    [Arguments(0xA0000003u, true)] // junction or mount point
    [Arguments(0xA000001Du, true)] // WSL symbolic link
    [Arguments(0x9000001Au, false)] // OneDrive and other Cloud Files placeholders
    [Arguments(0x9000601Au, false)]
    [Arguments(0x80000013u, false)] // deduplicated file
    [Arguments(0x80000017u, false)] // compressed by Windows
    public async Task OnlyNameSurrogateReparsePoints_CountAsLinks(uint tag, bool link)
    {
        await Assert.That(WindowsSettings.IsNameSurrogateTag(tag)).IsEqualTo(link);
    }

    [Test]
    public async Task APlainFile_IsNotALink()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("reparse tags are read through kernel32");
        }

        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string file = temp.Sub("a.txt");
        await File.WriteAllTextAsync(file, "x");

        await Assert.That(WindowsSettings.IsLink(file)).IsFalse();
        await Assert.That(WindowsSettings.IsLink(temp.Path)).IsFalse();
    }

    /// <summary>
    ///     A driver call that never returns fails its part after the timeout or at a cancel instead of holding the
    ///     save, and while it is still stuck no second call starts. One test, since the worker is shared state.
    /// </summary>
    [Test]
    [NotInParallel("nvidia-driver-worker")]
    public async Task ADriverCallThatHangs_FailsItsPart_AndBlocksASecondCallUntilItReturns()
    {
        using ManualResetEventSlim release = new();
        Exception? error;

        string? why = KitBackup.RunOnDriverWorker(() => release.Wait(), CancellationToken.None, 300, out error);
        await Assert.That(why).IsNotNull();
        await Assert.That(why!).Contains("did not answer");

        bool ran = false;
        why = KitBackup.RunOnDriverWorker(() => ran = true, CancellationToken.None, 300, out error);
        await Assert.That(why).IsNotNull();
        await Assert.That(why!).Contains("has not returned");
        await Assert.That(ran).IsFalse();

        release.Set();
        await Task.Delay(500);
        why = KitBackup.RunOnDriverWorker(() => throw new InvalidOperationException("no driver"), CancellationToken.None, 5000, out error);
        await Assert.That(why).IsNull();
        await Assert.That(error?.Message).IsEqualTo("no driver");

        // One thread at a time is in the driver: a second call waits for a running one, and is not run past its limit.
        using ManualResetEventSlim busy = new();
        using ManualResetEventSlim inside = new();
        Task<string?> first = Task.Run<string?>(() => KitBackup.RunOnDriverWorker(() =>
        {
            inside.Set();
            busy.Wait();
        }, CancellationToken.None, 60000, out _));
        inside.Wait();
        bool overlapped = false;
        why = KitBackup.RunOnDriverWorker(() => overlapped = true, CancellationToken.None, 300, out error);
        await Assert.That(why).IsNotNull();
        await Assert.That(why!).Contains("busy with another call");
        await Assert.That(overlapped).IsFalse();
        busy.Set();
        await Assert.That(await first).IsNull();
        why = KitBackup.RunOnDriverWorker(() => ran = true, CancellationToken.None, 5000, out error);
        await Assert.That(why).IsNull();
        await Assert.That(ran).IsTrue();

        using ManualResetEventSlim second = new();
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(200));
        why = KitBackup.RunOnDriverWorker(() => second.Wait(), cancel.Token, 60000, out error);
        second.Set();
        await Assert.That(why).IsNotNull();
        await Assert.That(why!).Contains("cancelled");
    }

    /// <summary>
    ///     A driver part's worker given up on before its first change never makes one, so that part changed nothing;
    ///     one given up on after it started may have changed the PC, and its record, written before each change,
    ///     covers what it changed.
    /// </summary>
    [Test]
    [NotInParallel("nvidia-driver-worker")]
    public async Task ADriverWorkerGivenUpOn_ChangesNothingLate_UnlessItHadStarted()
    {
        DriverGate early = new();
        await Assert.That(early.GiveUp()).IsFalse();
        await Assert.That(early.Enter).Throws<OperationCanceledException>();

        DriverGate late = new();
        late.Enter();
        await Assert.That(late.GiveUp()).IsTrue();
        late.Enter();

        await Assert.That(KitBackup.DriverTimeoutDetail(false, isUndo: false)).Contains("nothing of this part was changed");
        await Assert.That(KitBackup.DriverTimeoutDetail(true, isUndo: false)).Contains("not known");
        await Assert.That(KitBackup.DriverTimeoutDetail(true, isUndo: false)).Contains("acb undo");
        await Assert.That(KitBackup.DriverTimeoutDetail(true, isUndo: true)).Contains("acb undo again");
    }

    /// <summary>
    ///     Only no NVIDIA GPU or no driver library stays so; any other failure of NVAPI's start may be a driver that
    ///     is updating or recovering, so the part is left for later instead of skipped as done.
    /// </summary>
    [Test]
    [Arguments(0, false)]
    [Arguments(-6, true)]
    [Arguments(-2, true)]
    [Arguments(-1, false)]
    [Arguments(-3, false)]
    [Arguments(-4, false)]
    [Arguments(-104, false)]
    public async Task NvapiStartStatus_IsLastingOnlyForNoGpuOrNoLibrary(int status, bool lasting)
    {
        await Assert.That(NvidiaDrs.IsLastingInitStatus(status)).IsEqualTo(lasting);
        string? problem = NvidiaDrs.InitProblem(status);
        if (status == 0)
        {
            await Assert.That(problem).IsNull();
        }
        else
        {
            await Assert.That(problem!).Contains(lasting ? "no NVIDIA GPU" : "may be updating or recovering");
        }
    }

    /// <summary>
    ///     An undo entry that removes a file asks only for the right to remove it: a folder that refuses new files
    ///     does not block it, while a write there is still blocked. A file that is gone is nothing to do.
    /// </summary>
    [Test]
    public async Task CheckDeletable_AsksOnlyToRemove_NotToAddToTheFolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("on Linux, removing a file needs the same folder right as adding one");
        }

        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string file = temp.Sub("autoexec.cfg");
        await File.WriteAllTextAsync(file, "echo player");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            using (Permissions.NoNewFiles(temp.Path))
            {
                SafeFile.CheckDeletable(file);
                SafeFile.CheckDeletable(temp.Sub("gone.cfg"));
                await Assert.That(() => SafeFile.CheckWritable(file, dryRun: true)).Throws<IOException>();
            }
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task AnUndoRecord_IsWrittenWhole()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string path = temp.Sub("backup.vdf");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        SafeFile.WriteRecord(path, "\"undo\" { }"u8.ToArray());

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("\"undo\" { }");
        await Assert.That(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)).IsTrue();
        await Assert.That(File.Exists(path + ".acb-tmp")).IsFalse();
        File.SetAttributes(path, FileAttributes.Normal);
    }
}
