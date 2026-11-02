using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The one type every restore goes through. Its rules hold whoever calls it: a step that picks sections can only
///     take some away, and nothing applies a section the rules left out.
/// </summary>
public class RestoreSessionTests
{
    private static readonly DateTimeOffset Created = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static string Backup(FakeSteam source, TempDir temp, params string[] sections)
    {
        string zip = temp.Sub("backup.zip");
        CaptureOptions options = new() { SteamRoot = source.Root, Creator = "test" };
        options.Include.Clear();
        options.Include.AddRange(sections);
        BackupBuilder.Create(options, zip, ArchiveV2Tests.Tool, Created, CancellationToken.None);
        return zip;
    }

    private static RestoreEnvironment Environment(bool asAdministrator = false, Func<string, bool>? running = null) => new()
    {
        Platform = ArchiveFormat.CurrentPlatform,
        IsRunning = running ?? (_ => false),
        LocateSteam = SteamInstall.Locate,
        AsAdministrator = asAdministrator
    };

    [Test]
    public async Task Choose_OnlyNarrows_AndApplyRefusesASectionTheRulesLeftOut()
    {
        using TempDir temp = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo mine");
        using FakeSteam target = new();
        string zip = Backup(source, temp, Sections.Cs2Game, Sections.Launch);
        RestoreSession session = RestoreSession.Open(new RestoreRequest(zip, null, null, target.Root, null, false), Environment());
        await Assert.That(session.PlayerSections).IsEquivalentTo([Sections.Cs2Game, Sections.Launch]);

        session.Choose([Sections.Cs2Game, Sections.Power]);

        await Assert.That(session.PlayerSections).IsEquivalentTo([Sections.Cs2Game]).Because("a pick can take sections away, never add one");
        UndoStore store = new(temp.Sub("store"), isMachine: false);
        store.EnsureUserStore();
        await Assert.That(() => session.Apply([Sections.Launch], store)).Throws<InvalidOperationException>();
        await Assert.That(store.Records()).IsEmpty();
    }

    [Test]
    public async Task AsAdministrator_ThePartsWrittenThroughTheSteamFolder_AreLeft()
    {
        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = Backup(source, temp, Sections.Cs2, Sections.Launch);

        RestoreSession session = RestoreSession.Open(new RestoreRequest(zip, null, null, target.Root, null, false), Environment(asAdministrator: true));

        await Assert.That(session.PlayerSections).IsEmpty();
        await Assert.That(session.SteamLeft).IsEquivalentTo([Sections.Cs2, Sections.Launch]);
    }

    [Test]
    public async Task APolicyThatNeedsCs2Closed_RefusesWhileItRuns_NamingTheArchive()
    {
        using TempDir temp = new();
        using FakeSteam source = new();
        string zip = Backup(source, temp, Sections.Cs2);
        string policy = temp.Sub("policy.json");
        File.WriteAllText(policy, """{ "requireCs2Closed": true }""");

        RestoreRefusedException refused = (await Assert.That(() => RestoreSession.Open(new RestoreRequest(zip, null, null, source.Root, policy, false),
            Environment(running: name => name == "cs2"))).Throws<RestoreRefusedException>())!;

        await Assert.That(refused.Message).Contains("only while CS2 is closed");
        await Assert.That(refused.Archive).IsNotNull().Because("the archive was verified, so its header can be shown with the refusal");
    }

