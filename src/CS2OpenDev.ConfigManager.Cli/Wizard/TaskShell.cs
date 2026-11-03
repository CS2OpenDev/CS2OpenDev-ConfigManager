using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>One step of a task: its body, what showing it refreshes, and what moving on from it does.</summary>
internal sealed class TaskStep
{
    public required string Title { get; init; }

    public required View Body { get; init; }

    /// <summary>Refreshes the body each time the step is shown.</summary>
    public Action? Enter { get; init; }

    /// <summary>Runs on Next. Null to move on; otherwise the step stays and this is said under it.</summary>
    public Func<string?>? Leave { get; init; }

    public string NextText { get; init; } = "Next";

    /// <summary>Moving on from this step changes the PC or writes a file, so there is no going back past it.</summary>
    public bool Commits { get; init; }
}

/// <summary>
///     The screen every task uses: the steps down the left with where the person is, a short summary of what is
///     picked under them, the step's body on the right with Back and Next under it, and the keys along the bottom.
///     Esc goes back a step (from the first, home); Ctrl+Q quits acb.
/// </summary>
internal sealed class TaskShell : IDisposable
{
    internal const int RailWidth = 26;

    private readonly IReadOnlyList<TaskStep> _steps;
    private readonly Func<IEnumerable<string>> _summary;
    private readonly List<Label> _railSteps = [];
    private readonly TextPane _railSummary = new() { CanFocus = false };
    private readonly FrameView _content;
    private readonly Label _notice = new() { X = 0, Width = Dim.Fill(), Text = "" };
    private readonly Button _back = new() { ShadowStyle = ShadowStyles.None, X = 0, Text = "Back" };
    private readonly Button _next = new() { ShadowStyle = ShadowStyles.None, Text = "Next", IsDefault = true };
    private bool _committed;

    public TaskShell(string task, IReadOnlyList<TaskStep> steps, Func<IEnumerable<string>> summary)
    {
        _steps = steps;
        _summary = summary;
        Window = new Window { Title = "", Width = Dim.Fill(), Height = Dim.Fill(), BorderStyle = LineStyle.None };

        Window.Add(Ui.Text(" " + Ui.Product, Ui.Accent), new Label { X = Pos.AnchorEnd(), Y = 0, Text = "acb " + Archives.Version() + " " });

        FrameView rail = new() { X = 0, Y = 1, Width = RailWidth, Height = Dim.Fill(1), Title = task, CanFocus = false };
        for (int i = 0; i < steps.Count; i++)
        {
            Label step = new() { X = 1, Y = i + 1, Width = Dim.Fill() };
            _railSteps.Add(step);
            rail.Add(step);
        }

        _railSummary.Y = steps.Count + 3;
        _railSummary.Height = Dim.Fill();
        rail.Add(_railSummary);

        _content = new FrameView { X = RailWidth, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        _notice.Y = Pos.AnchorEnd(2);
        _notice.SetScheme(Ui.Bad);
        _back.Y = Pos.AnchorEnd(1);
        _next.X = Pos.AnchorEnd();
        _next.Y = Pos.AnchorEnd(1);
        _back.Accepting += (_, e) =>
        {
            Back();
            e.Handled = true;
        };
        _next.Accepting += (_, e) =>
        {
            Next();
            e.Handled = true;
        };

        Window.Add(rail, _content,
            Ui.Text(" Tab moves   Space ticks   Enter presses   Esc back   Ctrl+Q quits", Ui.Muted, y: Pos.AnchorEnd(1)));
        Window.KeyDown += (_, key) =>
        {
            if (key == Key.Esc)
            {
                Back();
                key.Handled = true;
            }
            else if (key == Key.Q.WithCtrl)
            {
                Quit = true;
                Window.RequestStop();
                key.Handled = true;
            }
        };

        Show(0);
    }

    public Window Window { get; }

    /// <summary>The step shown, from 0.</summary>
    public int Index { get; private set; }

    public IReadOnlyList<TaskStep> Steps => _steps;

    /// <summary>The person asked to quit acb, not just this task.</summary>
    public bool Quit { get; private set; }

    /// <summary>What is said under the step: why it did not move on.</summary>
    public string Notice => _notice.Text;

    /// <summary>Does the step's work and moves on; on the last step, closes the task. False when the step stays.</summary>
    public bool Next()
    {
        TaskStep step = _steps[Index];
        string? problem = step.Leave?.Invoke();
        if (problem is not null)
        {
            _notice.Text = problem;
            return false;
        }

        _committed |= step.Commits;
        if (Index == _steps.Count - 1)
        {
            Window.RequestStop();
            return true;
        }

        Show(Index + 1);
        return true;
    }

    /// <summary>Goes back a step, or home from the first. False once the task has changed something.</summary>
    public bool Back()
    {
        if (_committed)
        {
            return false;
        }

        if (Index == 0)
        {
            Window.RequestStop();
            return true;
        }

        Show(Index - 1);
        return true;
    }

    /// <summary>Redraws the rail's summary, after a step picked something.</summary>
    public void Refresh() => _railSummary.SetLines(_summary().Select(line => Ui.Fit(line, RailWidth - 3)));

    public void Dispose() => Window.Dispose();

    private void Show(int index)
    {
        _content.RemoveAll();
        Index = index;
        TaskStep step = _steps[index];
        _notice.Text = "";
        step.Enter?.Invoke();
        step.Body.X = 0;
        step.Body.Y = 0;
        step.Body.Width = Dim.Fill();
        step.Body.Height = Dim.Fill(3);
        _content.Title = step.Title;
        _content.Add(step.Body, _notice, _back, _next);
        _next.Text = step.NextText;
        _back.Visible = !_committed;
        _back.Text = index == 0 ? "Home" : "Back";
        for (int i = 0; i < _steps.Count; i++)
        {
            Label label = _railSteps[i];
            label.Text = (i < index ? "✓ " : i == index ? "▶ " : "  ") + (i + 1) + " " + _steps[i].Title;
            label.SetScheme(i < index ? Ui.Good : i == index ? Ui.Accent : Ui.Muted);
        }

        Refresh();
        step.Body.SetFocus();
    }
}
