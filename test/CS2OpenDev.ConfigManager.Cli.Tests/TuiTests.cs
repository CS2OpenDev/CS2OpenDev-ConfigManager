using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Tests;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     The screens through Terminal.Gui's ANSI driver: home, and backing up, restoring and putting the PC back on the
///     task screen, moved through as a person would, with injected keys. Fake Steam folders and temp stores only;
///     dialogs are stand-ins.
/// </summary>
public class TuiTests
{
    private const string Passphrase = "correct horse battery staple";

    [Test]
    [NotInParallel("terminal")]
    public async Task Home_SaysWhatIsOnThisPc_AndOffersTheThreeTasks()
    {
        if (NoTerminal())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam steam = new();
        PcStatus status = PcStatus.Read(host.Host(), steam.Root);
        using Home home = new(status);
        using ScreenHarness screen = new(home.Window);

        string text = screen.Text("home");

        await Assert.That(text).Contains("Steam      " + steam.Root);
        await Assert.That(text).Contains(FakeSteam.AccountId + " (signed in last)");
        await Assert.That(text).Contains("nothing to put back");
        await Assert.That(text).Contains("Back up this PC");
        await Assert.That(text).Contains("Restore a backup");
        await Assert.That(text).Contains("Put this PC back");
    }

    /// <summary>What, Save, Done: parts this PC has start ticked, Space unticks one, and Back up writes only the rest.</summary>
    [Test]
    [NotInParallel("terminal")]
    public async Task Backup_WalksWhatSaveDone_AndSavesWhatStayedTicked()
    {
        if (NoTerminal())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam steam = new(launchOptions: "-novid");
        string zip = host.Temp.Sub("out/AdvancedConfigBackup-test.zip");
        BackupFlow flow = new(host.Host()) { SteamPath = steam.Root, OutputPath = zip };
        using BackupScreen screenView = new(flow);
        using ScreenHarness screen = new(screenView.Shell.Window);

        string what = screen.Text("backup what");
        await Assert.That(what).Contains("▶ 1 What");
        await Assert.That(what).Contains("CS2 cfg files");
        await Assert.That(flow.IsChosen(Sections.Cs2)).IsTrue();

        // The list starts on its first row, the CS2 settings part; Space unticks it.
        screenView.Shell.Window.SubViews.OfType<FrameView>().Last().SubViews.First().SubViews.OfType<ListView>().First().SetFocus();
        screen.Key(Key.Space);
        await Assert.That(screenView.Shell.Next()).IsTrue().Because(screenView.Shell.Notice);

        string save = screen.Text("backup save");
        await Assert.That(save).Contains("✓ 1 What");
        await Assert.That(save).Contains("Seal it with a passphrase");
        await Assert.That(save).Contains("Not here: acb cannot find its own program file").Because("a test host has no acb to carry");

        await Assert.That(screenView.Shell.Next()).IsTrue().Because(screenView.Shell.Notice);
        await Assert.That(screen.Text("backup done")).Contains("Saved ");
        await Assert.That(flow.DoneLines()).Contains("Saved " + Path.GetFullPath(zip));
        await Assert.That(screenView.Shell.Back()).IsFalse().Because("the backup is written; Done is where the task stays");

        VerifiedArchive archive = ArchiveReader.Open(zip, new ArchiveReadOptions());
        await Assert.That(archive.Manifest.Sections.Select(s => s.Id)).DoesNotContain(Sections.Cs2);
        await Assert.That(archive.Manifest.Sections.Select(s => s.Id)).Contains(Sections.Cs2Game);
    }

    [Test]
    public async Task BackupFlow_LeavesPartsThisPcLacksUnticked_AndRefusesWhatCannotBeWritten()
    {
        using TestHost host = new();
        using FakeSteam steam = new();
        string zip = host.Temp.Sub("b.zip");
        BackupFlow flow = new(host.Host()) { SteamPath = steam.Root, OutputPath = zip };

        await Assert.That(flow.Look()).IsTrue().Because(flow.Problem ?? "");

        await Assert.That(flow.Parts.Single(p => p.Section == Sections.Cs2Game).Found).IsTrue();
        foreach (BackupPart missing in flow.Parts.Where(p => !p.Found))
        {
            await Assert.That(flow.IsChosen(missing.Section)).IsFalse().Because(missing.Section + " is not on this PC");
        }

        flow.Passphrase = "short";
        await Assert.That(flow.OutputProblem()).Contains("at least");
        flow.Passphrase = Passphrase;
        await Assert.That(flow.Save()).IsEqualTo(ExitCodes.Done).Because(flow.SaveProblem ?? "");
        await Assert.That(ArchiveReader.Open(zip, new ArchiveReadOptions { Passphrase = Passphrase }).Level).IsEqualTo(TrustLevel.Seal);
        await Assert.That(string.Join('\n', flow.DoneLines())).Contains("Sealed");

        BackupFlow again = new(host.Host()) { SteamPath = steam.Root, OutputPath = zip };
        again.Look();
        await Assert.That(again.OutputProblem()).Contains("already exists");
        foreach (BackupPart part in again.Parts)
        {
            again.Choose(part.Section, chosen: false);
        }

        await Assert.That(again.OutputProblem()).Contains("Tick at least one part");
    }

