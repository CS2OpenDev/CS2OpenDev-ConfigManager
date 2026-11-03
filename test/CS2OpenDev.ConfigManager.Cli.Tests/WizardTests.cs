using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     The restore screen's flow, which holds every decision; TuiTests drives the screen itself. Restores go only
///     into fake Steam folders, and only file sections.
/// </summary>
public class WizardTests
{
    private static (TestHost Host, FakeSteam Source, FakeSteam Target, string Zip) Setup(params string[] sections)
    {
        TestHost host = new();
        FakeSteam source = new(launchOptions: "-novid -freq 360");
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup\nexec sd_core");
        FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo this pc");
        string zip = TestArchives.FromSteam(source, host.Temp, sections);
        return (host, source, target, zip);
    }

    [Test]
    public async Task Check_RefusesAFolder_AndOffersNothingFurther()
    {
        using TestHost host = new();
        string folder = host.Temp.Sub("unzipped");
        Directory.CreateDirectory(folder);
        RestoreFlow flow = new(host.Host()) { ArchivePath = folder };

        await Assert.That(flow.Check()).IsFalse();

        await Assert.That(flow.Session).IsNull();
        await Assert.That(string.Join('\n', flow.CheckLines())).Contains("is a folder");
        await Assert.That(string.Join('\n', flow.CheckLines())).Contains("Nothing was changed");
        await Assert.That(flow.Apply()).IsEqualTo(ExitCodes.Refused);
    }

    /// <summary>From Open to Done: what was unticked keeps this PC's copy, and what was applied is on an undo record.</summary>
    [Test]
    public async Task FromOpenToDone_RestoresWhatWasChosen_AndRecordsIt()
    {
        (TestHost host, FakeSteam source, FakeSteam target, string zip) = Setup();
        using (host)
        using (source)
        using (target)
        {
            RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
            await Assert.That(flow.Check()).IsTrue().Because(string.Join('\n', flow.CheckLines()));
            await Assert.That(string.Join('\n', flow.CheckLines())).Contains("Level:    hash");
            await Assert.That(flow.Choosable).IsEquivalentTo([Sections.Cs2, Sections.Cs2Game, Sections.Launch]);

            flow.Choose(Sections.Launch, chosen: false);
            FileChange sdCore = flow.Review().Single(r => r.Section == Sections.Cs2Game).Files.Single(f => f.Relative == "sd_core.cfg");
            flow.ChooseFile(sdCore, chosen: false);
            target.Write(FakeSteam.Cfg("sd_core.cfg"), "this pc's sd_core");
            flow.Refresh();
            await Assert.That(flow.PreflightLines()).Contains("Nothing stands in the way.");

            await Assert.That(flow.Apply()).IsEqualTo(ExitCodes.Done).Because(string.Join('\n', flow.DoneLines()));

            await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).Contains("from the backup");
            await Assert.That(target.Read(FakeSteam.Cfg("sd_core.cfg"))).IsEqualTo("this pc's sd_core");
            await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-novid -high");
            await Assert.That(flow.Results.Single(r => r.Section == Sections.Cs2Game).Detail).Contains("left alone, as chosen: sd_core.cfg");
            await Assert.That(flow.Results.Exists(r => r.Section == Sections.Launch)).IsFalse();
            await Assert.That(host.Records()).HasSingleItem();
            await Assert.That(flow.DoneLines()).Contains("Done. To put this PC back as it was, run: acb undo");
            await Assert.That(host.Run("undo", "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
            await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo this pc");
        }
    }

    /// <summary>Review shows a settings part as a table: each setting as this PC has it, and as the restore would leave it.</summary>
    [Test]
    public async Task Review_ShowsEachSettingsCurrentAndNewValue()
    {
        (TestHost host, FakeSteam source, FakeSteam target, string zip) = Setup(Sections.Launch);
        using (host)
        using (source)
        using (target)
        {
            RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
            await Assert.That(flow.Check()).IsTrue();

            SectionReview launch = flow.Review().Single(r => r.Section == Sections.Launch);
            await Assert.That(launch.Settings).IsEquivalentTo([new SettingChange(Sections.Launch, "launch options", "-novid -high", "-novid -freq 360")]);
            IReadOnlyList<string> table = RestoreFlow.SettingLines(launch);
            await Assert.That(table[0]).StartsWith("Setting");
            await Assert.That(table[1]).Contains("-novid -high").And.Contains("-novid -freq 360");
        }
    }

    /// <summary>Review names each file new, changed or the same here, and shows a changed file's lines against the backup's.</summary>
    [Test]
    public async Task Review_MarksEachFile_AndDiffsAChangedOne()
    {
        (TestHost host, FakeSteam source, FakeSteam target, string zip) = Setup(Sections.Cs2Game);
        using (host)
        using (source)
        using (target)
        {
            target.Delete(FakeSteam.Cfg("practice/nades.cfg"));
            RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
            await Assert.That(flow.Check()).IsTrue();

            IReadOnlyList<FileChange> files = flow.Review().Single().Files;
            FileChange autoexec = files.Single(f => f.Relative == "autoexec.cfg");
            await Assert.That(autoexec.Kind).IsEqualTo(FileChangeKind.Changed);
            await Assert.That(files.Single(f => f.Relative == "sd_core.cfg").Kind).IsEqualTo(FileChangeKind.Same);
            await Assert.That(files.Single(f => f.Relative == "practice/nades.cfg").Kind).IsEqualTo(FileChangeKind.New);

            IReadOnlyList<string> diff = flow.Diff(autoexec);
            await Assert.That(diff).Contains("- echo this pc");
            await Assert.That(diff).Contains("+ echo from the backup");
            await Assert.That(diff).Contains("+ exec sd_core");
        }
    }

    /// <summary>What a policy leaves is never offered, and ticking a section the backup does not hold adds nothing.</summary>
    [Test]
    public async Task Choose_CannotAddWhatThePolicyOrTheBackupLeaves()
    {
        (TestHost host, FakeSteam source, FakeSteam target, string zip) = Setup();
        using (host)
        using (source)
        using (target)
        {
            string policy = host.Temp.Sub("policy.json");
            File.WriteAllText(policy, """{ "sections": { "allow": ["cs2game"] } }""");
            RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root, PolicyPath = policy };
            await Assert.That(flow.Check()).IsTrue().Because(string.Join('\n', flow.CheckLines()));

            await Assert.That(flow.Choosable).IsEquivalentTo([Sections.Cs2Game]);
            flow.Choose(Sections.Launch, chosen: true);
            flow.Choose(Sections.Power, chosen: true);

            await Assert.That(flow.Session!.PlayerSections).IsEquivalentTo([Sections.Cs2Game]);
            await Assert.That(flow.Review().Single(r => r.Section == Sections.Launch).Detail).IsEqualTo(RestoreSession.PolicyLeftDetail);
        }
    }

