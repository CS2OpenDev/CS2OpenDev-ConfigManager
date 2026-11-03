using System.Collections.ObjectModel;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>
///     Putting the PC back, on the task screen: Records (what each restore left), Review (what an undo would put
///     back, and what changed since), Choose, Ready, Done. <see cref="UndoFlow" /> holds the state, and applying is
///     <c>acb undo</c>'s own run.
/// </summary>
internal sealed class UndoScreen : IDisposable
{
    private readonly UndoFlow _flow;
    private readonly ListView _records = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(3) };
    private readonly Button _setAside = new() { ShadowStyle = ShadowStyles.None, X = 0, Y = Pos.AnchorEnd(2), Text = "Set aside the selected record" };
    private readonly Label _setAsideNote = Ui.Text("", Ui.Muted, y: Pos.AnchorEnd(1));
    private readonly ListView _review = new() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Percent(45) };
    private readonly TextPane _reviewDetail = new();
    private readonly ListView _choose = new() { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(), ShowMarks = true, MarkMultiple = true };
    private readonly TextPane _ready = new();
    private readonly CheckBox _askWindows = new() { X = 0, Y = Pos.AnchorEnd(3), Text = "Ask Windows for an administrator for the administrator's records" };
    private readonly Button _checkAgain = new() { ShadowStyle = ShadowStyles.None, X = 0, Y = Pos.AnchorEnd(1), Text = "Check again" };
    private readonly TextPane _done = new();
    private readonly List<(RecordReview Record, string? Section, UndoFile? File)> _reviewRows = [];
    private readonly List<(UndoRecord? Record, string? Section)> _chooseRows = [];

    public UndoScreen(UndoFlow flow)
    {
        _flow = flow;
        _setAside.Accepting += (_, e) =>
        {
            int row = _records.SelectedItem ?? -1;
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
        View records = new() { CanFocus = true };
        records.Add(_records, _setAside, _setAsideNote);

        _reviewDetail.Y = Pos.Bottom(_review) + 1;
        _reviewDetail.Height = Dim.Fill();
        _review.ValueChanged += (_, _) => ShowReviewed();
        View review = new() { CanFocus = true };
        review.Add(_review, new Line { Y = Pos.Bottom(_review), Width = Dim.Fill() }, _reviewDetail);

        View choose = new() { CanFocus = true };
        choose.Add(Ui.Text("Space ticks or unticks. An unticked record still holds back older records' same parts.", Ui.Muted), _choose);

        _ready.Height = Dim.Fill(4);
        _checkAgain.Accepting += (_, e) =>
        {
            ShowReady();
            e.Handled = true;
        };
        View ready = new() { CanFocus = true };
        ready.Add(_ready, _askWindows, _checkAgain);

        Shell = new TaskShell("Put this PC back",
        [
            new TaskStep { Title = "Records", Body = records, Enter = ShowRecords, Leave = LeaveRecords },
            new TaskStep { Title = "Review", Body = review, Enter = ShowReview },
            new TaskStep { Title = "Choose", Body = choose, Enter = ShowChoose, Leave = TakeChoices },
            new TaskStep { Title = "Ready", Body = ready, Enter = ShowReady, Leave = Apply, NextText = "Put back", Commits = true },
            new TaskStep { Title = "Done", Body = _done, Enter = () => _done.SetLines(_flow.DoneLines()), NextText = "Finish" }
        ], Summary);
    }

    public TaskShell Shell { get; }

    public void Dispose() => Shell.Dispose();

    private IEnumerable<string> Summary() =>
        _flow.Records.Count == 0 ? [] : [_flow.Records.Count == 1 ? "1 record" : _flow.Records.Count + " records",
            .. _flow.SectionsHeld.Where(_flow.IsSectionChosen).Select(s => "• " + Sections.Label(s))];

    private void ShowRecords()
    {
        _flow.Load();
        _records.SetSource(new ObservableCollection<string>(_flow.Problem is { } problem ? ["The undo records cannot be read: " + problem] : _flow.RecordLines()));
        _records.SelectedItem = 0;
    }

    private string? LeaveRecords() =>
        _flow.Problem is { } problem ? "The undo records cannot be read: " + problem
        : _flow.Records.Count == 0 && !_flow.AdminWaiting ? "There is nothing to put back."
        : null;

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

        _review.SetSource(new ObservableCollection<string>(rows));
        _review.SelectedItem = 0;
        ShowReviewed();
    }

    private void ShowReviewed()
    {
        int row = _review.SelectedItem ?? 0;
        if (row < 0 || row >= _reviewRows.Count)
        {
            _reviewDetail.SetLines([]);
            return;
        }

        (RecordReview record, string? section, UndoFile? file) = _reviewRows[row];
        if (file is not null)
        {
            _reviewDetail.SetLines(UndoFlow.Diff(file));
        }
        else if (section is not null && record.Parts.FirstOrDefault(p => p.Section == section) is { } part)
        {
            _reviewDetail.SetLines([$"{Sections.Label(section)}: {part.Outcome}", Output.Printable(part.Detail)]);
        }
        else
        {
            _reviewDetail.SetLines([UndoFlow.Describe(record.Record)]);
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

        _choose.SetSource(new ObservableCollection<string>(rows));
        for (int i = 0; i < marks.Count; i++)
        {
            _choose.Source!.SetMark(i, marks[i]);
        }

        _choose.SelectedItem = 0;
    }

    private string? TakeChoices()
    {
        for (int i = 0; i < _chooseRows.Count; i++)
        {
            bool marked = _choose.Source!.IsMarked(i);
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

        return null;
    }

    private void ShowReady()
    {
        List<string> lines = [.. _flow.PreflightLines().SelectMany(l => new[] { l, "" }), "Pressing Put back puts back what was chosen, newest record first:"];
        lines.AddRange(_flow.Records.Where(r => r.IsReadable && _flow.IsRecordChosen(r)).Select(r => "  " + UndoFlow.Describe(r)));
        _ready.SetLines(lines);
        _askWindows.Visible = _flow.AdminWaiting && _flow.Elevation.CanAsk;
        _askWindows.Value = _flow.AskWindows ? CheckState.Checked : CheckState.UnChecked;
    }

    private string? Apply()
    {
        _flow.AskWindows = _askWindows.Visible && _askWindows.Value == CheckState.Checked;
        _flow.Apply();
        return null;
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