    /// <summary>
    ///     A file the person leaves keeps the target's contents, a .vcfg's twin with it, and the record holds only what
    ///     was written; the review says which files are new, changed or already the same.
    /// </summary>
    [Test]
    public async Task ALeftFile_AndItsTwin_KeepTheTargets_AndTheReviewSaysWhatChanges()
    {
        using TempDir temp = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"), "\"config\" { \"source\" \"1\" }");
        source.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded"), "\"config\" { \"source\" \"1\" }");
        source.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg"), "\"config\" { \"keys\" \"source\" }");
        using FakeSteam target = new();
        string zip = Backup(source, temp, Sections.Cs2);
        RestoreSession session = RestoreSession.Open(new RestoreRequest(zip, null, null, target.Root, null, false), Environment());
        string convarsBefore = target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"));
        string twinBefore = target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded"));

        session.PreviewChanges();
        IReadOnlyList<FileChange> files = session.Files();
        await Assert.That(files.Single(f => f.Relative == "local/cfg/cs2_user_convars_0_slot0.vcfg").Kind).IsEqualTo(FileChangeKind.Changed);
        await Assert.That(files.Single(f => f.Relative == "local/cfg/cs2_machine_convars.vcfg").Kind).IsEqualTo(FileChangeKind.Same);
        await Assert.That(files.Count(f => f.Relative.Contains("cs2_user_convars_0_slot0", StringComparison.Ordinal))).IsEqualTo(1).Because("a twin is listed with its .vcfg");

        session.LeaveFile(Sections.Cs2, "local/cfg/cs2_user_convars_0_slot0.vcfg", leave: true);
        UndoStore store = new(temp.Sub("store"), isMachine: false);
        store.EnsureUserStore();
        RunReport report = session.Apply([Sections.Cs2], store);

        await Assert.That(report.Results.Single().Detail).Contains("left alone, as chosen: local/cfg/cs2_user_convars_0_slot0.vcfg, local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded");
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"))).IsEqualTo(convarsBefore);
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded"))).IsEqualTo(twinBefore);
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg"))).Contains("source");
        string record = File.ReadAllText(Path.Combine(store.Records().Single().Dir, ConfigBackup.ManifestName));
        await Assert.That(record).DoesNotContain("cs2_user_convars_0_slot0");
    }

    /// <summary>
    ///     A restore's record keeps what each file held once written, for the undo review to tell a file changed since;
    ///     an undo reads only the record's files, so it puts the PC back all the same.
    /// </summary>
    [Test]
    public async Task TheRecord_KeepsWhatTheRestoreWrote_AndUndoIgnoresIt()
    {
        using TempDir temp = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo mine");
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo theirs");
        string zip = Backup(source, temp, Sections.Cs2Game);
        RestoreSession session = RestoreSession.Open(new RestoreRequest(zip, null, null, target.Root, null, false), Environment());
        UndoStore store = new(temp.Sub("store"), isMachine: false);
        store.EnsureUserStore();

        session.Apply([Sections.Cs2Game], store);

        UndoRecord record = store.Records().Single();
        KvNode wrote = record.Manifest!.Child(Sections.Cs2Game)!.Child(ConfigBackup.WroteKey)!;
        await Assert.That(wrote.Get("autoexec.cfg")).IsEqualTo(ConfigBackup.Sha256(target.PathOf(FakeSteam.Cfg("autoexec.cfg"))));
        await Assert.That(wrote.Get("sd_core.cfg")).IsEqualTo(ConfigBackup.Sha256(target.PathOf(FakeSteam.Cfg("sd_core.cfg"))));

        RunReport undo = Engine.Undo(record.Dir, new RestoreOptions { SteamRoot = target.Root, SkipProcessChecks = true });
        await Assert.That(undo.Results.Single().Outcome).IsEqualTo(Outcomes.Done).Because(undo.Results.Single().Detail);
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo theirs");
    }

    [Test]
    public async Task ThePreview_ChangesNothing_AndItsFingerprintFollowsTheTarget()
    {
        using TempDir temp = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo mine");
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo theirs");
        string zip = Backup(source, temp, Sections.Cs2Game);
        RestoreRequest request = new(zip, null, null, target.Root, null, false);

        RestoreSession first = RestoreSession.Open(request, Environment());
        first.PreviewChanges();
        PlanPins pins = new(null, [], first.Fingerprint!);

        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo theirs");
        RestoreSession same = RestoreSession.Open(request, Environment());
        same.PreviewChanges();
        await Assert.That(same.MatchesPlan(pins)).IsTrue();

        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo changed since");
        RestoreSession changed = RestoreSession.Open(request, Environment());
        changed.PreviewChanges();
        await Assert.That(changed.MatchesPlan(pins)).IsFalse();
    }
}
