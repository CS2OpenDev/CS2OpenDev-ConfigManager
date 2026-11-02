using System.Globalization;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>How a file an undo would put back stands now, against what the restore left there.</summary>
internal enum UndoFileState
{
    /// <summary>As the restore left it.</summary>
    AsRestored,

    /// <summary>Changed since the restore, by CS2, the player or another person on this PC; undo replaces those changes too.</summary>
    ChangedSince,

    /// <summary>Already as the record has it; undo leaves it.</summary>
    AlreadyBack,

    /// <summary>Added by the restore; undo removes it.</summary>
    Added,

    /// <summary>The record does not say what the restore left (an older record), or the part does not run now.</summary>
    Unknown
}

/// <summary>One file in an undo record, as the Review step shows it.</summary>
internal sealed record UndoFile(string Section, string Relative, string? TargetPath, UndoFileState State, string RecordDir);

/// <summary>One undo record with what an undo would do with each of its parts now.</summary>
internal sealed record RecordReview(UndoRecord Record, IReadOnlyList<SectionResult> Parts, IReadOnlyList<string> Waiting, IReadOnlyList<UndoFile> Files);

/// <summary>
///     The undo wizard's steps without the screen: Records, Review, Choose, Pre-flight, Apply, Done. Applying runs
///     <c>acb undo</c> itself (through <see cref="UndoCommand" />), so the hold-back rule, the administrator's
///     store and its prompt are the command's; this holds what the person picked and words it.
/// </summary>
internal sealed class UndoFlow
{
    private readonly AcbHost _host;
    private readonly HashSet<string> _unchosenRecords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unchosenSections = new(StringComparer.Ordinal);

    public UndoFlow(AcbHost host)
    {
        _host = host;
        Elevation = host.Elevation();
    }

    public ElevationState Elevation { get; }

    /// <summary>The Steam folder, or empty for the one each record's restore wrote to.</summary>
    public string SteamPath { get; set; } = "";

    /// <summary>Whether the person asked for Windows' prompt for the administrator's records.</summary>
    public bool AskWindows { get; set; }

    /// <summary>The player's records, newest first; read again by <see cref="Load" />.</summary>
    public IReadOnlyList<UndoRecord> Records { get; private set; } = [];

    /// <summary>Why the store could not be read; null when it could.</summary>
    public string? Problem { get; private set; }

    /// <summary>Whether the administrator's store holds records, which an administrator's run puts back.</summary>
    public bool AdminWaiting { get; private set; }

    public void Load()
    {
        Problem = null;
        try
        {
            Records = _host.UserStore.Records();
            AdminWaiting = _host.MachineStore?.HasRecords() == true;
        }
        catch (UndoStoreUnreachableException ex)
        {
            Records = [];
            Problem = ex.Message;
        }
    }

    public IReadOnlyList<string> RecordLines()
    {
        List<string> lines = [];
        if (Problem is not null)
        {
            lines.Add("The undo store cannot be read: " + Output.Printable(Problem) + ". Nothing was changed.");
            return lines;
        }

        if (Records.Count == 0 && !AdminWaiting)
        {
            lines.Add("Nothing to undo: no restore has changed this PC, or every one is already put back.");
            return lines;
        }

        foreach (UndoRecord record in Records)
        {
            lines.Add(Describe(record));
        }

        if (AdminWaiting)
        {
            lines.Add("Administrator's records (NVIDIA settings), which an administrator's run puts back.");
        }

        return lines;
    }

    /// <summary>A record as one line: its name, when, from which backup, and what it holds, or why it cannot be read.</summary>
    public static string Describe(UndoRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.IsReadable)
        {
            return Output.Printable($"{record.Name}: cannot be read ({record.Problem}); it holds back every older record until it is set aside");
        }

