using System.Collections.ObjectModel;
using CS2OpenDev.ConfigManager.Archive;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>
///     Backing up, on the task screen: What (each part, found on this PC or not), Save (where, sealed or not, with
///     acb in it or not), Done. <see cref="BackupFlow" /> holds the choices and saves.
/// </summary>
internal sealed class BackupScreen : IDisposable
{
    private readonly BackupFlow _flow;
    private readonly ListView _parts = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(10), ShowMarks = true, MarkMultiple = true };
    private readonly TextPane _partDetail = new() { CanFocus = false };
    private readonly CheckBox _gsi = new() { X = 0, Text = "Also game state integration files (they send live game data to the address they name)" };
    private readonly ListView _accounts = new() { X = 0, Width = Dim.Fill(), Height = 3 };
    private readonly Label _accountsLabel = Ui.Text("Account to save");
    private readonly TextField _output = new() { X = 0, Y = 1, Width = Dim.Fill(12) };
    private readonly Button _browse = new() { ShadowStyle = ShadowStyles.None, X = Pos.AnchorEnd(), Y = 1, Text = "Browse" };
    private readonly CheckBox _seal = new() { X = 0, Y = 4, Text = "Seal it with a passphrase" };
    private readonly Label _sealNote = Ui.Text("", Ui.Muted, x: 4, y: 5);
    private readonly CheckBox _carry = new() { X = 0, Y = 7, Text = "Carry acb in it, for a PC with nothing installed" };
    private readonly Label _carryNote = Ui.Text("", Ui.Muted, x: 4, y: 8);
    private readonly TextPane _done = new();
    private bool _looked;

    /// <summary>Asks for a new passphrase; null when the person cancels. A dialog on screen, a stand-in in tests.</summary>
    public Func<string?> AskPassphrase { get; set; }

    /// <summary>Picks where to save; null when the person cancels.</summary>
    public Func<string, string?> PickSave { get; set; }

    public BackupScreen(BackupFlow flow)
    {
        _flow = flow;
        AskPassphrase = () => Dialogs.NewPassphrase(Shell!.Window.App);
        PickSave = suggested => Dialogs.Save(Shell!.Window.App, "Save the backup as", suggested);

        View what = new() { CanFocus = true };
        _partDetail.Y = Pos.AnchorEnd(9);
        _partDetail.Height = 2;
        _parts.ValueChanged += (_, _) => ShowPartDetail();
        _gsi.Y = Pos.AnchorEnd(6);
        _accountsLabel.Y = Pos.AnchorEnd(4);
        _accounts.Y = Pos.AnchorEnd(3);
        what.Add(Ui.Text("Space ticks or unticks. Parts this PC does not have cannot be saved.", Ui.Muted), _parts,
            new Line { Y = Pos.AnchorEnd(10), Width = Dim.Fill() }, _partDetail, _gsi, _accountsLabel, _accounts);

        View save = new() { CanFocus = true };
        _browse.Accepting += (_, e) =>
        {
            if (PickSave(_output.Text.Trim()) is { } picked)
            {
                _output.Text = picked.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? picked : picked + ".zip";
            }

            e.Handled = true;
        };
        _seal.ValueChanged += (_, _) => SealChanged();
        save.Add(Ui.Text("Save as", Ui.Accent), _output, _browse, Ui.Text("How", Ui.Accent, y: 3), _seal, _sealNote, _carry, _carryNote);

        Shell = new TaskShell("Back up",
        [
            new TaskStep { Title = "What", Body = what, Enter = ShowParts, Leave = TakeParts },
            new TaskStep { Title = "Save", Body = save, Enter = ShowSave, Leave = Save, NextText = "Back up", Commits = true },
            new TaskStep { Title = "Done", Body = _done, Enter = () => _done.SetLines(_flow.DoneLines()), NextText = "Finish" }
        ], Summary);
    }

    public TaskShell Shell { get; }

    public void Dispose() => Shell.Dispose();

    private List<string> Summary()
    {
        if (!_looked)
        {
            return [];
        }

        List<string> lines = [.. _flow.Chosen.Select(s => "• " + Kit.Sections.Label(s))];
        if (_flow.Passphrase is not null)
        {
            lines.Add("sealed");
        }

        if (_flow.SelfContained)
        {
            lines.Add("carries acb");
        }

        return lines;
    }

    private void ShowParts()
    {
        if (!_looked)
        {
            _flow.Look();
            _looked = true;
        }

        _parts.SetSource(new ObservableCollection<string>(Ui.Columns([.. _flow.Parts.Select(p => (p.Label, p.Found ? p.Detail : "not on this PC"))])));
        for (int i = 0; i < _flow.Parts.Count; i++)
        {
            _parts.Source!.SetMark(i, _flow.Parts[i].Found && _flow.IsChosen(_flow.Parts[i].Section));
        }

        _parts.SelectedItem = 0;
        ShowPartDetail();
        _gsi.Value = _flow.Gsi ? CheckState.Checked : CheckState.UnChecked;
        bool several = _flow.Accounts.Count > 1;
        _accounts.Visible = several;
        _accountsLabel.Visible = several;
        _accounts.SetSource(new ObservableCollection<string>(_flow.Accounts.Select(a => a.AccountId + (a.MostRecent ? " (signed in last)" : ""))));
        _accounts.SelectedItem = 0;
    }

    private void ShowPartDetail()
    {
        int row = _parts.SelectedItem ?? 0;
        _partDetail.SetLines(row >= 0 && row < _flow.Parts.Count
            ? [_flow.Parts[row].Label + ": " + (_flow.Parts[row].Found ? "" : "not on this PC, ") + _flow.Parts[row].Detail]
            : []);
    }

    private string? TakeParts()
    {
        if (_flow.Problem is { } problem)
        {
            return problem;
        }

        for (int i = 0; i < _flow.Parts.Count; i++)
        {
            _flow.Choose(_flow.Parts[i].Section, _parts.Source!.IsMarked(i));
        }

        _flow.Gsi = _gsi.Value == CheckState.Checked;
        if (_accounts.Visible && _accounts.SelectedItem is int pick && pick >= 0 && pick < _flow.Accounts.Count)
        {
            _flow.Account = _flow.Accounts[pick].AccountId;
        }

        return _flow.Chosen.Count == 0 ? "Tick at least one part this PC has." : null;
    }

    private void ShowSave()
    {
        _output.Text = _flow.OutputPath;
        _seal.Value = _flow.Passphrase is null ? CheckState.UnChecked : CheckState.Checked;
        _sealNote.Text = SealText();
        string? cannot = _flow.SelfContainedProblem;
        _carry.Enabled = cannot is null;
        _carry.Value = _flow.SelfContained && cannot is null ? CheckState.Checked : CheckState.UnChecked;
        _carryNote.Text = cannot is null
            ? $"Adds this acb to the zip as restorer/{ArchiveFormat.RestorerFileName(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64)}."
            : "Not here: " + cannot + ".";
    }

    private void SealChanged()
    {
        if (_seal.Value == CheckState.Checked && _flow.Passphrase is null)
        {
            _flow.Passphrase = AskPassphrase();
            if (_flow.Passphrase is null)
            {
                _seal.Value = CheckState.UnChecked;
            }
        }
        else if (_seal.Value != CheckState.Checked)
        {
            _flow.Passphrase = null;
        }

        _sealNote.Text = SealText();
        Shell.Refresh();
    }

    private string SealText() => _flow.Passphrase is null
        ? "Unsealed, anyone holding the file can read it and could edit it."
        : "Sealed: it opens only with the passphrase you set.";

    private string? Save()
    {
        _flow.OutputPath = _output.Text.Trim();
        _flow.SelfContained = _carry.Enabled && _carry.Value == CheckState.Checked;
        if (_flow.OutputProblem() is { } problem)
        {
            return problem;
        }

        _flow.Save();
        return _flow.Saved ? null : _flow.SaveProblem;
    }
}
