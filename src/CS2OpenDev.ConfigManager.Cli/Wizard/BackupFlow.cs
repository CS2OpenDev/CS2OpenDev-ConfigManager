using System.Globalization;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>One part as the backup screen lists it: whether this PC has it, and what a backup would save of it.</summary>
internal sealed record BackupPart(string Section, string Label, bool Found, string Detail);

/// <summary>
///     The backup screen's steps without the screen: look at what this PC has, choose what to save and how, save.
///     Saving is <c>acb backup</c>'s own <see cref="BackupBuilder.Create" />; this holds what the person picked.
/// </summary>
internal sealed class BackupFlow
{
    private readonly AcbHost _host;
    private readonly HashSet<string> _unchosen = new(StringComparer.Ordinal);

    public BackupFlow(AcbHost host)
    {
        _host = host;
        OutputPath = Path.Combine(DefaultFolder(), BackupBuilder.DefaultFileName(host.Now().LocalDateTime));
    }

    /// <summary>The Steam folder, or empty to find it.</summary>
    public string SteamPath { get; set; } = "";

    /// <summary>The account to save, or null for the one Steam signed in last.</summary>
    public string? Account { get; set; }

    /// <summary>Accounts with CS2 settings on this PC, the most likely first; empty when Steam was not found.</summary>
    public IReadOnlyList<SteamAccount> Accounts { get; private set; } = [];

    /// <summary>Every part, found here or not, after <see cref="Look" />.</summary>
    public IReadOnlyList<BackupPart> Parts { get; private set; } = [];

    /// <summary>Why nothing can be backed up here; null when something can.</summary>
    public string? Problem { get; private set; }

    /// <summary>Also saves gamestate_integration_*.cfg files, which send live game data to the address they name.</summary>
    public bool Gsi { get; set; }

    public string OutputPath { get; set; }

    /// <summary>The passphrase to seal the backup with; null leaves it unsealed (hash level).</summary>
    public string? Passphrase { get; set; }

    /// <summary>Signs the backup for the person's Steam account through the service, instead of sealing it.</summary>
    public bool Sign { get; set; }

    /// <summary>Why the backup cannot be signed here (no service set, or no root keys in this acb); null when it can.</summary>
    public string? SignProblem => InfoCommands.SignProblem(_host.IdentityService, _host.BrowserSignInProblem);

    /// <summary>Carries this acb in the backup as its restorer.</summary>
    public bool SelfContained { get; set; }

    /// <summary>Why this acb cannot be carried (under a test runner, say); null when it can.</summary>
    public string? SelfContainedProblem => _host.Platform is { } platform ? Restorers.Problem(_host.Executable, platform) : "this PC's platform has no restorer";

    public bool IsChosen(string section) => !_unchosen.Contains(section);

    public void Choose(string section, bool chosen)
    {
        if (chosen)
        {
            _unchosen.Remove(section);
        }
        else
        {
            _unchosen.Add(section);
        }
    }

    /// <summary>The parts found here and chosen: what Save captures.</summary>
    public IReadOnlyList<string> Chosen => [.. Parts.Where(p => p.Found && IsChosen(p.Section)).Select(p => p.Section)];