    /// <summary>Backup, Choose, Ready, Done: Space unticks a part in Choose, and Restore applies only what stayed ticked.</summary>
    [Test]
    [NotInParallel("terminal")]
    public async Task Restore_WalksEachStep_AndRestoresWhatStayedTicked()
    {
        if (NoTerminal())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam source = new(launchOptions: "-novid -freq 360");
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "echo from the backup\nexec sd_core");
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo this pc");
        string zip = TestArchives.FromSteam(source, host.Temp);
        RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
        using RestoreScreen restore = new(flow);
        using ScreenHarness screen = new(restore.Shell.Window);

        await Assert.That(screen.Text("restore backup")).Contains("Backup file");
        await Assert.That(restore.Shell.Next()).IsTrue().Because(restore.Shell.Notice);

        string choose = screen.Text("restore choose");
        await Assert.That(choose).Contains("autoexec.cfg");
        await Assert.That(choose).Contains("1 changed");
        await Assert.That(choose).Contains("unsigned (hash)").Because("the rail sums up the backup");

        restore.Shell.Window.SubViews.OfType<FrameView>().Last().SubViews.First().SubViews.OfType<ListView>().First().SetFocus();
        screen.Key(Key.Space);
        await Assert.That(restore.Shell.Next()).IsTrue();
        string ready = screen.Text("restore ready");
        await Assert.That(ready).Contains("Nothing stands in the way.");
        await Assert.That(ready).Contains("Pressing Restore applies this:");
        await Assert.That(flow.IsChosen(Sections.Cs2)).IsFalse();

