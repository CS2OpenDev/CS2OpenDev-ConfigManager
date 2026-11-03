using CS2OpenDev.ConfigManager.Archive;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>The few questions asked in a box over the task screen: files and passphrases.</summary>
internal static class Dialogs
{
    /// <summary>A file to open, or null when the person cancels.</summary>
    public static string? Open(IApplication? app, string title)
    {
        if (app is null)
        {
            return null;
        }

        using OpenDialog dialog = new() { Title = title, OpenMode = OpenMode.File, AllowsMultipleSelection = false };
        app.Run(dialog);
        return dialog.Canceled || string.IsNullOrEmpty(dialog.Path) ? null : dialog.Path;
    }

    /// <summary>Where to save, starting from <paramref name="suggested" />; null when the person cancels.</summary>
    public static string? Save(IApplication? app, string title, string suggested)
    {
        if (app is null)
        {
            return null;
        }

        using SaveDialog dialog = new() { Title = title, Path = suggested };
        app.Run(dialog);
        return dialog.Canceled || string.IsNullOrEmpty(dialog.Path) ? null : dialog.Path;
    }

    /// <summary>A sealed backup's passphrase; null when the person gives none. <paramref name="again" /> after one that did not open it.</summary>
    public static string? Passphrase(IApplication? app, bool again)
    {
        if (app is null)
        {
            return null;
        }

        TextField field = new() { X = 1, Y = 2, Width = Dim.Fill(1), Secret = true };
        string? result = null;
        using Dialog dialog = Box(again ? "That passphrase does not open it" : "This backup is sealed", 64, 8);
        dialog.Add(Ui.Text(again ? " Try again, or Cancel to pick another backup." : " Type the passphrase it was sealed with.", y: 0), field);
        Buttons(dialog, ("Open", () =>
        {
            result = field.Text.Length == 0 ? null : field.Text;
            return true;
        }), ("Cancel", () => true));
        field.SetFocus();
        app.Run(dialog);
        return result;
    }

    /// <summary>
    ///     A new passphrase for sealing: typed twice, or six random words shown once to write down. Null when the
    ///     person cancels.
    /// </summary>
    public static string? NewPassphrase(IApplication? app)
    {
        if (app is null)
        {
            return null;
        }

        TextField first = new() { X = 1, Y = 3, Width = Dim.Fill(1), Secret = true };
        TextField second = new() { X = 1, Y = 5, Width = Dim.Fill(1), Secret = true };
        Label problem = Ui.Text("", Ui.Bad, x: 1, y: 7);
        string? result = null;
        using Dialog dialog = Box("Seal with a passphrase", 72, 12);
        dialog.Add(Ui.Text($" At least {Seal.MinimumLength} characters. Anyone holding the backup can guess at it, so a long one matters.", y: 0),
            Ui.Text(" Passphrase", y: 2), first, Ui.Text(" Again", y: 4), second, problem);
        Buttons(dialog, ("Seal", () =>
        {
            string typed = first.Text;
            string? why = typed != second.Text ? "The two differ." : Seal.PassphraseProblem(typed) is { } weak ? weak + "." : null;
            problem.Text = why ?? "";
            result = why is null ? typed : null;
            return why is null;
        }), ("Make one for me", () =>
        {
            string words = Seal.GeneratePhrase();
            first.Text = words;
            second.Text = words;
            first.Secret = false;
            problem.SetScheme(Ui.Accent);
            problem.Text = "Write it down now; it is kept nowhere. Press Seal to use it.";
            return false;
        }), ("Cancel", () => true));
        first.SetFocus();
        app.Run(dialog);
        return result;
    }

    private static Dialog Box(string title, int width, int height) => new() { Title = title, Width = width, Height = height };

    /// <summary>Buttons along the bottom; each returns whether pressing it closes the box.</summary>
    private static void Buttons(Dialog dialog, params (string Text, Func<bool> Pressed)[] buttons)
    {
        View? previous = null;
        foreach ((string text, Func<bool> pressed) in buttons)
        {
            Button button = new() { ShadowStyle = ShadowStyles.None, Text = text, Y = Pos.AnchorEnd(1), X = previous is null ? 1 : Pos.Right(previous) + 1 };
            button.Accepting += (_, e) =>
            {
                if (pressed())
                {
                    dialog.RequestStop();
                }

                e.Handled = true;
            };
            dialog.Add(button);
            previous = button;
        }
    }
}