    /// <summary>Game state integration files are offered as their own choice, unticked, and written only once ticked.</summary>
    [Test]
    public async Task GameStateIntegrationFiles_AreWrittenOnlyWhenTicked()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Delete(FakeSteam.Cfg("gamestate_integration_overlay.cfg"));
        string zip = host.Temp.Sub("gsi.zip");
        CaptureOptions options = new() { SteamRoot = source.Root, Creator = "test", IncludeGameStateIntegration = true };
        options.Include.Clear();
        options.Include.Add(Sections.Cs2Game);
        BackupBuilder.Create(options, zip, new Archive.ArchiveTool("Advanced Config Backup", "test"), DateTimeOffset.UtcNow, CancellationToken.None);
        RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
        await Assert.That(flow.Check()).IsTrue();

        await Assert.That(flow.HoldsGsi).IsTrue();
        await Assert.That(flow.Gsi).IsFalse();
        await Assert.That(flow.Review().Single().Detail).Contains("restored only with --gsi");
        flow.ChooseGsi(true);
        await Assert.That(flow.Apply()).IsEqualTo(ExitCodes.Done);

        await Assert.That(target.Exists(FakeSteam.Cfg("gamestate_integration_overlay.cfg"))).IsTrue();
    }

    [Test]
    public async Task Acb_WithNoCommand_RunsTheWizardOnlyAtATerminal()
    {
        using TestHost host = new() { Wizard = _ => 42 };

        await Assert.That(host.Run()).IsEqualTo(ExitCodes.Usage).Because("there is no terminal to draw on");
        await Assert.That(host.Errors).Contains("need a terminal");

        host.Interactive = true;
        await Assert.That(host.Run()).IsEqualTo(42);
    }

    [Test]
    public async Task Wrap_KeepsIndent_AndBreaksAtSpaces()
    {
        string[] lines = [.. Ui.Wrap(["  one two three four five"], 12)];

        await Assert.That(lines).IsEquivalentTo(["  one two", "    three", "    four", "    five"]);
    }
}
