using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The restorer's undo store and the undo runner over it: newest first, the hold-back rule, unreadable records
///     and the way out of them, partial undos, a claim that fails, and the administrator's store ACL. Restores go
///     into fake Steam folders, over the file sections only.
/// </summary>
public class UndoStoreTests
{
    private const string Autoexec = "autoexec.cfg";

    /// <summary>Two restores of the same part, A then B, over a target whose autoexec.cfg was "echo original".</summary>
    private static (FakeSteam Target, UndoStore Store) TwoRestores(TempDir temp)
    {
        FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        foreach (string tag in new[] { "A", "B" })
        {
            using FakeSteam source = new();
            source.Write(FakeSteam.Cfg(Autoexec), "echo " + tag);
            string capture = temp.Sub("capture-" + tag);
            CaptureRestoreTests.Capture(source, capture);
            RestoreOptions options = CaptureRestoreTests.Options(target);
            options.Only = [Sections.Cs2Game];
            Engine.Restore(capture, options);
        }

        return (target, new UndoStore(CaptureRestoreTests.Options(target).UndoRoot!, isMachine: false));
    }

    [Test]
    public async Task Undo_AppliesNewestFirst_AndAnOlderPartWaitsWhileANewerOneStillHoldsIt()
    {
        using TempDir temp = new();
        (FakeSteam target, UndoStore store) = TwoRestores(temp);
        using (target)
        {
            await Assert.That(store.Records().Select(r => r.Sequence)).IsEquivalentTo([2, 1]).Because("newest first");

            // CS2 runs: the newer record's part is left for later, so the older one must not go first.
            IReadOnlyList<RecordOutcome> blocked = UndoRunner.Run(store, new UndoRunOptions { SteamRoot = target.Root, ProcessCheck = n => n == "cs2" }, null);

            await Assert.That(blocked[0].Left).Contains(Sections.Cs2Game);
            await Assert.That(blocked[1].Waiting).Contains(Sections.Cs2Game);
            await Assert.That(blocked[1].Report!.Results).IsEmpty().Because("the older record's part was not even tried");
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo B");

            IReadOnlyList<RecordOutcome> done = UndoRunner.Run(store, new UndoRunOptions { SteamRoot = target.Root, ProcessCheck = _ => false }, null);

            await Assert.That(done.All(o => o.Filed)).IsTrue();
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original").Because("B's record put A back, then A's put the original back");
            await Assert.That(store.Records()).IsEmpty();
            await Assert.That(Directory.GetDirectories(Path.Combine(store.Root, UndoStore.AppliedFolder)).Length).IsEqualTo(2);
        }
    }

    /// <summary>
    ///     v1 left a record that could not be read in place forever, holding back every older one with no way out.
    ///     It still holds them back, since what it recorded is unknown, until the person sets it aside.
    /// </summary>
    [Test]
    public async Task AnUnreadableRecord_HoldsBackOlderOnes_UntilItIsSetAside()
    {
        using TempDir temp = new();
        (FakeSteam target, UndoStore store) = TwoRestores(temp);
        using (target)
        {
            UndoRecord newest = store.Records()[0];
            await File.WriteAllTextAsync(Path.Combine(newest.Dir, ConfigBackup.ManifestName), "{{{ not a record");
            UndoRunOptions headless = new() { SteamRoot = target.Root, ProcessCheck = _ => false };

            IReadOnlyList<RecordOutcome> left = UndoRunner.Run(store, headless, null);

            await Assert.That(left[0].Unreadable).IsNotNull();
            await Assert.That(left[1].Waiting).Contains(Sections.Cs2Game);
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo B");

            IReadOnlyList<RecordOutcome> setAside = UndoRunner.Run(store, headless with { OnUnreadable = _ => UnreadableChoice.SetAside }, null);

            await Assert.That(setAside[0].SetAside).IsTrue();
            await Assert.That(Directory.Exists(Path.Combine(store.Root, UndoStore.SetAsideFolder, newest.Name))).IsTrue();
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original")
                .Because("the older record went ahead once the unreadable one was out of the way");
        }
    }