        await Assert.That(restore.Shell.Next()).IsTrue();
        await Assert.That(screen.Text("restore done")).Contains("To put this PC back as it was, run: acb undo");
        await Assert.That(flow.ExitCode).IsEqualTo(ExitCodes.Done);
        await Assert.That(restore.Shell.Back()).IsFalse().Because("once applied, the task stays on Done");

        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).Contains("from the backup");
        await Assert.That(flow.Results.Exists(r => r.Section == Sections.Cs2)).IsFalse().Because("the CS2 settings part was unticked");
    }

    /// <summary>A sealed backup asks for its passphrase in a box only once it turns out to be sealed, and again after a wrong one.</summary>
    [Test]
    public async Task Restore_ASealedBackup_AsksForItsPassphrase_UntilOneOpensIt()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string file = host.Temp.Sub("pass.txt");
        Directory.CreateDirectory(host.Temp.Path);
        File.WriteAllText(file, Passphrase);
        string zip = host.Temp.Sub("sealed.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal", "--passphrase-file", file))
            .IsEqualTo(ExitCodes.Done).Because(host.All);

        RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
        using RestoreScreen restore = new(flow);
        List<bool> asked = [];
        Queue<string?> typed = new(["not it at all, sorry", Passphrase]);
        restore.AskPassphrase = again =>
        {
            asked.Add(again);
            return typed.Dequeue();
        };

        await Assert.That(restore.Shell.Next()).IsTrue().Because(restore.Shell.Notice);
        await Assert.That(asked).IsEquivalentTo([false, true]);
        await Assert.That(flow.Archive!.Level).IsEqualTo(TrustLevel.Seal);

        RestoreFlow refused = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };
        using RestoreScreen nobody = new(refused) { AskPassphrase = _ => null };
        await Assert.That(nobody.Shell.Next()).IsFalse();
        await Assert.That(nobody.Shell.Notice).Contains("none was given");
    }

    /// <summary>Esc goes back a step (Terminal.Gui would otherwise close the screen on it), and from the first step home; Ctrl+Q quits acb.</summary>
    [Test]
    [NotInParallel("terminal")]
    public async Task Esc_GoesBackAStep_AndCtrlQ_Quits()
    {
        if (NoTerminal())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp);
        using RestoreScreen restore = new(new RestoreFlow(host.Host()) { ArchivePath = zip, SteamPath = target.Root });
        using ScreenHarness screen = new(restore.Shell.Window);
        screen.Text("esc");
        await Assert.That(restore.Shell.Next()).IsTrue().Because(restore.Shell.Notice);

        screen.Key(Key.Esc);
        await Assert.That(restore.Shell.Index).IsEqualTo(0);
        await Assert.That(restore.Shell.Quit).IsFalse();

        screen.Key(Key.Q.WithCtrl);
        await Assert.That(restore.Shell.Quit).IsTrue();
    }

    [Test]
    public async Task Restore_WithNoBackupPicked_StaysAndSaysSo()
    {
        using TestHost host = new();
        using RestoreScreen restore = new(new RestoreFlow(host.Host()));

        await Assert.That(restore.Shell.Next()).IsFalse();
        await Assert.That(restore.Shell.Notice).Contains("pick a backup file first");
        await Assert.That(restore.Shell.Index).IsEqualTo(0);
    }

    /// <summary>Records, Review, Choose, Ready, Done: Put back undoes the restore.</summary>
    [Test]
    [NotInParallel("terminal")]
    public async Task Undo_WalksEachStep_AndPutsThePcBack()
    {
        if (NoTerminal())
        {
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
        using UndoScreen undo = new(flow);
        using ScreenHarness screen = new(undo.Shell.Window);

        await Assert.That(screen.Text("undo records")).Contains("Set aside the selected record");
        await Assert.That(undo.Shell.Next()).IsTrue().Because(undo.Shell.Notice);
        await Assert.That(screen.Text("undo review")).Contains("as restored   autoexec.cfg");
        await Assert.That(undo.Shell.Next()).IsTrue();
        await Assert.That(screen.Text("undo choose")).Contains("Part: " + Sections.Label(Sections.Cs2Game));
        await Assert.That(undo.Shell.Next()).IsTrue();
        await Assert.That(screen.Text("undo ready")).Contains("Pressing Put back");
        await Assert.That(undo.Shell.Next()).IsTrue();
        await Assert.That(screen.Text("undo done")).Contains("Done: the PC is back as it was");

        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo before the restore");
        await Assert.That(flow.ExitCode).IsEqualTo(ExitCodes.Done);
    }

    [Test]
    public async Task Undo_WithNothingToPutBack_StaysOnRecords()
    {
        using TestHost host = new();
        using UndoScreen undo = new(new UndoFlow(host.Host()));

        await Assert.That(undo.Shell.Next()).IsFalse();
        await Assert.That(undo.Shell.Notice).Contains("nothing to put back");
    }

    [Test]
    public async Task Wrap_KeepsIndent_AndBreaksAtSpaces()
    {
        string[] lines = [.. Ui.Wrap(["  one two three four five"], 12)];

        await Assert.That(lines).IsEquivalentTo(["  one two", "    three", "    four", "    five"]);
    }

    /// <summary>
    ///     Esc in the working box cancels the work but keeps the box up until the work has stopped, so nothing it says
    ///     afterwards lands on a box that is gone.
    /// </summary>
    [Test]
    [NotInParallel("terminal")]
    public async Task Working_Esc_CancelsTheWork_AndTheBoxWaitsForItToStop()
    {
        if (NoTerminal())
        {
            return;
        }

        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        app.Driver!.SetScreenSize(120, 36);
        bool cancelled = false;
        bool stopped = false;
        bool upAfterEsc = false;
        string? shown = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            upAfterEsc = app.TopRunnableView is Dialog;
            app.LayoutAndDraw(true);
            shown = app.Driver.ToString();
            return false;
        });

        Dialogs.Working(app, "Signing in with Steam", (words, cancel) =>
        {
            words.WriteLine("Open http://127.0.0.1/sign-in");
            cancelled = cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            Thread.Sleep(800);
            words.WriteLine("too late to show");
            stopped = true;
        });

        await Assert.That(cancelled).IsTrue().Because("Esc cancels the work");
        await Assert.That(stopped).IsTrue().Because("Working returns only once the work has ended");
        await Assert.That(upAfterEsc).IsTrue().Because("the box stays up while the work stops");
        await Assert.That(shown).Contains("Cancelling...");
        await Assert.That(shown).Contains("http://127.0.0.1/sign-in");
    }

    /// <summary>On Linux Terminal.Gui's driver waits on the terminal it starts in; CI runs the suite under script(1) to give it one.</summary>
    private static bool NoTerminal()
    {
        if (!OperatingSystem.IsWindows() && (Console.IsInputRedirected || Console.IsOutputRedirected))
        {
            Skip.Test("on Linux the screen needs a terminal; run the suite under script(1)");
            return true;
        }

        return false;
    }
}

/// <summary>
///     One screen on the ANSI driver at 120 by 36. With ACB_SCREEN_DUMP set to a file, every screen read is also
///     appended there, to look at the layout by eye.
/// </summary>
internal sealed class ScreenHarness : IDisposable
{
    private readonly IApplication _app;
    private readonly SessionToken? _token;

    public ScreenHarness(IRunnable view)
    {
        _app = Application.Create();
        _app.Init(DriverRegistry.Names.ANSI);
        _app.Driver!.SetScreenSize(120, 36);
        _token = _app.Begin(view);
    }

    public string Text(string name)
    {
        _app.LayoutAndDraw(true);
        string text = _app.Driver!.ToString() ?? "";
        if (Environment.GetEnvironmentVariable("ACB_SCREEN_DUMP") is { Length: > 0 } dump)
        {
            File.AppendAllText(dump, $"===== {name}\n{text}\n");
        }

        return text;
    }

    public void Key(Key key) => _app.InjectKey(key);

    public void Dispose()
    {
        if (_token is not null)
        {
            _app.End(_token);
        }

        _app.Dispose();
    }
}
