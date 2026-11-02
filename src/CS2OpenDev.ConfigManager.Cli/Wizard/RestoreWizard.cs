using System.Collections.ObjectModel;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>
///     The restore wizard's screen: one step per stage of <see cref="RestoreFlow" />, which makes every decision.
///     Moving on from a step does that step's work (open and check the backup, take the choices, apply), and Done
///     closes it. The screen shows only text the flow has already escaped.
/// </summary>
internal sealed class RestoreWizard : IDisposable
{
    /// <summary>The width text is wrapped to: an 80-column terminal less the wizard's frame and help column.</summary>
    internal const int TextWidth = 70;

    private readonly RestoreFlow _flow;
    private readonly WizardStep _open = new() { Title = "Open" };
    private readonly WizardStep _check = new() { Title = "Check" };
    private readonly WizardStep _review = new() { Title = "Review" };
    private readonly WizardStep _choose = new() { Title = "Choose" };
    private readonly WizardStep _preflight = new() { Title = "Pre-flight" };
    private readonly WizardStep _apply = new() { Title = "Apply", NextButtonText = "Restore" };
    private readonly WizardStep _done = new() { Title = "Done", NextButtonText = "Close" };
    private readonly List<WizardStep> _steps;

    private readonly TextField _archive = new() { X = 0, Y = 1, Width = Dim.Fill() };
    private readonly TextField _policy = new() { X = 0, Y = 4, Width = Dim.Fill() };
    private readonly TextField _steam = new() { X = 0, Y = 7, Width = Dim.Fill() };
    private readonly Button _toUndo = new() { X = 0, Y = 11, Text = "Undo a restore instead" };
    private readonly ListView _checkText = Reading();
    private readonly ListView _reviewList = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(40) };
    private readonly ListView _reviewText = Reading();
    private readonly ListView _chooseList = new() { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), ShowMarks = true, MarkMultiple = true };
    private readonly ListView _preflightText = Reading();
    private readonly CheckBox _askWindows = new() { X = 0, Y = Pos.AnchorEnd(5), Text = "Ask Windows for an administrator for the NVIDIA settings" };
    private readonly ListView _accounts = new() { X = 0, Y = Pos.AnchorEnd(4), Width = Dim.Fill(), Height = 3 };
    private readonly Button _checkAgain = new() { X = 0, Y = Pos.AnchorEnd(1), Text = "Check again" };
    private readonly ListView _applyText = Reading();
    private readonly ListView _doneText = Reading();

    private readonly List<(SectionReview Section, FileChange? File)> _reviewRows = [];
    private readonly List<(string? Section, FileChange? File, bool Gsi)> _chooseRows = [];
    private List<SteamAccount> _accountRows = [];

    public RestoreWizard(RestoreFlow flow)
    {
        _flow = flow;
        Wizard = new Terminal.Gui.Views.Wizard { Title = "Advanced Config Backup: restore", Width = Dim.Fill(), Height = Dim.Fill() };
        _steps = [_open, _check, _review, _choose, _preflight, _apply, _done];

        _archive.Text = flow.ArchivePath;
        _policy.Text = flow.PolicyPath;
        _steam.Text = flow.SteamPath;
        _open.Add(new Label { X = 0, Y = 0, Text = "Backup file (.zip):" }, _archive,
            new Label { X = 0, Y = 3, Text = "Organiser's policy file (optional):" }, _policy,
            new Label { X = 0, Y = 6, Text = "Steam folder (optional; found when empty):" }, _steam,
            new Label { X = 0, Y = 9, Text = "Nothing on this PC changes until you press Restore." }, _toUndo);
        _toUndo.Accepting += (_, e) =>
        {
            SwitchToUndo = true;
            Wizard.RequestStop();
            e.Handled = true;
        };
        _open.HelpText = "Pick the backup to restore. An organiser's policy can only narrow what is restored.";

        _check.Add(_checkText);
        _check.HelpText = "What the backup is, and whether it may be restored here.";

        _reviewText.Y = Pos.Bottom(_reviewList) + 1;
        _reviewList.ValueChanged += (_, _) => ShowReviewed();
        _review.Add(_reviewList, _reviewText);
        _review.HelpText = "Each part, and each file: new here, different here, or already the same. Pick a file to see what changes.";

        _choose.Add(new Label { X = 0, Y = 0, Text = "Space ticks or unticks. Unticked parts and files keep this PC's copy." }, _chooseList);
        _choose.HelpText = "Choosing can only take parts and files away, never add one the rules left out.";

        _preflightText.Height = Dim.Fill(6);
        _checkAgain.Accepting += (_, e) =>
        {
            _flow.Refresh();
            ShowPreflight();
            e.Handled = true;
        };
        _preflight.Add(_preflightText, _askWindows, _accounts, _checkAgain);
        _preflight.HelpText = "What stands in the way, before anything changes.";

        _apply.Add(_applyText);
        _apply.HelpText = "Press Restore to apply. Every change is recorded first, so acb undo can put this PC back.";

        _done.Add(_doneText);

        foreach (WizardStep step in _steps)
        {
            Wizard.AddStep(step);
        }

        Wizard.StepChanging += OnStepChanging;
    }

    public Terminal.Gui.Views.Wizard Wizard { get; }

    /// <summary>The person chose to put the PC back instead; <see cref="Run" /> starts the undo wizard.</summary>
    public bool SwitchToUndo { get; private set; }

    /// <summary>The steps in order, for tests that move through them.</summary>
    public IReadOnlyList<WizardStep> Steps => _steps;

    /// <summary>
    ///     Runs the wizard on the terminal until it closes, or the undo wizard when the person picks it on Open. The exit
    ///     code is the restore's or the undo's, or refused when nothing was applied.
    /// </summary>
    public static int Run(AcbHost host)
    {
        RestoreFlow flow = new(host);
        using IApplication app = Application.Create();
        app.Init();
        bool undo;
        using (RestoreWizard wizard = new(flow))
        {
            app.Run(wizard.Wizard);
            undo = wizard.SwitchToUndo;
        }

        if (!undo)
        {
            return flow.ExitCode;
        }

        UndoFlow undoFlow = new(host);
        using UndoWizard undoWizard = new(undoFlow);
        app.Run(undoWizard.Wizard);
        return undoFlow.ExitCode;
    }

    public void Dispose() => Wizard.Dispose();

    /// <summary>The lines a read-only list shows, for tests.</summary>
    internal static IReadOnlyList<string> Lines(ListView view) =>
        view.Source is null ? [] : [.. Enumerable.Range(0, view.Source.Count).Select(i => view.Source.ToList()[i]?.ToString() ?? "")];

    /// <summary>Wraps text at word breaks to <paramref name="width" />, keeping each line's indent on its continuations.</summary>
    internal static IEnumerable<string> Wrap(IEnumerable<string> lines, int width)
    {
        foreach (string text in lines)
        {
            foreach (string paragraph in text.Split('\n'))
            {
                string rest = paragraph;
                string indent = new(' ', Math.Min(rest.Length - rest.TrimStart(' ').Length, width / 2));
                while (rest.Length > width)
                {
                    // A break inside the leading spaces would only move them to the next line, so it is not one.
                    int lead = rest.Length - rest.TrimStart(' ').Length;
                    int cut = rest.LastIndexOf(' ', width);
                    if (cut <= lead)
                    {
                        cut = width;
                    }

                    yield return rest[..cut].TrimEnd();
                    rest = indent + "  " + rest[cut..].TrimStart();
                }

                yield return rest;
            }
        }
    }

    private void OnStepChanging(object? sender, ValueChangingEventArgs<WizardStep?> e)
    {
        int from = e.CurrentValue is null ? -1 : _steps.IndexOf(e.CurrentValue);
        int to = e.NewValue is null ? -1 : _steps.IndexOf(e.NewValue);
        if (to <= from)
        {
            // Back: nothing on the PC has changed before Apply; once it ran, Done is where the wizard stays.
            e.Handled = from >= _steps.IndexOf(_done);
            return;
        }

        switch (from)
        {
            case 0:
                _flow.ArchivePath = _archive.Text.Trim();
                _flow.PolicyPath = _policy.Text.Trim();
                _flow.SteamPath = _steam.Text.Trim();
                _flow.Check();
                Show(_checkText, _flow.CheckLines());
                break;
            case 1:
                e.Handled = _flow.Session is null;
                if (!e.Handled)
                {
                    ShowReview();
                }

                break;
            case 2:
                ShowChoose();
                break;
            case 3:
                TakeChoices();
                ShowPreflight();
                break;
            case 4:
                TakePreflight();
                Show(_applyText, ApplyLines());
                break;
            case 5:
                _flow.Apply();
                Show(_doneText, _flow.DoneLines());
                break;
        }
    }

    private void ShowReview()
    {
        _reviewRows.Clear();
        List<string> rows = [];
        foreach (SectionReview section in _flow.Review())
        {
            _reviewRows.Add((section, null));
            rows.Add($"{section.Outcome,-9} {section.Label}");
            foreach (FileChange file in section.Files)
            {
                _reviewRows.Add((section, file));
                rows.Add($"            {Kind(file.Kind),-8} {Output.Printable(file.Relative)}");
            }
        }

        _reviewList.SetSource(new ObservableCollection<string>(rows));
        ShowReviewed();
    }

    private void ShowReviewed()
    {
        int row = _reviewList.SelectedItem ?? 0;
        if (row < 0 || row >= _reviewRows.Count)
        {
            Show(_reviewText, []);
            return;
        }

        (SectionReview section, FileChange? file) = _reviewRows[row];
        Show(_reviewText, file is null
            ? [$"{section.Label}: {section.Outcome}", section.Detail, "", .. RestoreFlow.SettingLines(section)]
            : _flow.Diff(file));
    }

    private void ShowChoose()
    {
        _chooseRows.Clear();
        List<string> rows = [];
        List<bool> marks = [];
        Dictionary<string, SectionReview> reviewed = _flow.Review().ToDictionary(r => r.Section, StringComparer.Ordinal);
        foreach (string section in _flow.Choosable)
        {
            _chooseRows.Add((section, null, false));
            rows.Add(Sections.Label(section));
            marks.Add(_flow.IsChosen(section));
            foreach (FileChange file in reviewed.TryGetValue(section, out SectionReview? r) ? r.Files : [])
            {
                _chooseRows.Add((section, file, false));
                rows.Add("    " + Output.Printable(file.Relative));
                marks.Add(_flow.IsFileChosen(file));
            }
        }

        if (_flow.HoldsGsi)
        {
            _chooseRows.Add((null, null, true));
            rows.Add("Game state integration files too (they send live game data to the address they name)");
            marks.Add(_flow.Gsi);
        }

        _chooseList.SetSource(new ObservableCollection<string>(rows));
        for (int i = 0; i < marks.Count; i++)
        {
            _chooseList.Source!.SetMark(i, marks[i]);
        }

        _chooseList.SelectedItem = 0;
    }

    private void TakeChoices()
    {
        for (int i = 0; i < _chooseRows.Count; i++)
        {
            bool marked = _chooseList.Source!.IsMarked(i);
            (string? section, FileChange? file, bool gsi) = _chooseRows[i];
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
    }

    private void ShowPreflight()
    {
        Show(_preflightText, _flow.PreflightLines().SelectMany(l => new[] { l, "" }));
        _askWindows.Visible = _flow.Session is { AdminSections.Count: > 0 } && _flow.Elevation.CanAsk;
        _askWindows.Value = _flow.AskWindows ? CheckState.Checked : CheckState.UnChecked;
        _accountRows = _flow.AccountChoice() is { } choice ? [.. choice.Accounts] : [];
        _accounts.Visible = _accountRows.Count > 0;
        _accounts.SetSource(new ObservableCollection<string>(
            ["Leave my CS2 settings and launch options", .. _accountRows.Select(a => "Mine: " + a.AccountId + (a.MostRecent ? " (signed in last)" : ""))]));
        _accounts.SelectedItem = 0;
    }

    private void TakePreflight()
    {
        _flow.AskWindows = _askWindows.Visible && _askWindows.Value == CheckState.Checked;
        int pick = _accounts.SelectedItem ?? 0;
        if (_accounts.Visible && pick > 0 && pick <= _accountRows.Count)
        {
            _flow.UseAccount(_accountRows[pick - 1].AccountId);
        }
    }

    private List<string> ApplyLines()
    {
        List<string> lines = ["Pressing Restore applies this:", ""];
        foreach (SectionReview section in _flow.Review())
        {
            lines.Add($"{section.Outcome,-9} {section.Label}: {section.Detail}");
        }

        lines.Add("");
        if (_flow.AskWindows)
        {
            lines.Add("Windows will ask for an administrator for the NVIDIA settings.");
        }

        lines.Add("Every change is recorded before it is made. To put this PC back afterwards, run: acb undo");
        return lines;
    }

    private static string Kind(FileChangeKind kind) => kind switch
    {
        FileChangeKind.New => "new",
        FileChangeKind.Changed => "changed",
        FileChangeKind.Same => "same",
        _ => "left"
    };

    internal static ListView Reading() => new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };

    internal static void Show(ListView view, IEnumerable<string> lines) =>
        view.SetSource(new ObservableCollection<string>(Wrap(lines, TextWidth)));
}
