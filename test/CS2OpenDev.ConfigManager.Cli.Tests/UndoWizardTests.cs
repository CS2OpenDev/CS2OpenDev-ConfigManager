using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     The undo wizard: its review shows what changed since each restore, its choices narrow <c>acb undo</c>'s own
///     run, and a record that cannot be read can be set aside from it. Fake Steam folders only.
/// </summary>
public class UndoWizardTests
{
    /// <summary>
    ///     Review tells a file as the restore left it from one changed since (by CS2, or the next person), and a file
    ///     the restore added, and shows what undo would put back in a changed one.
    /// </summary>
    [Test]
    public async Task Review_SaysWhatChangedSinceTheRestore_AndWhatUndoPutsBack()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup");
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo before the restore");
        target.Delete(FakeSteam.Cfg("practice/nades.cfg"));
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo the next person's change");

        UndoFlow flow = new(host.Host());
        flow.Load();
        await Assert.That(flow.RecordLines().Single()).Contains(Sections.Label(Sections.Cs2Game));
        RecordReview review = flow.Review().Single();

        UndoFile autoexec = review.Files.Single(f => f.Relative == "autoexec.cfg");
        await Assert.That(autoexec.State).IsEqualTo(UndoFileState.ChangedSince);
        await Assert.That(review.Files.Single(f => f.Relative == "practice/nades.cfg").State).IsEqualTo(UndoFileState.Added);
        await Assert.That(review.Files.Single(f => f.Relative == "sd_core.cfg").State).IsEqualTo(UndoFileState.AlreadyBack)
            .Because("the backup's sd_core.cfg matched this PC's, so the record already holds what is there");
        IReadOnlyList<string> diff = UndoFlow.Diff(autoexec);
        await Assert.That(diff[0]).Contains("Changed since the restore");
        await Assert.That(diff).Contains("- echo the next person's change");
        await Assert.That(diff).Contains("+ echo before the restore");
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo the next person's change").Because("the review changes nothing");
    }

    /// <summary>An unticked part stays in its record; the rest is put back by acb undo's own run.</summary>
    [Test]
    public async Task Apply_PutsBackOnlyWhatWasChosen()
    {
        using TestHost host = new();
        using FakeSteam source = new(launchOptions: "-novid -freq 360");
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup");
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo before the restore");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game, Sections.Launch);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        UndoFlow flow = new(host.Host());
        flow.Load();
        await Assert.That(flow.SectionsHeld).IsEquivalentTo([Sections.Cs2Game, Sections.Launch]);
        flow.ChooseSection(Sections.Launch, chosen: false);
        await Assert.That(flow.PreflightLines()).Contains("Nothing stands in the way.");

        flow.Apply();

        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo before the restore");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-novid -freq 360");
        await Assert.That(host.Records().Single().Parts).IsEquivalentTo([Sections.Launch]);
        await Assert.That(string.Join('\n', flow.DoneLines())).Contains(Sections.Label(Sections.Cs2Game));
    }

    /// <summary>A record that cannot be read holds back older ones; the wizard says so and can set it aside.</summary>
    [Test]
    public async Task AnUnreadableRecord_IsShown_AndCanBeSetAside()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        UndoRecord record = host.Records().Single();
        File.WriteAllText(Path.Combine(record.Dir, ConfigBackup.ManifestName), "\"ConfigBackup\" {");

        UndoFlow flow = new(host.Host());
        flow.Load();
        await Assert.That(flow.RecordLines().Single()).Contains("cannot be read");
        await Assert.That(string.Join('\n', flow.PreflightLines())).Contains("Set it aside in Records");

        string said = flow.SetAside(flow.Records.Single());

        await Assert.That(said).Contains("is set aside");
        await Assert.That(flow.Records).IsEmpty();
        await Assert.That(Directory.Exists(Path.Combine(host.UserStore, UndoStore.SetAsideFolder, record.Name))).IsTrue();
    }

    /// <summary>The undo wizard on screen, through the ANSI driver: each step shows its stage, and Put back undoes the restore.</summary>
    [Test]
    [NotInParallel("terminal")]
    public async Task TheScreen_WalksEachStep_AndPutsThePcBack()
    {
        // See WizardTests: on Linux the screen needs a terminal, which CI gives the suite with script(1).
        if (!OperatingSystem.IsWindows() && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            Skip.Test("on Linux the screen needs a terminal; run the suite under script(1)");
            return;
        }

        using TestHost host = new();
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup");
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo before the restore");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        UndoFlow flow = new(host.Host());
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(120, 40);
        using UndoWizard wizard = new(flow);
        SessionToken? token = app.Begin(wizard.Wizard);
        try
        {
            string Screen()
            {
                app.LayoutAndDraw(true);
                return app.Driver!.ToString() ?? "";
            }

            await Assert.That(Screen()).Contains("Set aside the selected record");
            await Assert.That(wizard.Wizard.GoNext()).IsTrue();
            await Assert.That(Screen()).Contains("as restored   autoexec.cfg");
            await Assert.That(wizard.Wizard.GoNext()).IsTrue();
            await Assert.That(Screen()).Contains("Part: " + Sections.Label(Sections.Cs2Game));
            await Assert.That(wizard.Wizard.GoNext()).IsTrue();
            await Assert.That(Screen()).Contains("Nothing stands in the way.");
            await Assert.That(wizard.Wizard.GoNext()).IsTrue();
            await Assert.That(Screen()).Contains("Pressing Put back");
            await Assert.That(wizard.Wizard.GoNext()).IsTrue();
            await Assert.That(Screen()).Contains("Done: the PC is back as it was");
        }
        finally
        {
            if (token is not null)
            {
                app.End(token);
            }
        }

        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo before the restore");
        await Assert.That(flow.ExitCode).IsEqualTo(ExitCodes.Done);
    }
}