    /// <summary>
    ///     Looks at what this PC has, part by part, by capturing into a temp folder that is then deleted. Parts not
    ///     found here start unticked. False with <see cref="Problem" /> when nothing could be backed up.
    /// </summary>
    public bool Look()
    {
        Problem = null;
        Parts = [];
        if (_host.Platform is null)
        {
            Problem = "Backups are made on Windows x64 and Linux x64 only.";
            return false;
        }

        try
        {
            SteamInstall? steam = _host.LocateSteam(SteamPath.Length == 0 ? null : Path.GetFullPath(SteamPath));
            Accounts = steam is null ? [] : [.. steam.Accounts()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Accounts = [];
        }

        RunReport report = BackupBuilder.Preview(Options(Sections.All), CancellationToken.None);
        List<BackupPart> parts = [];
        foreach (string section in Sections.All)
        {
            SectionResult? result = report.Results.FirstOrDefault(r => r.Section == section);
            bool found = result?.Outcome == Outcomes.Done;
            parts.Add(new BackupPart(section, Sections.Label(section), found, Output.Printable(result?.Detail ?? "not looked at")));
            if (!found)
            {
                _unchosen.Add(section);
            }
        }

        Parts = parts;
        if (!parts.Any(p => p.Found))
        {
            Problem = "Nothing on this PC can be backed up: " + string.Join("; ", parts.Select(p => p.Detail).Distinct()) + ".";
            return false;
        }

        return true;
    }

    /// <summary>Why the chosen output cannot be written; null when it can.</summary>
    public string? OutputProblem()
    {
        if (Chosen.Count == 0)
        {
            return "Tick at least one part to save.";
        }

        if (OutputPath.Trim().Length == 0)
        {
            return "Pick where to save the backup.";
        }

        if (File.Exists(OutputPath))
        {
            return OutputPath + " already exists; a backup never replaces a file.";
        }

        if (Passphrase is { } passphrase && Seal.PassphraseProblem(passphrase) is { } weak)
        {
            return weak + ".";
        }

        if (Sign && Passphrase is not null)
        {
            return "A backup is sealed or signed, not both.";
        }

        if (Sign && SignProblem is { } unsigned)
        {
            return "The backup cannot be signed: " + unsigned;
        }

        // The screen writes no signed backup whose settings name no account (OtherSigner is null), so say it before signing in.
        if (Sign && OtherSigner is null && !Chosen.Contains(Sections.Cs2) && !Chosen.Contains(Sections.Launch))
        {
            return "A signed backup needs the CS2 settings or the launch options, which name the Steam account it is signed for.";
        }

        return SelfContained && SelfContainedProblem is { } cannot ? "The backup cannot carry this acb: " + cannot + "." : null;
    }

    // Save and Done

    public List<SectionResult> Results { get; } = [];

    /// <summary>Why the backup was not written, when it was not.</summary>
    public string? SaveProblem { get; private set; }

    public int ExitCode { get; private set; } = ExitCodes.Refused;

    public bool Saved { get; private set; }

    /// <summary>
    ///     Asked, with what to say, when the service signs for another account than the one whose settings these are;
    ///     null, as on the screens, writes no such backup and says why.
    /// </summary>
    public Func<string, bool>? OtherSigner { get; init; }

    /// <summary>The Steam account a signed backup is signed for, once saved.</summary>
    public string? Signer { get; private set; }

    /// <summary>
    ///     Captures the chosen parts and writes the backup, as <c>acb backup</c> does. Signing in for a signed backup
    ///     says where to go on <paramref name="words" />, and stops when <paramref name="cancel" /> is cancelled.
    /// </summary>
    public int Save(TextWriter? words = null, CancellationToken cancel = default)
    {
        if (Saved)
        {
            return ExitCode;
        }

        SaveProblem = OutputProblem();
        if (SaveProblem is not null)
        {
            return ExitCode;
        }

        byte[]? restorer = null;
        try
        {
            if (SelfContained)
            {
                restorer = File.ReadAllBytes(_host.Executable!);
            }

            RunReport report = BackupBuilder.Create(Options(Chosen), Path.GetFullPath(OutputPath),
                new ArchiveTool("Advanced Config Backup (acb)", Archives.Version()), _host.Now(), cancel,
                Passphrase is null ? null : SealKey.Create(Passphrase), restorer,
                Sign ? _host.IdentitySigner(_host.IdentityService!, words ?? TextWriter.Null, cancel) : null,
                OtherSigner);
            Results.AddRange(report.Results);
            Saved = BackupBuilder.HoldsAnything(report);
            ExitCode = !Saved ? ExitCodes.Refused : report.Count(Outcomes.Failed) > 0 ? ExitCodes.PartsLeft : ExitCodes.Done;
            SaveProblem = Saved ? null : "Nothing was saved, so no backup was written.";
            if (Sign && Saved)
            {
                Signer = ArchiveReader.Open(Path.GetFullPath(OutputPath), new ArchiveReadOptions { RequirePlatform = null }).Signer;
            }
        }
        catch (IdentityServiceException ex)
        {
            SaveProblem = ex.Message + " No backup was written; save it unsigned, or sealed, to have one now.";
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            SaveProblem = "The backup was stopped, so none was written.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveProblem = "The backup could not be written: " + ex.Message;
        }

        return ExitCode;
    }

    public IReadOnlyList<string> DoneLines()
    {
        List<string> lines = [];
        foreach (SectionResult result in Results)
        {
            lines.Add(Output.Printable($"{result.Outcome,-9} {Sections.Label(result.Section)}: {result.Detail}"));
        }

        lines.Add("");
        if (!Saved)
        {
            lines.Add(SaveProblem ?? "No backup was written.");
            return lines;
        }

        lines.Add("Saved " + Path.GetFullPath(OutputPath));
        lines.Add(Signer is not null
            ? $"Signed for Steam account (SteamID64 {Signer}): anyone can check offline that this account made it and that it is unchanged since."
            : Passphrase is null
                ? "Unsealed: anyone holding it can read it, and could edit it."
                : "Sealed: acb asks for the passphrase before it checks or restores it. Without the passphrase it cannot be opened.");
        if (SelfContained)
        {
            lines.Add("It carries this acb: unpack it beside the zip on the other PC and run restorer/" + ArchiveFormat.RestorerFileName(_host.Platform!) + ".");
        }

        return lines;
    }

    private CaptureOptions Options(IEnumerable<string> sections)
    {
        CaptureOptions options = new()
        {
            SteamRoot = SteamPath.Length == 0 ? null : Path.GetFullPath(SteamPath),
            AccountId = Account,
            IncludeGameStateIntegration = Gsi,
            Creator = "Advanced Config Backup (acb) " + Archives.Version(),
            Machine = _host.Machine
        };
        options.Include.Clear();
        options.Include.AddRange(sections);
        return options;
    }

    /// <summary>Documents, where a person looks for a file they saved; the home folder where there is none.</summary>
    private static string DefaultFolder()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return documents.Length > 0 ? documents : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string Describe(BackupPart part) =>
        string.Create(CultureInfo.InvariantCulture, $"{part.Label}: {part.Detail}");
}
