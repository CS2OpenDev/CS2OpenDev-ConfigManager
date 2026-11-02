using System.Globalization;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>One section as the Review step shows it: what a restore would do there, and the files it would write.</summary>
internal sealed record SectionReview(string Section, string Label, string Outcome, string Detail, IReadOnlyList<FileChange> Files,
    IReadOnlyList<SettingChange> Settings);

/// <summary>
///     The restore wizard's steps without the screen: Open, Check, Review, Choose, Pre-flight, Apply, Done. Every
///     decision is a <see cref="RestoreSession" />'s, the one <c>acb plan</c> and <c>acb restore</c> use; this only
///     holds what the person picked and words it. Text from the archive is shown escaped, as the commands print it.
/// </summary>
internal sealed class RestoreFlow
{
    private readonly AcbHost _host;
    private readonly HashSet<string> _unchosen = new(StringComparer.Ordinal);
    private readonly HashSet<(string Section, string Relative)> _leftFiles = [];
    private string? _account;

    public RestoreFlow(AcbHost host)
    {
        _host = host;
        Elevation = host.Elevation();
    }

    public ElevationState Elevation { get; }

    // Open

    /// <summary>The backup file to restore.</summary>
    public string ArchivePath { get; set; } = "";

    /// <summary>An organiser's policy file, or empty for none.</summary>
    public string PolicyPath { get; set; } = "";

    /// <summary>The Steam folder, or empty to find it.</summary>
    public string SteamPath { get; set; } = "";

    // Check

    public RestoreSession? Session { get; private set; }

    /// <summary>Why the backup is refused; null once it is open.</summary>
    public string? Refusal { get; private set; }

    /// <summary>The verified archive, also when the policy refused it after verification.</summary>
    public VerifiedArchive? Archive { get; private set; }

    /// <summary>Also writes the backup's gamestate_integration_*.cfg files (--gsi); asked in Choose when it holds any.</summary>
    public bool Gsi { get; private set; }

    /// <summary>Whether the person picked "ask Windows for an administrator" for the NVIDIA settings in Pre-flight.</summary>
    public bool AskWindows { get; set; }

    /// <summary>
    ///     Opens and checks the backup, and previews what a restore would change. False with <see cref="Refusal" />
    ///     when it is refused; the person goes back to Open.
    /// </summary>
    public bool Check()
    {
        _unchosen.Clear();
        _leftFiles.Clear();
        _account = null;
        Gsi = false;
        AskWindows = false;
        return Reopen();
    }

    /// <summary>What Check shows: the backup's level, when and by what it was made, its account and digest, and the policy.</summary>
    public IReadOnlyList<string> CheckLines()
    {
        List<string> lines = [];
        if (Archive is { } archive)
        {
            ArchiveManifest manifest = archive.Manifest;
            lines.Add("Backup:   " + Output.Printable(Path.GetFullPath(ArchivePath)));
            lines.Add("Level:    " + Archives.LevelText(archive.Level));
            lines.Add(Output.Printable(string.Create(CultureInfo.InvariantCulture,
                $"Made:     {manifest.Created:yyyy-MM-dd HH:mm}Z by {manifest.Tool.Name} {manifest.Tool.Version}, on {manifest.Platform}")));
            if (manifest.SteamAccount is not null)
            {
                lines.Add("Account:  Steam account " + Output.Printable(manifest.SteamAccount));
            }

            lines.Add("Digest:   " + archive.ManifestSha256);
        }

        if (Session?.Policy is { } policy)
        {
            lines.Add("Policy:   " + Output.Printable(Path.GetFullPath(PolicyPath)) + " (" + policy.Sha256 + ")");
        }

        if (Refusal is not null)
        {
            lines.Add("");
            lines.Add("Refused: " + Output.Printable(Refusal));
            lines.Add("Nothing was changed. Go back to pick another backup or policy.");
        }

        return lines;
    }

    // Review

    /// <summary>Every section the backup holds, with what a restore would do there now and, for file sections, each file.</summary>
    public IReadOnlyList<SectionReview> Review()
    {
        if (Session is not { } session)
        {
            return [];
        }

        List<SectionReview> sections = [];
        IReadOnlyList<FileChange> files = session.Files();
        foreach (string section in Sections.All)
        {
            if (session.Source.Manifest.Child(section) is null)
            {
                continue;
            }

            (string outcome, string detail) = Planned(session, section);
            sections.Add(new SectionReview(section, Sections.Label(section), outcome, Output.Printable(detail),
                [.. files.Where(f => f.Section == section)], [.. (session.Preview?.Settings ?? []).Where(c => c.Section == section)]));
        }

        return sections;
    }

