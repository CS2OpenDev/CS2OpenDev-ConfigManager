using System.Security.AccessControl;
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
    ///     v1's NVIDIA rule, which no test can run without writing a real driver: every write in the
    ///     restore goes to the profile FindCs2Profile returned, never to the global profile, which is only read.
    /// </summary>
    [Test]
    public async Task TheNvidiaRestore_WritesOnlyTheCs2Profile()
    {
        string source = await File.ReadAllTextAsync(Path.Combine(CS2OpenDev.ConfigManager.Testing.Repository.Root,
            "src", "CS2OpenDev.ConfigManager.Core", "Kit", "ConfigBackup.cs"));
        int start = source.IndexOf("case Sections.Nvidia:", source.IndexOf("private static void RestoreSection(", StringComparison.Ordinal), StringComparison.Ordinal);
        string restore = source[start..source.IndexOf("case Sections.Vibrance:", start, StringComparison.Ordinal)];

        await Assert.That(restore).Contains("IntPtr profile = drs.FindCs2Profile();");
        await Assert.That(System.Text.RegularExpressions.Regex.Count(restore, @"\bprofile = ")).IsEqualTo(1)
            .Because("the profile written is never reassigned");
        foreach (System.Text.RegularExpressions.Match write in System.Text.RegularExpressions.Regex.Matches(restore, @"drs\.(SetSetting|ResetSetting)\(([^,]+),"))
        {
            await Assert.That(write.Groups[2].Value).IsEqualTo("profile").Because(write.Value);
        }

        await Assert.That(restore).DoesNotContain("SetSetting(drs.GlobalProfile()");
        await Assert.That(NvidiaDrs.IsCarriedSetting(0xDEADBEEF)).IsFalse();
    }
}
