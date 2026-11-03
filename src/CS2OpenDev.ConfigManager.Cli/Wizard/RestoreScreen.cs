using System.Collections.ObjectModel;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>
///     Restoring, on the task screen: Backup (pick it; a sealed one asks for its passphrase), Choose (each part and
///     file with what it would do, and the change for the one selected), Ready (what stands in the way), Done.
///     <see cref="RestoreFlow" /> makes every decision; this only shows it.
/// </summary>
internal sealed class RestoreScreen : IDisposable
{
    private readonly RestoreFlow _flow;

    private readonly TextField _archive = new() { X = 0, Y = 1, Width = Dim.Fill(12) };
    private readonly Button _browse = new() { ShadowStyle = ShadowStyles.None, X = Pos.AnchorEnd(), Y = 1, Text = "Browse" };
    private readonly Label _found = Ui.Text("", Ui.Good, y: 2);
    private readonly TextField _policy = new() { X = 0, Y = 6, Width = Dim.Fill(12) };
    private readonly Button _browsePolicy = new() { ShadowStyle = ShadowStyles.None, X = Pos.AnchorEnd(), Y = 6, Text = "Browse" };
    private readonly TextField _steam = new() { X = 0, Y = 9, Width = Dim.Fill() };
    private readonly ListView _choose = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(45), ShowMarks = true, MarkMultiple = true };
    private readonly TextPane _detail = new();
    private readonly TextPane _ready = new();
    private readonly CheckBox _askWindows = new() { X = 0, Text = "Ask Windows for an administrator for the NVIDIA settings" };
    private readonly ListView _accounts = new() { X = 0, Width = Dim.Fill(), Height = 3 };
    private readonly Button _checkAgain = new() { ShadowStyle = ShadowStyles.None, X = 0, Text = "Check again" };
    private readonly TextPane _done = new();
    private readonly List<(string? Section, FileChange? File, bool Gsi)> _rows = [];
    private List<SteamAccount> _accountRows = [];

    /// <summary>Asks for a sealed backup's passphrase; null when the person gives none. A dialog on screen, a stand-in in tests.</summary>
    public Func<bool, string?> AskPassphrase { get; set; }

    /// <summary>Picks a file; null when the person cancels. A file dialog on screen, a stand-in in tests.</summary>
    public Func<string, string?> PickFile { get; set; }

    /// <summary>Asks whether to use a backup whose signing key was revoked, saying why; a stand-in in tests.</summary>
    public Func<string, bool> AskRevoked { get; set; }

    public RestoreScreen(RestoreFlow flow)
    {
        _flow = flow;
        AskPassphrase = again => Dialogs.Passphrase(Shell!.Window.App, again);
        PickFile = title => Dialogs.Open(Shell!.Window.App, title);
        AskRevoked = why => Dialogs.YesNo(Shell!.Window.App, "Its signing key was revoked", why, "Use it anyway");

        View open = new() { CanFocus = true };
        _archive.Text = flow.ArchivePath;
        _policy.Text = flow.PolicyPath;
        _steam.Text = flow.SteamPath;
        if (flow.Found is { } found)
        {
            _found.Text = found + " Press Next to check it, or Browse for another.";
        }

        _browse.Accepting += (_, e) =>
        {
            if (PickFile("Pick a backup (a .zip file)") is { } picked)
            {
                _archive.Text = picked;
            }

            e.Handled = true;
        };
        _browsePolicy.Accepting += (_, e) =>
        {
            if (PickFile("Pick an organiser's policy file") is { } picked)
            {
                _policy.Text = picked;
            }

            e.Handled = true;
        };
        open.Add(Ui.Text("Backup file", Ui.Accent), _archive, _browse, _found,
            Ui.Text("Only if you were given one", Ui.Muted, y: 4),
            Ui.Text("Organiser's policy (it can only narrow what is restored)", y: 5), _policy, _browsePolicy,
            Ui.Text("Steam folder (found when empty)", y: 8), _steam,
            Ui.Text("Nothing on this PC changes until you press Restore.", Ui.Muted, y: 11));

        _detail.Y = Pos.Bottom(_choose) + 1;
        _detail.Height = Dim.Fill();
        _choose.ValueChanged += (_, _) => ShowDetail();
        View choose = new() { CanFocus = true };
        choose.Add(_choose, new Line { Y = Pos.Bottom(_choose), Width = Dim.Fill() }, _detail);

        _ready.Height = Dim.Fill(6);
        _askWindows.Y = Pos.AnchorEnd(5);
        _accounts.Y = Pos.AnchorEnd(4);
        _checkAgain.Y = Pos.AnchorEnd(1);
        _checkAgain.Accepting += (_, e) =>
        {
            _flow.Refresh();
            ShowReady();
            e.Handled = true;
        };
        View ready = new() { CanFocus = true };
        ready.Add(_ready, _askWindows, _accounts, _checkAgain);

        Shell = new TaskShell("Restore",
        [
            new TaskStep { Title = "Backup", Body = open, Leave = Open },
            new TaskStep { Title = "Choose", Body = choose, Enter = ShowChoose, Leave = TakeChoices },
            new TaskStep { Title = "Ready", Body = ready, Enter = ShowReady, Leave = Apply, NextText = "Restore", Commits = true },
            new TaskStep { Title = "Done", Body = _done, Enter = () => _done.SetLines(_flow.DoneLines()), NextText = "Finish" }
        ], Summary);
    }

    public TaskShell Shell { get; }

    public void Dispose() => Shell.Dispose();

    /// <summary>Opens and checks the backup, asking for a passphrase when it is sealed. Null to move on.</summary>
    private string? Open()
    {
        _flow.ArchivePath = _archive.Text.Trim();
        _flow.PolicyPath = _policy.Text.Trim();
        _flow.SteamPath = _steam.Text.Trim();
        _flow.Passphrase = "";
        bool open = _flow.Check();
        _archive.Text = _flow.ArchivePath;
        for (int tries = 0; !open && _flow.SealRefusal is { } seal && tries < RestoreSession.PassphraseTries; tries++)
        {
            if (AskPassphrase(seal == SealNotOpenedException.WrongMessage) is not { } passphrase)
            {
                break;
            }

            _flow.Passphrase = passphrase;
            open = _flow.Check();
        }

        if (!open && _flow.RevokedRefusal && _flow.RevokedAccepted != _flow.ArchivePath
            && AskRevoked(_flow.Refusal + " Use it anyway, checked only as unchanged (the hash level)?"))
        {
            _flow.RevokedAccepted = _flow.ArchivePath;
            open = _flow.Check();
        }

        return open ? null : _flow.Refusal;
    }

    /// <summary>
    ///     The rail's summary of the backup: at the identity level the signer it checked, and the account whose
    ///     settings these are only as the manifest says, with a warning when that is not the signer's.
    /// </summary>
    internal List<string> Summary()
    {
        if (_flow.Archive is not { } archive)
        {
            return [];
        }

        List<string> lines =
        [
            Output.Printable(Path.GetFileName(_flow.ArchivePath)),
            archive.RevokedSigner is not null ? "revoked signature (hash)"
                : archive.Level switch { TrustLevel.Hash => "unsigned (hash)", TrustLevel.Seal => "sealed", _ => "signed (identity)" },
            archive.Manifest.Created.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "Z",
        ];
        if (archive.RevokedSigner is { } claimed)
        {
            lines.Add("Revoked: " + Output.Printable(claimed.RevokedWarning()));
        }

        if (archive.IdentitySigner is { } signer)
        {
            lines.Add("signed by " + signer.Describe());
            if (signer.OtherAccount(archive.Manifest.SteamAccount) is { } other)
            {
                lines.Add("Note: " + Output.Printable(other));
            }

            lines.Add(signer.Revocations());
            if (_flow.CheckedByCarriedRestorer)
            {
                lines.Add(Archives.CarriedRestorer);
            }
        }

        if (archive.Manifest.SteamAccount is { } account)
        {
            lines.Add("settings of account " + Output.Printable(account));
        }

        if (archive.Restorer is not null)
        {
            lines.Add("carries a restorer");
        }

        return lines;
    }

    private void ShowChoose()
    {
        _rows.Clear();
        List<(string, string)> rows = [];
        List<bool> marks = [];
        Dictionary<string, SectionReview> reviewed = _flow.Review().ToDictionary(r => r.Section, StringComparer.Ordinal);
        foreach (string section in _flow.Choosable)
        {
            SectionReview? review = reviewed.GetValueOrDefault(section);
            _rows.Add((section, null, false));
            rows.Add((Sections.Label(section), review is null ? "" : Counts(review)));
            marks.Add(_flow.IsChosen(section));
            foreach (FileChange file in review?.Files ?? [])
            {
                _rows.Add((section, file, false));
                rows.Add(("    " + Output.Printable(file.Relative), Kind(file.Kind)));
                marks.Add(_flow.IsFileChosen(file));
            }
        }

        if (_flow.HoldsGsi)
        {
            _rows.Add((null, null, true));
            rows.Add(("Game state integration files", _flow.Gsi ? "written" : "send live game data; written only when ticked"));
            marks.Add(_flow.Gsi);
        }

        _choose.SetSource(new ObservableCollection<string>(Ui.Columns(rows)));
        for (int i = 0; i < marks.Count; i++)
        {
            _choose.Source!.SetMark(i, marks[i]);
        }

        _choose.SelectedItem = 0;
        ShowDetail();
    }

    private void ShowDetail()
    {
        int row = _choose.SelectedItem ?? 0;
        if (row < 0 || row >= _rows.Count)
        {
            _detail.SetLines(["Nothing in this backup can be restored here."]);
            return;
        }

        (string? section, FileChange? file, bool gsi) = _rows[row];
        if (gsi)
        {
            _detail.SetLines(["Game state integration files make CS2 send live game data to the address each one names, for every account on this PC. They are written only when ticked."]);
        }
        else if (file is not null)
        {
            _detail.SetLines(_flow.Diff(file));
        }
        else if (section is not null && _flow.Review().FirstOrDefault(r => r.Section == section) is { } review)
        {
            _detail.SetLines([$"{review.Label}: {review.Detail}", "", .. RestoreFlow.SettingLines(review)]);
        }
    }

    private string? TakeChoices()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            bool marked = _choose.Source!.IsMarked(i);
            (string? section, FileChange? file, bool gsi) = _rows[i];
            if (gsi)
            {
                _flow.ChooseGsi(marked);
            }
            else if (file is not null)
            {
                _flow.ChooseFile(file, marked);
            }
            else if (section is not null)
            {
                _flow.Choose(section, marked);
            }
        }

        return null;
    }

    private void ShowReady()
    {
        List<string> lines = [.. _flow.PreflightLines().SelectMany(l => new[] { l, "" }), "Pressing Restore applies this:"];
        lines.AddRange(_flow.Review().Where(r => _flow.IsChosen(r.Section)).Select(r => $"  {r.Label}: {r.Detail}"));
        lines.Add("");
        lines.Add("Every change is recorded first, so Put this PC back (acb undo) can undo it.");
        _ready.SetLines(lines);
        _askWindows.Visible = _flow.Session is { AdminSections.Count: > 0 } && _flow.Elevation.CanAsk;
        _askWindows.Value = _flow.AskWindows ? CheckState.Checked : CheckState.UnChecked;
        _accountRows = _flow.AccountChoice() is { } choice ? [.. choice.Accounts] : [];
        _accounts.Visible = _accountRows.Count > 0;
        _accounts.SetSource(new ObservableCollection<string>(
            ["Leave my CS2 settings and launch options", .. _accountRows.Select(a => "Mine: " + a.AccountId + (a.MostRecent ? " (signed in last)" : ""))]));
        _accounts.SelectedItem = 0;
    }

    private string? Apply()
    {
        _flow.AskWindows = _askWindows.Visible && _askWindows.Value == CheckState.Checked;
        int pick = _accounts.SelectedItem ?? 0;
        if (_accounts.Visible && pick > 0 && pick <= _accountRows.Count)
        {
            _flow.UseAccount(_accountRows[pick - 1].AccountId);
        }

        _flow.Apply();
        return null;
    }

    private static string Counts(SectionReview review)
    {
        if (review.Files.Count == 0)
        {
            return review.Settings.Count > 0 ? (review.Settings.Count == 1 ? "1 setting" : review.Settings.Count + " settings") : review.Outcome;
        }

        return string.Join(", ", review.Files.GroupBy(f => f.Kind).OrderBy(g => g.Key).Select(g => g.Count() + " " + Kind(g.Key)));
    }

    private static string Kind(FileChangeKind kind) => kind switch
    {
        FileChangeKind.New => "new",
        FileChangeKind.Changed => "changed",
        FileChangeKind.Same => "same",
        _ => "left"
    };
}