    /// <summary>
    ///     A claim makes backup.vdf before anything else, so only an empty record folder changed nothing. One that
    ///     holds copies but lost its backup.vdf is unreadable: it holds back older records and is never filed.
    /// </summary>
    [Test]
    public async Task ARecordFolderWithoutItsRecord_IsUnreadable_UnlessItIsEmpty()
    {
        using TempDir temp = new();
        string root = temp.Sub("undo");
        string lost = Path.Combine(root, "000002-20261101-180000Z");
        string empty = Path.Combine(root, "000001-20261101-170000Z");
        Directory.CreateDirectory(Path.Combine(lost, "files", Sections.Cs2Game));
        await File.WriteAllTextAsync(Path.Combine(lost, "files", Sections.Cs2Game, Autoexec), "echo original");
        Directory.CreateDirectory(empty);
        UndoStore store = new(root, isMachine: false);

        IReadOnlyList<UndoRecord> records = store.Records();

        await Assert.That(records[0].IsReadable).IsFalse();
        await Assert.That(records[0].Problem!).Contains(ConfigBackup.ManifestName + " is missing");
        await Assert.That(records[1].IsReadable).IsTrue();
        await Assert.That(records[1].Manifest).IsNull();

        IReadOnlyList<RecordOutcome> outcomes = UndoRunner.Run(store, new UndoRunOptions(), null);

        await Assert.That(outcomes[0].Unreadable).IsNotNull();
        await Assert.That(Directory.Exists(lost)).IsTrue().Because("a lost record is never filed as if it changed nothing");
        await Assert.That(outcomes[1].Filed).IsTrue();
    }

    /// <summary>A store that may be there but cannot be reached is never taken for an empty one.</summary>
    [Test]
    public async Task AStoreThatCannotBeReached_IsNotTakenForAnEmptyOne()
    {
        using TempDir temp = new();
        string locked = temp.Sub("locked");
        Directory.CreateDirectory(locked);

        await Assert.That(new UndoStore(temp.Sub("not-made-yet"), isMachine: false).Records()).IsEmpty();

        UndoStore store = new(Path.Combine(locked, "CS2OpenDev", "undo"), isMachine: false);
        using (Permissions.Unlistable(locked))
        {
            await Assert.That(() => store.Records()).Throws<UndoStoreUnreachableException>();
        }
    }

    [Test]
    public async Task AnUnreadableRecord_ReadAgain_IsAppliedWhenItCanBeReadNow()
    {
        using TempDir temp = new();
        (FakeSteam target, UndoStore store) = TwoRestores(temp);
        using (target)
        {
            UndoRecord newest = store.Records()[0];
            string path = Path.Combine(newest.Dir, ConfigBackup.ManifestName);
            byte[] good = await File.ReadAllBytesAsync(path);
            await File.WriteAllTextAsync(path, "{{{");
            int asked = 0;

            UndoRunner.Run(store, new UndoRunOptions
            {
                SteamRoot = target.Root,
                ProcessCheck = _ => false,
                OnUnreadable = _ =>
                {
                    asked++;
                    File.WriteAllBytes(path, good);
                    return UnreadableChoice.Retry;
                }
            }, null);

            await Assert.That(asked).IsEqualTo(1);
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
        }
    }

    /// <summary>v1 moved a whole snapshot into applied after a partial undo; only a record with nothing left is filed.</summary>
    [Test]
    public async Task APartialUndo_KeepsTheRecordWithWhatIsLeft_AndDoesNotFileIt()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        Engine.Restore(temp.Path, options);
        UndoStore store = new(options.UndoRoot!, isMachine: false);

        IReadOnlyList<RecordOutcome> outcome = UndoRunner.Run(store, new UndoRunOptions { Only = [Sections.Cs2Game], SteamRoot = target.Root, ProcessCheck = _ => false }, null);