        return Output.Printable($"{record.Name}: {record.Created ?? "unknown time"}, from {record.Manifest?.Get("archive") ?? "an unnamed backup"}: "
                                + string.Join(", ", record.Parts.Select(Sections.Label)));
    }

    /// <summary>Moves an unreadable record aside, as <c>acb undo --set-aside</c> does, so older records go ahead.</summary>
    public string SetAside(UndoRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        (int code, CommandReport? report, string words) = RunUndo(["--set-aside", record.Name, "--yes"]);
        Load();
        return code == ExitCodes.Done ? $"{record.Name} is set aside; what it recorded is not put back." : Output.Printable(report?.Notes.LastOrDefault() ?? words);
    }

    /// <summary>Every readable record with what an undo would do now: a dry run of the undo, which changes nothing.</summary>
    public IReadOnlyList<RecordReview> Review()
    {
        if (Records.Count == 0)
        {
            return [];
        }

        IReadOnlyList<RecordOutcome> outcomes;
        try
        {
            outcomes = UndoRunner.Run(_host.UserStore, Options(dryRun: true), null);
        }
        catch (UndoStoreUnreachableException ex)
        {
            Problem = ex.Message;
            return [];
        }

        List<RecordReview> reviews = [];
        foreach (RecordOutcome outcome in outcomes.Where(o => o.Record.IsReadable && o.Record.Manifest is not null))
        {
            reviews.Add(new RecordReview(outcome.Record, outcome.Report?.Results ?? [], outcome.Waiting, Files(outcome)));
        }

        return reviews;
    }

    /// <summary>What undo would put back in a file: this PC's current contents against the record's copy.</summary>
    public static IReadOnlyList<string> Diff(UndoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string recorded = "";
        string copy = Path.Combine(file.RecordDir, "files", file.Section, file.Relative.Replace('/', Path.DirectorySeparatorChar));
        string here = "";
        try
        {
            if (file.State != UndoFileState.Added)
            {
                recorded = ConfigBackup.Text(File.ReadAllBytes(copy));
            }

            if (file.TargetPath is not null && File.Exists(file.TargetPath))
            {
                here = ConfigBackup.Text(File.ReadAllBytes(file.TargetPath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ["It cannot be read: " + Output.Printable(ex.Message)];
        }

        List<string> lines = [file.State switch
        {
            UndoFileState.ChangedSince => "Changed since the restore. - this PC now   + what undo puts back",
            UndoFileState.Added => "Added by the restore; undo removes it. What it holds now:",
            _ => "- this PC now   + what undo puts back"
        }];
        lines.AddRange(TextDiff.Lines(here, recorded).Select(l => Output.Printable(l)));
        return lines;
    }

    // Choose

    public bool IsRecordChosen(UndoRecord record) => !_unchosenRecords.Contains(record.Name);

    /// <summary>Ticks or unticks a record. An unticked record still holds back an older record's same parts.</summary>
    public void ChooseRecord(UndoRecord record, bool chosen)
    {
        ArgumentNullException.ThrowIfNull(record);
        _ = chosen ? _unchosenRecords.Remove(record.Name) : _unchosenRecords.Add(record.Name);
    }

    public bool IsSectionChosen(string section) => !_unchosenSections.Contains(section);

    public void ChooseSection(string section, bool chosen) =>
        _ = chosen ? _unchosenSections.Remove(section) : _unchosenSections.Add(section);

    /// <summary>The sections the records hold, for ticking.</summary>
    public IReadOnlyList<string> SectionsHeld =>
        [.. Sections.All.Where(s => Records.Any(r => r.IsReadable && r.Parts.Contains(s)))];

    // Pre-flight

    public IReadOnlyList<string> PreflightLines()
    {
        List<string> lines = [];
        HashSet<string> parts = [.. Records.Where(r => r.IsReadable && IsRecordChosen(r)).SelectMany(r => r.Parts).Where(IsSectionChosen)];
        if (parts.Overlaps([Sections.Cs2, Sections.Cs2Game]) && _host.IsRunning("cs2"))
        {
            lines.Add("CS2 is running and writes its settings back when it closes. Close it and press Check again, or go on and those parts wait.");
        }

        if (parts.Contains(Sections.Launch) && _host.IsRunning("steam"))
        {
            lines.Add("Steam is running and writes launch options back when it closes. Close it and press Check again, or go on and that part waits.");
        }

        foreach (UndoRecord record in Records.Where(r => !r.IsReadable))
        {
            lines.Add(Describe(record) + ". Set it aside in Records to let older records go ahead.");
        }

        if (AdminWaiting && _unchosenRecords.Count > 0)
        {
            lines.Add("With a record unticked, the administrator's records wait: they are put back by an undo that leaves none out.");
        }
        else if (AdminWaiting && Elevation.Kind != ElevationKind.Elevated)
        {
            lines.Add(Elevation.CanAsk
                ? $"The administrator's records need an administrator: {Elevation.Detail}. Tick \"Ask Windows\" to be asked; otherwise they wait."
                : $"The administrator's records need an administrator, and {Elevation.Detail}. They wait.");
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing stands in the way.");
        }

        return lines;
    }

    // Apply and Done

    public CommandReport? Result { get; private set; }

    public int ExitCode { get; private set; } = ExitCodes.Refused;

    public bool Applied { get; private set; }

    /// <summary>Puts back what was chosen by running <c>acb undo --yes</c> with those choices.</summary>
    public int Apply()
    {
        if (Applied)
        {
            return ExitCode;
        }

        List<string> args = ["--yes"];
        List<string> ids = [.. Records.Where(r => r.IsReadable && IsRecordChosen(r)).Select(r => r.Name)];
        if (_unchosenRecords.Count > 0)
        {
            // Naming records leaves the administrator's, whose names a player cannot read; they wait for a run that
            // names none (the Pre-flight step says so).
            if (ids.Count == 0)
            {
                Applied = true;
                ExitCode = ExitCodes.Refused;
                Result = new CommandReport { Command = "undo", Notes = { "Nothing was chosen, so nothing was changed." } };
                return ExitCode;
            }
        }

        List<string> sections = [.. Sections.All.Where(IsSectionChosen)];
        if (_unchosenSections.Count > 0)
        {
            args.AddRange(["--only", string.Join(',', sections)]);
        }

        if (_unchosenRecords.Count > 0)
        {
            args.AddRange(["--id", string.Join(',', ids)]);
        }

        if (SteamPath.Length > 0)
        {
            args.AddRange(["--steam-path", SteamPath]);
        }

        if (AskWindows)
        {
            args.Add("--elevate");
        }

        (ExitCode, Result, _) = RunUndo(args);
        Applied = true;
        return ExitCode;
    }

    public IReadOnlyList<string> DoneLines()
    {
        List<string> lines = [];
        foreach (SectionLine line in Result?.Sections ?? [])
        {
            lines.Add(Output.Printable($"{line.Outcome,-9} {line.Label}: {line.Detail}"));
        }

        lines.AddRange((Result?.Notes ?? []).Select(n => Output.Printable(n)));
        return lines;
    }

    private (int Code, CommandReport? Report, string Words) RunUndo(List<string> args)
    {
        StringWriter words = new(CultureInfo.InvariantCulture);
        StringWriter json = new(CultureInfo.InvariantCulture);
        AcbHost quiet = _host.WithWriter(words);
        AcbHost reporting = new()
        {
            Out = json,
            Err = words,
            ReadLine = quiet.ReadLine,
            Interactive = false,
            Platform = quiet.Platform,
            UserStoreRoot = quiet.UserStoreRoot,
            MachineStoreRoot = quiet.MachineStoreRoot,
            MachineStoreParent = quiet.MachineStoreParent,
            IsRunning = quiet.IsRunning,
            LocateSteam = quiet.LocateSteam,
            Elevation = quiet.Elevation,
            Elevator = quiet.Elevator,
            ConnectElevated = quiet.ConnectElevated,
            Now = quiet.Now,
            EnsureMachineStore = quiet.EnsureMachineStore
        };
        int code = UndoCommand.Run([.. args, "--json"], reporting);
        CommandReport? report = null;
        try
        {
            report = JsonSerializer.Deserialize(json.ToString(), CliJson.Default.CommandReport);
        }
        catch (JsonException)
        {
            // The words say what happened.
        }

        return (code, report, words.ToString());
    }

    private UndoRunOptions Options(bool dryRun) => new()
    {
        Only = _unchosenSections.Count == 0 ? null : [.. Sections.All.Where(IsSectionChosen)],
        Ids = _unchosenRecords.Count == 0 ? null : [.. Records.Where(IsRecordChosen).Select(r => r.Name)],
        SteamRoot = SteamPath.Length == 0 ? null : Path.GetFullPath(SteamPath),
        DryRun = dryRun,
        ProcessCheck = _host.IsRunning,
        LocateSteam = _host.LocateSteam
    };

    private static List<UndoFile> Files(RecordOutcome outcome)
    {
        Dictionary<string, string> observed = new(StringComparer.Ordinal);
        foreach (string line in outcome.Report?.Observed ?? [])
        {
            int first = line.IndexOf(' ', StringComparison.Ordinal);
            int last = line.LastIndexOf(' ');
            if (first > 0 && last > first)
            {
                observed[line[..first] + "|" + line[(first + 1)..last]] = line[(last + 1)..];
            }
        }

        List<UndoFile> files = [];
        KvNode manifest = outcome.Record.Manifest!;
        foreach (string section in outcome.Record.Parts.Where(s => s is Sections.Cs2 or Sections.Cs2Game))
        {
            KvNode? part = manifest.Child(section);
            KvNode? wrote = part?.Child(ConfigBackup.WroteKey);
            bool ran = outcome.Report?.Results.Any(r => r.Section == section && r.Outcome == Outcomes.Done) == true;
            foreach (KvNode file in part?.Child("files")?.Children ?? [])
            {
                string native = file.Key.Replace('/', Path.DirectorySeparatorChar);
                KeyValuePair<string, string> hit = observed.FirstOrDefault(o => o.Key.StartsWith(section + "|", StringComparison.Ordinal)
                                                                                 && o.Key.EndsWith(Path.DirectorySeparatorChar + native, StringComparison.Ordinal));
                string? target = hit.Key?[(section.Length + 1)..];
                UndoFileState state;
                if (file.Value == "absent")
                {
                    state = UndoFileState.Added;
                }
                else if (hit.Key is null)
                {
                    // An undo leaves a file that already matches its record, so a part that ran without reaching it is back.
                    state = ran ? UndoFileState.AlreadyBack : UndoFileState.Unknown;
                }
                else if (wrote?.Get(file.Key) is not { } left)
                {
                    state = UndoFileState.Unknown;
                }
                else
                {
                    state = string.Equals(left, hit.Value, StringComparison.OrdinalIgnoreCase) ? UndoFileState.AsRestored : UndoFileState.ChangedSince;
                }

                files.Add(new UndoFile(section, file.Key, target, state, outcome.Record.Dir));
            }
        }

        return files;
    }
}
