using System.Collections.ObjectModel;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>
///     The undo wizard's screen: list the records, review what an undo would put back (and what changed since each
///     restore), choose, pre-flight, apply. <see cref="UndoFlow" /> holds the state, and applying is
///     <c>acb undo</c>'s own run.
/// </summary>
internal sealed class UndoWizard : IDisposable
{
    private readonly UndoFlow _flow;
    private readonly WizardStep _records = new() { Title = "Records" };
    private readonly WizardStep _review = new() { Title = "Review" };
    private readonly WizardStep _choose = new() { Title = "Choose" };
    private readonly WizardStep _preflight = new() { Title = "Pre-flight" };
    private readonly WizardStep _apply = new() { Title = "Apply", NextButtonText = "Put back" };
    private readonly WizardStep _done = new() { Title = "Done", NextButtonText = "Close" };
    private readonly List<WizardStep> _steps;

    private readonly ListView _recordList = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3) };
    private readonly Button _setAside = new() { X = 0, Y = Pos.AnchorEnd(2), Text = "Set aside the selected record" };
    private readonly Label _setAsideNote = new() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Text = "" };
    private readonly ListView _reviewList = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(40) };
    private readonly ListView _reviewText = RestoreWizard.Reading();
    private readonly ListView _chooseList = new() { X = 0, Y = 2, Width = Dim.Fill(), Height = Dim.Fill(), ShowMarks = true, MarkMultiple = true };
    private readonly ListView _preflightText = RestoreWizard.Reading();
    private readonly CheckBox _askWindows = new() { X = 0, Y = Pos.AnchorEnd(3), Text = "Ask Windows for an administrator for the administrator's records" };
    private readonly Button _checkAgain = new() { X = 0, Y = Pos.AnchorEnd(1), Text = "Check again" };
    private readonly ListView _applyText = RestoreWizard.Reading();
    private readonly ListView _doneText = RestoreWizard.Reading();

    private readonly List<(RecordReview Record, string? Section, UndoFile? File)> _reviewRows = [];
    private readonly List<(UndoRecord? Record, string? Section)> _chooseRows = [];

    public UndoWizard(UndoFlow flow)
    {
        _flow = flow;
        Wizard = new Terminal.Gui.Views.Wizard { Title = "Advanced Config Backup: undo", Width = Dim.Fill(), Height = Dim.Fill() };
        _steps = [_records, _review, _choose, _preflight, _apply, _done];

        _setAside.Accepting += (_, e) =>
        {
            int row = _recordList.SelectedItem ?? -1;
            if (row >= 0 && row < _flow.Records.Count && !_flow.Records[row].IsReadable)
            {
                _setAsideNote.Text = _flow.SetAside(_flow.Records[row]);
                ShowRecords();
            }
            else
            {
                _setAsideNote.Text = "Only a record that cannot be read is set aside here; acb undo --set-aside covers the rest.";
            }

            e.Handled = true;
        };
        _records.Add(_recordList, _setAside, _setAsideNote);
        _records.HelpText = "Every restore left a record of what it replaced. Newest first; an older record's part waits while a newer one holds it.";

        _reviewText.Y = Pos.Bottom(_reviewList) + 1;
        _reviewList.ValueChanged += (_, _) => ShowReviewed();
        _review.Add(_reviewList, _reviewText);
        _review.HelpText = "What an undo would put back. A file changed since its restore (by CS2, or by someone else on this PC) loses those changes too.";

        _choose.Add(new Label { X = 0, Y = 0, Text = "Space ticks or unticks. An unticked record still holds back older records' same parts." }, _chooseList);

        _preflightText.Height = Dim.Fill(4);
        _checkAgain.Accepting += (_, e) =>
        {
            ShowPreflight();
            e.Handled = true;
        };
        _preflight.Add(_preflightText, _askWindows, _checkAgain);

        _apply.Add(_applyText);
        _done.Add(_doneText);
        foreach (WizardStep step in _steps)
        {
            Wizard.AddStep(step);
        }

        Wizard.StepChanging += OnStepChanging;
        _flow.Load();
        ShowRecords();
    }

    public Terminal.Gui.Views.Wizard Wizard { get; }

    public IReadOnlyList<WizardStep> Steps => _steps;

    public void Dispose() => Wizard.Dispose();

    private void OnStepChanging(object? sender, ValueChangingEventArgs<WizardStep?> e)
    {
        int from = e.CurrentValue is null ? -1 : _steps.IndexOf(e.CurrentValue);
        int to = e.NewValue is null ? -1 : _steps.IndexOf(e.NewValue);
        if (to <= from)
        {
            e.Handled = from >= _steps.IndexOf(_done);
            return;
        }

        switch (from)
        {
            case 0:
                e.Handled = _flow.Problem is not null || (_flow.Records.Count == 0 && !_flow.AdminWaiting);
                if (!e.Handled)
                {
                    ShowReview();
                }

                break;
            case 1:
                ShowChoose();
                break;
            case 2:
                TakeChoices();
                ShowPreflight();
                break;
            case 3:
                _flow.AskWindows = _askWindows.Visible && _askWindows.Value == CheckState.Checked;
                RestoreWizard.Show(_applyText, ["Pressing Put back puts back what was chosen, newest record first.", "",
                    .. _flow.Records.Where(r => r.IsReadable && _flow.IsRecordChosen(r)).Select(UndoFlow.Describe),
                    "", _flow.AskWindows ? "Windows will ask for an administrator for the administrator's records." : ""]);
                break;
            case 4:
                _flow.Apply();
                RestoreWizard.Show(_doneText, _flow.DoneLines());
                break;
        }
    }

    /// <summary>One row per record, unwrapped, so the selected row is the record "Set aside" acts on.</summary>
    private void ShowRecords()
    {
        _recordList.SetSource(new ObservableCollection<string>(_flow.RecordLines()));
        _recordList.SelectedItem = 0;
    }

    private void ShowReview()
    {
        _reviewRows.Clear();
        List<string> rows = [];
        foreach (RecordReview record in _flow.Review())
        {
            _reviewRows.Add((record, null, null));
            rows.Add(record.Record.Name);
            foreach (SectionResult part in record.Parts)
            {
                _reviewRows.Add((record, part.Section, null));
                rows.Add($"    {part.Outcome,-9} {Sections.Label(part.Section)}");
                foreach (UndoFile file in record.Files.Where(f => f.Section == part.Section))
                {
                    _reviewRows.Add((record, part.Section, file));
                    rows.Add($"        {State(file.State),-13} {Output.Printable(file.Relative)}");
                }
            }

            foreach (string waiting in record.Waiting)
            {
                _reviewRows.Add((record, waiting, null));
                rows.Add($"    waits     {Sections.Label(waiting)} (a newer record holds it)");
            }
        }

        _reviewList.SetSource(new ObservableCollection<string>(rows));
        _reviewList.SelectedItem = 0;
        ShowReviewed();
    }

    private void ShowReviewed()
    {
        int row = _reviewList.SelectedItem ?? 0;
        if (row < 0 || row >= _reviewRows.Count)
        {
            RestoreWizard.Show(_reviewText, []);
            return;
        }

        (RecordReview record, string? section, UndoFile? file) = _reviewRows[row];
        if (file is not null)
        {
            RestoreWizard.Show(_reviewText, UndoFlow.Diff(file));
        }
        else if (section is not null && record.Parts.FirstOrDefault(p => p.Section == section) is { } part)
        {
            RestoreWizard.Show(_reviewText, [$"{Sections.Label(section)}: {part.Outcome}", Output.Printable(part.Detail)]);
        }
        else
        {
            RestoreWizard.Show(_reviewText, [UndoFlow.Describe(record.Record)]);
        }
    }

    private void ShowChoose()
    {
        _chooseRows.Clear();
        List<string> rows = [];
        List<bool> marks = [];
        foreach (UndoRecord record in _flow.Records.Where(r => r.IsReadable))
        {
            _chooseRows.Add((record, null));
            rows.Add("Record " + UndoFlow.Describe(record));
            marks.Add(_flow.IsRecordChosen(record));
        }

        foreach (string section in _flow.SectionsHeld)
        {
            _chooseRows.Add((null, section));
            rows.Add("Part: " + Sections.Label(section));
            marks.Add(_flow.IsSectionChosen(section));
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
            (UndoRecord? record, string? section) = _chooseRows[i];
            if (record is not null)
            {
                _flow.ChooseRecord(record, marked);
            }
            else if (section is not null)
            {
                _flow.ChooseSection(section, marked);
            }
        }
    }

    private void ShowPreflight()
    {
        RestoreWizard.Show(_preflightText, _flow.PreflightLines().SelectMany(l => new[] { l, "" }));
        _askWindows.Visible = _flow.AdminWaiting && _flow.Elevation.CanAsk;
        _askWindows.Value = _flow.AskWindows ? CheckState.Checked : CheckState.UnChecked;
    }

    private static string State(UndoFileState state) => state switch
    {
        UndoFileState.AsRestored => "as restored",
        UndoFileState.ChangedSince => "changed since",
        UndoFileState.AlreadyBack => "already back",
        UndoFileState.Added => "added",
        _ => "not known"
    };
}