        await Assert.That(outcome.Single().Filed).IsFalse();
        await Assert.That(outcome.Single().Left).IsEquivalentTo([Sections.Cs2, Sections.Launch]);
        await Assert.That(store.Records().Single().Parts).IsEquivalentTo([Sections.Cs2, Sections.Launch]);
        await Assert.That(Directory.Exists(Path.Combine(store.Root, UndoStore.AppliedFolder))).IsFalse();
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
    }

    [Test]
    public async Task Records_AreFoundByNameOrSequence_AndANumberIsNeverReusedAfterSetAside()
    {
        using TempDir temp = new();
        (FakeSteam target, UndoStore store) = TwoRestores(temp);
        using (target)
        {
            UndoRecord newest = store.Records()[0];
            await Assert.That(store.Find("2")?.Name).IsEqualTo(newest.Name);
            await Assert.That(store.Find(newest.Name)?.Sequence).IsEqualTo(2);
            await Assert.That(store.Find("9")).IsNull();

            store.SetAside(newest);
            using FakeSteam source = new();
            source.Write(FakeSteam.Cfg(Autoexec), "echo C");
            CaptureRestoreTests.Capture(source, temp.Sub("capture-C"));
            RestoreOptions options = CaptureRestoreTests.Options(target);
            options.Only = [Sections.Cs2Game];
            RunReport third = Engine.Restore(temp.Sub("capture-C"), options);

            await Assert.That(Path.GetFileName(third.UndoDir!)[..6]).IsEqualTo("000003").Because("set-aside records keep their numbers");
        }
    }

    /// <summary>
    ///     v1 retried a claim that failed on a full disk a hundred times and left a hundred half-made folders. A claim
    ///     that cannot be written is removed and the restore stops before its first change.
    /// </summary>
    [Test]
    public async Task AClaimThatCannotBeWritten_LeavesNoFolder_AndChangesNothing()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        string root = options.UndoRoot!;
        using IDisposable refusing = Permissions.RefuseRecords(root);

        RunReport report = Engine.Restore(temp.Path, options);

        await Assert.That(report.ClaimProblem).IsNotNull().Because(string.Join("; ", report.Results));
        await Assert.That(report.Results.Count).IsEqualTo(1).Because("the run stops at the first part");
        await Assert.That(Directory.GetDirectories(root)).IsEmpty().Because("the half-made claim was removed");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
    }

    [Test]
    [Arguments("O:BAG:SYD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;;0x100081;;;BU)", null)]
    [Arguments("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", null)]
    [Arguments("O:S-1-5-21-1-2-3-1001G:SYD:P(A;OICI;FA;;;BA)", "owned by")]
    [Arguments("O:BAG:SYD:P(A;OICI;FA;;;BA)(A;OICI;0x2;;;BU)", "change it")]
    [Arguments("O:BAG:SYD:(A;OICI;0x1301bf;;;AU)(A;OICI;FA;;;BA)", "change it")]
    [Arguments("O:BAG:SYD:P(A;OICIIO;FA;;;CO)(A;OICI;FA;;;BA)", "change it")]
    [Arguments("O:BAG:SYD:P(D;OICI;FA;;;BU)(A;OICI;FA;;;BA)", null)]
    [Arguments("O:BAG:SY", "no access list")]
    public async Task TheAdministratorsStore_IsUsedOnlyWhenNobodyElseCanChangeIt(string sddl, string? problem)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("the administrator's store is Windows only");
            return;
        }

        string? found = StoreSecurity.DescriptorProblem(new RawSecurityDescriptor(sddl));

        if (problem is null)
        {
            await Assert.That(found).IsNull();
        }
        else
        {
            await Assert.That(found).Contains(problem);
        }
    }

    /// <summary>
    ///     acb is started as an administrator only from where nobody else can add a DLL beside it or swap it: Windows
    ///     loads a program's DLLs from its own folder first. Entries that only pass to new children do not count, and
    ///     the person who would approve the prompt may be trusted with their own folder.
    /// </summary>
    [Test]
    [Arguments("O:BAG:SYD:(A;;FA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;FA;;;CO)", null, false)]
    [Arguments("O:BAG:SYD:(A;;FA;;;BA)(A;OICIIO;0x1301bf;;;AU)", null, false)]
    [Arguments("O:BAG:SYD:(A;;FA;;;BA)(A;;0x1301bf;;;AU)", "change it", false)]
    [Arguments("O:S-1-5-21-1-2-3-1001G:SYD:(A;;FA;;;S-1-5-21-1-2-3-1001)", "owned by", false)]
    [Arguments("O:S-1-5-21-1-2-3-1001G:SYD:(A;;FA;;;S-1-5-21-1-2-3-1001)", null, true)]
    public async Task AProgramFolder_IsTrustedForElevationOnlyWhenNobodyElseCanChangeIt(string sddl, string? problem, bool ownerApproves)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("elevation is Windows only");
            return;
        }

        SecurityIdentifier? approver = ownerApproves ? new SecurityIdentifier("S-1-5-21-1-2-3-1001") : null;
        string? found = StoreSecurity.DescriptorProblem(new RawSecurityDescriptor(sddl), 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000, approver);

        if (problem is null)
        {
            await Assert.That(found).IsNull();
        }
        else
        {
            await Assert.That(found).Contains(problem);
        }
    }

    /// <summary>Windows' own programs pass the check; a copy in a folder this user can write does not.</summary>
    [Test]
    public async Task AProgramInAUsersFolder_IsNotStartedAsAnAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("elevation is Windows only");
            return;
        }

        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string copy = temp.Sub("acb.exe");
        await File.WriteAllBytesAsync(copy, [0x4d, 0x5a]);

        await Assert.That(StoreSecurity.ProgramProblem(Path.Combine(Environment.SystemDirectory, "cmd.exe"), null)).IsNull();
        await Assert.That(StoreSecurity.ProgramProblem(copy, null)).IsNotNull();
    }

    /// <summary>
    ///     %ProgramData% lets any user make a folder. One a player made where the store goes is refused, rather than
    ///     written to by the administrator's run (v1's elevated run trusted a folder the player owned).
    /// </summary>
    [Test]
    public async Task AStoreFolderAPlayerMadeFirst_IsRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("the administrator's store is Windows only");
            return;
        }

        using TempDir temp = new();
        string parent = temp.Sub("ProgramData");
        Directory.CreateDirectory(Path.Combine(parent, "CS2OpenDev"));

        await Assert.That(() => Ensure(Path.Combine(parent, "CS2OpenDev", "ConfigBackup", "undo"), parent))
            .Throws<UnauthorizedAccessException>().WithMessageContaining("CS2OpenDev");
        await Assert.That(Directory.Exists(Path.Combine(parent, "CS2OpenDev", "ConfigBackup"))).IsFalse()
            .Because("nothing is made under a folder that failed the check");
    }

    private static void Ensure(string root, string parent)
    {
        if (OperatingSystem.IsWindows())
        {
            StoreSecurity.EnsureMachineStore(root, parent);
        }
    }

    [Test]
    public async Task APolicy_OnlyNarrows_AndRefusesWhatItCannotRead()
    {
        OrganiserPolicy policy = OrganiserPolicy.Parse(Encoding.UTF8.GetBytes("""
            {
              "minimumLevel": "seal",
              "sections": { "allow": ["cs2", "launch"] },
              "nvidia": { "deny": ["0x10835002"] },
              "requireCs2Closed": true
            }
            """));

        await Assert.That(policy.MinimumLevel).IsEqualTo(CS2OpenDev.ConfigManager.Archive.TrustLevel.Seal);
        await Assert.That(policy.Allows(Sections.Cs2)).IsTrue();
        await Assert.That(policy.Allows(Sections.Cs2Game)).IsFalse();
        await Assert.That(policy.NvidiaDeny).Contains(0x10835002u);
        await Assert.That(policy.RequireCs2Closed).IsTrue();
        await Assert.That(OrganiserPolicy.Parse("{}"u8.ToArray()).MinimumLevel).IsEqualTo(CS2OpenDev.ConfigManager.Archive.TrustLevel.Hash)
            .Because("an empty policy narrows nothing and widens nothing");
    }

    [Test]
    [Arguments("""{ "sections": { "allow": ["cs2"], "add": ["remote"] } }""", "")]
    [Arguments("""{ "allowRemote": true }""", "")]
    [Arguments("""{ "MinimumLevel": "hash" }""", "")]
    [Arguments("""{ "minimumLevel": "none" }""", "not hash, seal or identity")]
    [Arguments("""{ "sections": { "allow": ["cs2gmae"] } }""", "not a section")]
    [Arguments("""{ "nvidia": { "deny": ["vsync"] } }""", "not a setting id")]
    [Arguments("""{ "steamIds": ["69413322"] }""", "not a SteamID64")]
    [Arguments("""{ "minimumLevel": "hash", }""", "")]
    [Arguments("// a comment\n{ }", "")]
    public async Task APolicyThisVersionCannotReadWhole_IsRefused(string json, string message)
    {
        await Assert.That(() => OrganiserPolicy.Parse(Encoding.UTF8.GetBytes(json))).Throws<PolicyException>().WithMessageContaining(message);
    }

    /// <summary>
    ///     steamIds is checked against the signer the reader verified, not only the level: an identity-level backup
    ///     signed by an account the policy does not name, or with no checked signer, is refused.
    /// </summary>
    [Test]
    [Arguments("76561198000000002")]
    [Arguments("")]
    public async Task ASteamIdsPolicy_RefusesAnIdentityBackupFromASignerItDoesNotName(string signer)
    {
        OrganiserPolicy policy = OrganiserPolicy.Parse("""{ "minimumLevel": "identity", "steamIds": ["76561198000000001"] }"""u8.ToArray());

        await Assert.That(policy.ArchiveProblem(Signed(signer.Length == 0 ? null : signer))).IsNotNull();
        await Assert.That(policy.ArchiveProblem(Signed("76561198000000001"))).IsNull();
    }

    /// <summary>Windows' own tools save a policy with a byte order mark, or as UTF-16; its digest stays over the file's own bytes.</summary>
    [Test]
    public async Task APolicySavedWithAByteOrderMarkOrAsUtf16_IsRead()
    {
        const string json = """{ "sections": { "allow": ["cs2"] } }""";
        byte[][] saved =
        [
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)],
            [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(json)],
            [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(json)]
        ];

        foreach (byte[] bytes in saved)
        {
            OrganiserPolicy policy = OrganiserPolicy.Parse(bytes);
            await Assert.That(policy.Allows(Sections.Cs2)).IsTrue();
            await Assert.That(policy.Allows(Sections.Cs2Game)).IsFalse();
            await Assert.That(policy.Sha256).IsEqualTo(CS2OpenDev.ConfigManager.Archive.Dsse.Sha256Hex(bytes));
        }
    }

    private static CS2OpenDev.ConfigManager.Archive.VerifiedArchive Signed(string? signer) => new(
        new CS2OpenDev.ConfigManager.Archive.ArchiveManifest(CS2OpenDev.ConfigManager.Archive.ArchiveFormat.Format,
            CS2OpenDev.ConfigManager.Archive.Platforms.WindowsX64, DateTimeOffset.UnixEpoch,
            new CS2OpenDev.ConfigManager.Archive.ArchiveTool("test", "1"), null, [], []),
        CS2OpenDev.ConfigManager.Archive.TrustLevel.Identity, "00", [], signer);
}
