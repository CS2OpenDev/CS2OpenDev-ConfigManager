using System.Collections.ObjectModel;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>The screens' few colours and the text helpers every screen shares.</summary>
internal static class Ui
{
    public const string Product = "Advanced Config Backup";

    /// <summary>The width text is wrapped to before a pane knows its own.</summary>
    internal const int FallbackWidth = 70;

    /// <summary>Headings and the step being worked on.</summary>
    public static Scheme Accent => Tinted(ColorName16.BrightCyan);

    /// <summary>What is secondary: hints, steps not reached, parts not found.</summary>
    public static Scheme Muted => Tinted(ColorName16.DarkGray);

    /// <summary>Steps done, parts saved.</summary>
    public static Scheme Good => Tinted(ColorName16.BrightGreen);

    /// <summary>A refusal or a problem to fix before going on.</summary>
    public static Scheme Bad => Tinted(ColorName16.BrightRed);

    private static Scheme Tinted(ColorName16 foreground)
    {
        Scheme baseScheme = SchemeManager.GetScheme(Schemes.Base);
        Attribute normal = new(new Color(foreground), baseScheme.Normal.Background);
        return new Scheme(baseScheme) { Normal = normal, Focus = normal, HotNormal = normal, HotFocus = normal, Active = normal, HotActive = normal };
    }

    /// <summary>A one-line label in a colour.</summary>
    public static Label Text(string text, Scheme? scheme = null, int x = 0, Pos? y = null)
    {
        Label label = new() { X = x, Y = y ?? 0, Text = text, Width = Dim.Fill() };
        if (scheme is not null)
        {
            label.SetScheme(scheme);
        }

        return label;
    }

    /// <summary>Text cut to <paramref name="width" /> with an ellipsis when it is longer.</summary>
    internal static string Fit(string text, int width) => text.Length <= width ? text : width <= 1 ? text[..width] : text[..(width - 1)] + "…";

    /// <summary>Two columns, the left as wide as its longest entry up to <paramref name="max" />, longer ones cut.</summary>
    internal static List<string> Columns(IReadOnlyList<(string Left, string Right)> rows, int max = 52)
    {
        int width = rows.Count == 0 ? 0 : Math.Min(max, rows.Max(r => r.Left.Length));
        return [.. rows.Select(r => Fit(r.Left, width).PadRight(width) + "  " + r.Right)];
    }

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

    /// <summary>The lines a list shows, for tests.</summary>
    internal static IReadOnlyList<string> Lines(ListView view) =>
        view.Source is null ? [] : [.. view.Source.ToList().Cast<object?>().Select(o => o?.ToString() ?? "")];
}

/// <summary>
///     Read-only text that wraps to its own width and wraps again when that changes. It scrolls when focused, and
///     shows no selection bar, since there is nothing to select.
/// </summary>
internal sealed class TextPane : ListView
{
    private IReadOnlyList<string> _lines = [];
    private int _wrappedAt;

    public TextPane()
    {
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        ViewportChanged += (_, _) =>
        {
            if (Viewport.Width > 0 && Viewport.Width != _wrappedAt)
            {
                Rewrap();
            }
        };
    }

    /// <summary>The lines as given, before wrapping.</summary>
    public IReadOnlyList<string> Raw => _lines;

    public void SetLines(IEnumerable<string> lines)
    {
        _lines = [.. lines];
        Rewrap();
    }

    private void Rewrap()
    {
        _wrappedAt = Viewport.Width > 0 ? Viewport.Width : Ui.FallbackWidth;
        SetSource(new ObservableCollection<string>(Ui.Wrap(_lines, Math.Max(20, _wrappedAt - 1))));
    }
}
