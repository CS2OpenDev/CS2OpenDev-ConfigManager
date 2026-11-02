namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>A line diff for showing a person what a restore would change in a text file.</summary>
internal static class TextDiff
{
    /// <summary>Above this many line pairs the files are only said to differ, so a large file cannot stall the screen.</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>
    ///     The lines of <paramref name="before" /> and <paramref name="after" /> as "  " (kept), "- " (removed) and
    ///     "+ " (added), with runs of kept lines beyond <paramref name="context" /> of a change folded into one line.
    /// </summary>
    public static IReadOnlyList<string> Lines(string before, string after, int context = 2)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        string[] a = Split(before);
        string[] b = Split(after);
        if ((long)a.Length * b.Length > MaxCells)
        {
            return [$"The files differ ({a.Length} lines here, {b.Length} in the backup); they are too long to compare line by line."];
        }

        // Longest common subsequence, from the end, so the walk below can go forwards.
        int[,] common = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
        {
            for (int j = b.Length - 1; j >= 0; j--)
            {
                common[i, j] = a[i] == b[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
            }
        }

        List<(char Mark, string Text)> all = [];
        int x = 0;
        int y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y])
            {
                all.Add((' ', a[x++]));
                y++;
            }
            else if (y < b.Length && (x == a.Length || common[x, y + 1] >= common[x + 1, y]))
            {
                all.Add(('+', b[y++]));
            }
            else
            {
                all.Add(('-', a[x++]));
            }
        }

        if (all.TrueForAll(l => l.Mark == ' '))
        {
            return ["The file here already matches the backup's."];
        }

        List<string> shown = [];
        int folded = 0;
        for (int i = 0; i < all.Count; i++)
        {
            bool near = false;
            for (int k = Math.Max(0, i - context); k <= Math.Min(all.Count - 1, i + context) && !near; k++)
            {
                near = all[k].Mark != ' ';
            }

            if (!near)
            {
                folded++;
                continue;
            }

            if (folded > 0)
            {
                shown.Add($"  ... {folded} unchanged line{(folded == 1 ? "" : "s")}");
                folded = 0;
            }

            shown.Add(all[i].Mark + " " + all[i].Text);
        }

        if (folded > 0)
        {
            shown.Add($"  ... {folded} unchanged line{(folded == 1 ? "" : "s")}");
        }

        return shown;
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
}
