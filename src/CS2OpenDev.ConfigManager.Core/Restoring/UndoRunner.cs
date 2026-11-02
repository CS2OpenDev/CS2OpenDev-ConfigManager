using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>What to do with a record that cannot be read.</summary>
public enum UnreadableChoice
{
    /// <summary>Keep it where it is; it holds back every older record's parts. The safe default.</summary>
    Leave,

    /// <summary>Read it again (the disk or a scanner may have let go).</summary>
    Retry,

    /// <summary>Move it into set-aside/, so older records go ahead. What it recorded is then not put back.</summary>
    SetAside
}

public sealed record UndoRunOptions
{
    /// <summary>Sections to put back; null for every one.</summary>
    public ICollection<string>? Only { get; init; }

    /// <summary>Records to apply, by name or sequence; null for every one. Records not named still hold back older ones.</summary>
    public ICollection<string>? Ids { get; init; }

    public string? SteamRoot { get; init; }

    public bool DryRun { get; init; }

    public Func<string, bool>? ProcessCheck { get; init; }

    /// <summary>Asked about each unreadable record; null leaves every one, as a headless run does.</summary>
    public Func<UndoRecord, UnreadableChoice>? OnUnreadable { get; init; }
}

/// <summary>How one record went in an undo run.</summary>
public sealed class RecordOutcome
{
    internal RecordOutcome(UndoRecord record) => Record = record;

    public UndoRecord Record { get; internal set; }

    /// <summary>The engine's report, or null when the record was not applied (unreadable, not chosen, set aside).</summary>
    public RunReport? Report { get; internal set; }

    /// <summary>Why the record cannot be read; it was left in place and holds back older records.</summary>
    public string? Unreadable { get; internal set; }

    public bool SetAside { get; internal set; }

    /// <summary>Parts this record still holds that waited for a newer record.</summary>
    public IReadOnlyList<string> Waiting { get; internal set; } = [];

    /// <summary>Parts still in the record after the run.</summary>
    public IReadOnlyList<string> Left { get; internal set; } = [];

    /// <summary>Moved into applied/, every part put back.</summary>
    public bool Filed { get; internal set; }

    /// <summary>Why a finished record could not be moved into applied/; the next run files it.</summary>
    public string? FileProblem { get; internal set; }
}

/// <summary>
///     Puts back what restores changed, from one store, newest record first. A part an older record holds waits
///     while a newer record still holds the same part (it failed, was left for later, was not chosen, or the newer
///     record cannot be read): the older record is what the PC had first, so it goes back last.
/// </summary>
public static class UndoRunner
{
    public static IReadOnlyList<RecordOutcome> Run(UndoStore store, UndoRunOptions options, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        List<RecordOutcome> outcomes = [];
        HashSet<string> heldBack = new(StringComparer.Ordinal);
        foreach (UndoRecord found in store.Records())
        {
            UndoRecord record = found;
            RecordOutcome outcome = new(record);
            outcomes.Add(outcome);
            while (!record.IsReadable)
            {
                UnreadableChoice choice = options.DryRun || options.OnUnreadable is null ? UnreadableChoice.Leave : options.OnUnreadable(record);
                if (choice == UnreadableChoice.Retry)
                {
                    record = store.Find(record.Name) ?? record;
                    outcome.Record = record;
                    continue;
                }

                if (choice == UnreadableChoice.SetAside)
                {
                    store.SetAside(record);
                    outcome.SetAside = true;
                }

                break;
            }

            if (outcome.SetAside)
            {
                continue;
            }

            if (!record.IsReadable)
            {
                // What it holds is unknown, so every part waits for it.
                outcome.Unreadable = record.Problem;
                heldBack.UnionWith(Sections.All);
                continue;
            }

            bool chosen = options.Ids is null || options.Ids.Any(id => id == record.Name || (int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n == record.Sequence));
            if (!chosen)
            {
                outcome.Left = record.Parts;
                heldBack.UnionWith(record.Parts);
                continue;
            }

            if (record.Manifest is null || record.Parts.Count == 0)
            {
                // A record that changed nothing, or whose every part is back but was not filed last time.
                FileQuietly(store, outcome, options.DryRun);
                continue;
            }

            IEnumerable<string> wanted = options.Only ?? Sections.All;
            List<string> only = [.. wanted.Where(s => !heldBack.Contains(s))];
            RestoreOptions restoreOptions = new()
            {
                Only = only,
                SteamRoot = options.SteamRoot,
                WriteUndo = false,
                DryRun = options.DryRun,
                ProcessCheck = options.ProcessCheck
            };
            RunReport report = ConfigBackup.Restore(new FolderSource(record.Dir, record.Manifest), RestoreMode.Undo, restoreOptions, log);
            outcome.Report = report;
            outcome.Left = report.PendingParts;
            outcome.Waiting = [.. outcome.Left.Where(heldBack.Contains)];
            heldBack.UnionWith(outcome.Left);
            if (outcome.Left.Count == 0)
            {
                FileQuietly(store, outcome, options.DryRun);
            }
        }

        return outcomes;
    }

    /// <summary>A record left in the store with nothing in it is filed on the next run; a failure to move it is reported, never fatal.</summary>
    private static void FileQuietly(UndoStore store, RecordOutcome outcome, bool dryRun)
    {
        if (dryRun)
        {
            return;
        }

        try
        {
            store.FileAway(outcome.Record);
            outcome.Filed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome.FileProblem = ex.Message;
        }
    }
}