    /// <summary>A file's change as lines: the target's current contents against the backup's, or the backup's alone for a new file.</summary>
    public IReadOnlyList<string> Diff(FileChange file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string backup = Session?.BackupText(file.Section, file.Relative) ?? "";
        string here = "";
        if (file.TargetPath is not null && file.Kind != FileChangeKind.New)
        {
            try
            {
                here = ConfigBackup.Text(File.ReadAllBytes(file.TargetPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ["The file here cannot be read: " + Output.Printable(ex.Message)];
            }
        }

        List<string> lines = [file.Kind == FileChangeKind.New ? "New on this PC; the backup's copy:" : "- this PC   + the backup"];
        if (file.Relative.EndsWith(ConfigBackup.VideoFile, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("(the lines that name this PC's GPU and monitor keep this PC's values)");
        }

        lines.AddRange(TextDiff.Lines(here, backup).Select(l => Output.Printable(l)));
        return lines;
    }

    /// <summary>A section's settings as a table a person reads: each one's value here and the value it gets.</summary>
    public static IReadOnlyList<string> SettingLines(SectionReview section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section.Settings.Count == 0)
        {
            return [];
        }

        int width = Math.Min(40, section.Settings.Max(c => c.Name.Length));
        List<string> lines = [$"{"Setting".PadRight(width)}  {"Here now",-20}  Gets"];
        foreach (SettingChange change in section.Settings)
        {
            lines.Add(Output.Printable($"{change.Name.PadRight(width)}  {change.Current ?? "(not set)",-20}  {change.Wanted ?? "(not set)"}"));
        }

        return lines;
    }

    // Choose

    /// <summary>The sections a person can tick: the ones the rules let this run (or an administrator's) apply.</summary>
    public IReadOnlyList<string> Choosable { get; private set; } = [];

    public bool IsChosen(string section) => !_unchosen.Contains(section);

    /// <summary>Ticks or unticks a section; the session only ever narrows what its rules let through.</summary>
    public void Choose(string section, bool chosen)
    {
        if (chosen ? _unchosen.Remove(section) : _unchosen.Add(section))
        {
            Reopen();
        }
    }

    public bool IsFileChosen(FileChange file) => !_leftFiles.Contains((file.Section, file.Relative));

    /// <summary>Ticks or unticks a file (with a .vcfg's twin); an unticked file keeps this PC's copy.</summary>
    public void ChooseFile(FileChange file, bool chosen)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (chosen ? _leftFiles.Remove((file.Section, file.Relative)) : _leftFiles.Add((file.Section, file.Relative)))
        {
            Reopen();
        }
    }

    /// <summary>Whether the backup holds game state integration files, which are written only when asked for.</summary>
    public bool HoldsGsi => Session?.Source.Manifest.Child(Sections.Cs2Game)?.Child("files")?.Children.Any(f => ConfigBackup.IsGameStateIntegration(f.Key)) == true;

    public void ChooseGsi(bool gsi)
    {
        if (Gsi != gsi)
        {
            Gsi = gsi;
            Reopen();
        }
    }

    // Pre-flight

    /// <summary>The questions before anything changes, each as a line the person reads.</summary>
    public IReadOnlyList<string> PreflightLines()
    {
        List<string> lines = [];
        if (Session is not { } session)
        {
            return lines;
        }

        IReadOnlyList<string> running = session.RunningWriters();
        if (running.Count > 0)
        {
            lines.Add($"{string.Join(" and ", running)} {(running.Count == 1 ? "is" : "are")} running and writes its settings back when it closes. "
                      + "Close it and press Check again, or go on and those parts are left for later.");
        }

        if (AccountChoice() is { } choice)
        {
            lines.Add($"Your Steam account ({Output.Printable(choice.BackupAccount ?? "unknown")}) has not signed in to Steam on this PC yet. "
                      + "Best: sign in with it, start CS2 once, close it, and restore again. Otherwise your CS2 settings and launch options are left"
                      + (choice.Accounts.Count > 0 ? ", unless you pick an account below that is yours under another number." : "."));
        }

        foreach (SectionResult blocked in (session.Preview?.Results ?? []).Where(r => r.Outcome == Outcomes.Blocked))
        {
            lines.Add($"{Sections.Label(blocked.Section)} cannot be written here: {Output.Printable(blocked.Detail)}. It is left.");
        }

        foreach (string section in session.SteamLeft)
        {
            lines.Add($"{Sections.Label(section)}: {RestoreCommand.SteamLeftDetail(Elevation)}.");
        }

        if (session.AdminSections.Count > 0 && Elevation.Kind != ElevationKind.Elevated)
        {
            string labels = string.Join(", ", session.AdminSections.Select(Sections.Label));
            lines.Add(Elevation.CanAsk
                ? $"{labels} need an administrator: {Elevation.Detail}. Tick \"Ask Windows\" to be asked; otherwise they are left."
                : $"{labels} need an administrator, and {Elevation.Detail}. They are left.");
        }

        if (lines.Count == 0)
        {
            lines.Add("Nothing stands in the way.");
        }

        return lines;
    }

    /// <summary>The account question, when the backup's account has not signed in here and none was picked.</summary>
    public (string? BackupAccount, IList<SteamAccount> Accounts)? AccountChoice() => Session?.AccountChoice();

    /// <summary>Restores the account parts into an account the person picked as theirs.</summary>
    public void UseAccount(string accountId)
    {
        _account = accountId;
        Reopen();
    }

    /// <summary>Checks again, for a person who closed CS2 or Steam.</summary>
    public void Refresh() => Reopen();

    // Apply and Done

    /// <summary>What Apply did, part by part, and its notes; empty before it runs.</summary>
    public List<SectionLine> Results { get; } = [];

    public List<string> Notes { get; } = [];

    /// <summary>The exit code acb returns once the wizard closes; refused until something is applied.</summary>
    public int ExitCode { get; private set; } = ExitCodes.Refused;

    public bool Applied { get; private set; }

    /// <summary>Applies what was reviewed and chosen, the way <c>acb restore</c> does, and keeps what each part did.</summary>
    public int Apply()
    {
        if (Session is not { } session || Applied)
        {
            return ExitCode;
        }

        StringWriter words = new(CultureInfo.InvariantCulture);
        AcbHost quiet = _host.WithWriter(words);
        Output output = new(quiet, "restore", json: false, reportPath: null);
        ExitCode = RestoreCommand.ApplyAll(session, quiet, output, Elevation, _ => AskWindows, yes: false, out _);
        Applied = true;
        Results.AddRange(output.Report.Sections);
        Notes.AddRange(output.Report.Notes);
        return ExitCode;
    }

    /// <summary>What Done shows: each part's outcome, and how to put the PC back.</summary>
    public IReadOnlyList<string> DoneLines()
    {
        List<string> lines = [];
        foreach (SectionLine line in Results)
        {
            lines.Add(Output.Printable($"{line.Outcome,-9} {line.Label}: {line.Detail}"));
        }

        lines.AddRange(Notes.Select(n => Output.Printable(n)));
        lines.Add("");
        lines.Add(ExitCode switch
        {
            ExitCodes.Done => "Done. To put this PC back as it was, run: acb undo",
            ExitCodes.Refused => "Nothing was changed.",
            ExitCodes.RecordsNeedAttention => "Finished, but something needs attention (see above). To put this PC back as it was, run: acb undo",
            _ => "Finished, with parts left as said above. To put this PC back as it was, run: acb undo"
        });
        return lines;
    }

    private static (string Outcome, string Detail) Planned(RestoreSession session, string section)
    {
        if (session.PolicyLeft.Contains(section))
        {
            return (Outcomes.Skipped, RestoreSession.PolicyLeftDetail);
        }

        if (session.SteamLeft.Contains(section))
        {
            return (Outcomes.Deferred, "left: an administrator's acb writes nothing through the Steam folder");
        }

        if (session.Preview?.Results.FirstOrDefault(r => r.Section == section) is { } result)
        {
            return (result.Outcome, result.Detail);
        }

        return (Outcomes.Skipped, "not chosen");
    }

    /// <summary>Opens the session again with what the person picked, so every change goes through the same rules, and previews it.</summary>
    private bool Reopen()
    {
        Refusal = null;
        Archive = null;
        Session = null;
        if (ArchivePath.Length == 0)
        {
            Refusal = "pick a backup file first.";
            return false;
        }

        RestoreRequest request = new(ArchivePath, null, _account, SteamPath.Length == 0 ? null : Path.GetFullPath(SteamPath),
            PolicyPath.Length == 0 ? null : Path.GetFullPath(PolicyPath), Gsi);
        try
        {
            Session = RestoreSession.Open(request, RestoreCommand.Environment(_host, Elevation));
        }
        catch (RestoreRefusedException ex)
        {
            Archive = ex.Archive;
            Refusal = ex.Message;
            return false;
        }

        Archive = Session.Archive;
        Choosable = Session.Restorable;

        Session.Choose([.. Session.Restorable.Where(s => !_unchosen.Contains(s))]);
        foreach ((string section, string relative) in _leftFiles)
        {
            Session.LeaveFile(section, relative, leave: true);
        }

        Session.PreviewChanges();
        return true;
    }
}
