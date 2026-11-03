using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

internal enum HomeChoice
{
    Quit,
    Backup,
    Restore,
    Undo
}

/// <summary>What acb sees on this PC, said once on the home screen. Reading it changes nothing.</summary>
internal sealed record PcStatus(string? SteamRoot, IReadOnlyList<SteamAccount> Accounts, bool Cs2Running, bool SteamRunning, int UndoRecords,
    bool AdminWaiting, string? UndoProblem)
{
    public static PcStatus Read(AcbHost host, string? steamPath = null)
    {
        SteamInstall? steam = null;
        try
        {
            steam = host.LocateSteam(steamPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        int records = 0;
        bool admin = false;
        string? problem = null;
        try
        {
            records = host.UserStore.Records().Count;
            admin = host.MachineStore?.HasRecords() == true;
        }
        catch (UndoStoreUnreachableException ex)
        {
            problem = ex.Message;
        }

        return new PcStatus(steam?.Root, steam is null ? [] : [.. steam.Accounts()], host.IsRunning("cs2"), host.IsRunning("steam"), records, admin, problem);
    }

    public IReadOnlyList<string> Lines()
    {
        string accounts = Accounts.Count == 0
            ? "none with CS2 settings"
            : string.Join(", ", Accounts.Select(a => a.AccountId + (a.MostRecent ? " (signed in last)" : "")));
        string undo = UndoProblem is not null ? "cannot be read: " + UndoProblem
            : UndoRecords == 0 && !AdminWaiting ? "nothing to put back"
            : (UndoRecords == 1 ? "1 restore" : UndoRecords + " restores") + " can be put back" + (AdminWaiting ? ", and an administrator's" : "");
        return
        [
            "Steam      " + Output.Printable(SteamRoot ?? "not found"),
            "Accounts   " + accounts,
            "Running    " + (Cs2Running && SteamRunning ? "CS2 and Steam" : Cs2Running ? "CS2" : SteamRunning ? "Steam (CS2 is closed)" : "neither CS2 nor Steam"),
            "Undo       " + undo
        ];
    }
}

/// <summary>The first screen: what is on this PC, and the three things acb does.</summary>
internal sealed class Home : IDisposable
{
    private readonly List<Button> _actions = [];

    public Home(PcStatus status)
    {
        Window = new Window { Title = "", Width = Dim.Fill(), Height = Dim.Fill(), BorderStyle = LineStyle.None };
        Window.Add(Ui.Text(" " + Ui.Product, Ui.Accent), new Label { X = Pos.AnchorEnd(), Y = 0, Text = "acb " + Archives.Version() + " " });

        FrameView pc = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = 6, Title = "This PC", CanFocus = false };
        TextPane lines = new() { X = 1, CanFocus = false };
        lines.SetLines(status.Lines());
        pc.Add(lines);

        FrameView tasks = new() { X = 0, Y = Pos.Bottom(pc), Width = Dim.Fill(), Height = Dim.Fill(1), Title = "What to do" };
        int waiting = status.UndoRecords + (status.AdminWaiting ? 1 : 0);
        string undoNote = waiting > 0 ? $" ({waiting} waiting)" : "";
        (HomeChoice Choice, string Name, string What)[] rows =
        [
            (HomeChoice.Backup, "Back up this PC", "save this PC's CS2 settings, and the Windows and NVIDIA ones that go with them, to a file"),
            (HomeChoice.Restore, "Restore a backup", "put a backup's settings on this PC, recording what it replaces"),
            (HomeChoice.Undo, "Put this PC back", "undo a restore" + undoNote),
            (HomeChoice.Quit, "Quit", "")
        ];
        for (int i = 0; i < rows.Length; i++)
        {
            (HomeChoice choice, string name, string what) = rows[i];
            Button button = new() { ShadowStyle = ShadowStyles.None, X = 1, Y = 1 + i * 2, Width = 22, Text = name, IsDefault = i == 0 };
            button.Accepting += (_, e) =>
            {
                Choice = choice;
                Window.RequestStop();
                e.Handled = true;
            };
            _actions.Add(button);
            tasks.Add(button, Ui.Text(what, Ui.Muted, x: 24, y: 1 + i * 2));
        }

        Window.Add(pc, tasks, Ui.Text(" Tab or arrows move   Enter chooses   Ctrl+Q quits", Ui.Muted, y: Pos.AnchorEnd(1)));
        Window.KeyDown += (_, key) =>
        {
            if (key == Key.Q.WithCtrl || key == Key.Esc)
            {
                Choice = HomeChoice.Quit;
                Window.RequestStop();
                key.Handled = true;
            }
        };
        _actions[0].SetFocus();
    }

    public Window Window { get; }

    public HomeChoice Choice { get; private set; } = HomeChoice.Quit;

    /// <summary>Presses an action, as a test or the keyboard would.</summary>
    internal void Press(HomeChoice choice)
    {
        Choice = choice;
        Window.RequestStop();
    }

    public void Dispose() => Window.Dispose();
}
