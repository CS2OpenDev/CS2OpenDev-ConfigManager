using System.Text;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>What to restore. <paramref name="Gsi" /> also writes the backup's gamestate_integration_*.cfg files.</summary>
internal sealed record RestoreRequest(string Archive, IReadOnlyList<string>? Only, string? Account, string? SteamPath, string? Policy, bool Gsi);

/// <summary>What a restore reaches on the PC, apart from the files and settings the engine writes.</summary>
internal sealed class RestoreEnvironment
{
    /// <summary>The platform as archives name it, or null for one nothing is restored on.</summary>
    public required string? Platform { get; init; }

    public required Func<string, bool> IsRunning { get; init; }

    /// <summary>Finds Steam from a folder, or on the PC when given null.</summary>
    public required Func<string?, SteamInstall?> LocateSteam { get; init; }

    /// <summary>The restore runs as an administrator, so it writes nothing through the Steam folder.</summary>
    public required bool AsAdministrator { get; init; }

    /// <summary>Called with the record's folder just before a failed part is rolled back. Tests only.</summary>
    public Action<string>? BeforeRollback { get; init; }

    /// <summary>Starts NVIDIA's driver API for the engine; null starts the real one. Tests only.</summary>
    public NvidiaStarter? StartNvidia { get; init; }

    /// <summary>How long the engine waits for an NVIDIA driver part. Tests only.</summary>
    public int DriverTimeoutMs { get; init; } = ConfigBackup.DriverPartTimeoutMs;
}

/// <summary>A restore refused before anything changed, with the archive when it was verified, so its header can still be shown.</summary>
internal sealed class RestoreRefusedException(string message, VerifiedArchive? archive = null) : Exception(message)
{
    public VerifiedArchive? Archive { get; } = archive;
}

/// <summary>
///     One file a restore would write, for a person to review: where it lands, and whether that file is new there,
///     differs from the backup's, or already matches it. <see cref="TargetPath" /> is null when the target is not
///     known (CS2 running, account not signed in).
/// </summary>
internal sealed record FileChange(string Section, string Relative, string ChoiceKey, string? TargetPath, FileChangeKind Kind);

internal enum FileChangeKind
{
    /// <summary>Not on this PC yet.</summary>
    New,

    /// <summary>On this PC with other contents.</summary>
    Changed,

    /// <summary>On this PC with the backup's contents already.</summary>
    Same,

    /// <summary>The preview did not reach it (the part is left, or the person chose to leave the file).</summary>
    NotReached
}

/// <summary>What a reviewed plan pinned, for checking that applying it now would do what was reviewed.</summary>
internal sealed record PlanPins(string? PolicySha256, IReadOnlyCollection<string> LeftByAdministrator, string Fingerprint);

/// <summary>
///     One restore, from the archive to the records: verification, the organiser's policy, which sections this run
///     applies and which an administrator's run does, the preview and its fingerprint, and the engine run into an
///     undo store. <c>acb plan</c>, <c>acb restore</c>, <c>restore --plan</c>, the administrator's run and the wizard
///     all go through it, so none of them can skip a rule. It decides; the caller words, asks and exits.
/// </summary>
internal sealed class RestoreSession
{
    public const string PolicyLeftDetail = "left alone by the organiser's policy";

    private readonly RestoreEnvironment _environment;

    private RestoreSession(RestoreEnvironment environment, RestoreRequest request, VerifiedArchive archive, ArchiveSource source, OrganiserPolicy? policy)
    {
        _environment = environment;
        Request = request;
        Archive = archive;
        Source = source;
        Policy = policy;
    }

    public RestoreRequest Request { get; private set; }

    public VerifiedArchive Archive { get; }

    public ArchiveSource Source { get; }

    public OrganiserPolicy? Policy { get; }

    /// <summary>Sections this process applies, into its own undo store.</summary>
    public List<string> PlayerSections { get; } = [];

    /// <summary>Sections only an administrator's run applies, into the administrator's undo store.</summary>
    public List<string> AdminSections { get; } = [];

    /// <summary>Sections the archive holds that the organiser's policy leaves alone.</summary>
    public List<string> PolicyLeft { get; } = [];

    /// <summary>Sections an administrator's restore leaves, as they are written through the Steam folder.</summary>
    public List<string> SteamLeft { get; } = [];

    /// <summary>Administrator's sections the preview found nothing to do for on this PC (no NVIDIA driver or GPU).</summary>
    public List<SectionResult> NotHere { get; } = [];

    /// <summary>Files the person chose to leave, by <see cref="ConfigBackup.FileChoiceKey" />.</summary>
    public IReadOnlyCollection<string> LeftFiles => _leaveFiles;

    private readonly HashSet<string> _leaveFiles = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The dry run over every section this restore would apply; null before <see cref="PreviewChanges" />.</summary>
    public RunReport? Preview { get; private set; }

    /// <summary>One value for everything the preview saw; null before <see cref="PreviewChanges" />.</summary>
    public string? Fingerprint { get; private set; }

    /// <summary>
    ///     Verifies the archive and applies the policy, and sorts its sections. With <paramref name="archiveBytes" />
    ///     (the administrator's run) the backup and policy are read from those bytes, and their paths are only named.
    ///     Throws <see cref="RestoreRefusedException" /> when the restore is refused.
    /// </summary>
    public static RestoreSession Open(RestoreRequest request, RestoreEnvironment environment, byte[]? archiveBytes = null, byte[]? policyBytes = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.Platform is null)
        {
            throw new RestoreRefusedException("acb restores only on Windows x64 and Linux x64.");
        }

        string? policyProblem;
        OrganiserPolicy? policy = policyBytes is null
            ? LoadPolicy(archiveBytes is null ? request.Policy : null, out policyProblem)
            : LoadPolicy(policyBytes, out policyProblem);
        if (policyProblem is not null)
        {
            throw new RestoreRefusedException(policyProblem);
        }

        TrustLevel minimum = policy?.MinimumLevel ?? TrustLevel.Hash;
        string? problem;
        VerifiedArchive archive = (archiveBytes is null
                                      ? OpenArchive(request.Archive, environment.Platform, minimum, out problem)
                                      : OpenArchive(archiveBytes, environment.Platform, minimum, out problem))
                                  ?? throw new RestoreRefusedException(problem!);
        if (policy?.ArchiveProblem(archive) is { } policyRefusal)
        {
            throw new RestoreRefusedException(policyRefusal, archive);
        }

        ArchiveSource source;
        try
        {
            source = new ArchiveSource(archive);
        }
        catch (ArchiveRejectedException ex)
        {
            throw new RestoreRefusedException(ex.Message, archive);
        }

        if (policy is { RequireCs2Closed: true } && environment.IsRunning("cs2"))
        {
            throw new RestoreRefusedException("the organiser's policy restores only while CS2 is closed, and it is running. Close CS2 and run this again.", archive);
        }

        RestoreSession session = new(environment, request, archive, source, policy);
        foreach (string section in Sections.All)
        {
            if (source.Manifest.Child(section) is null || (request.Only is not null && !request.Only.Contains(section)))
            {
                continue;
            }

            if (policy is not null && !policy.Allows(section))
            {
                session.PolicyLeft.Add(section);
            }
            else if (RestoreRules.NeedsAdministrator(section))
            {
                session.AdminSections.Add(section);
            }
            else if (environment.AsAdministrator && RestoreRules.LeftByAdministrator(section))
            {
                session.SteamLeft.Add(section);
            }
            else
            {
                session.PlayerSections.Add(section);
            }
        }

        return session;
    }

    /// <summary>The sections the archive holds that this restore could apply, in the order the engine runs them.</summary>
    public IReadOnlyList<string> Restorable => [.. PlayerSections, .. AdminSections];

    /// <summary>Narrows the restore to <paramref name="sections" />; it can never add one the rules left out.</summary>
    public void Choose(IReadOnlyCollection<string> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        PlayerSections.RemoveAll(s => !sections.Contains(s));
        AdminSections.RemoveAll(s => !sections.Contains(s));
        Preview = null;
        Fingerprint = null;
    }

    /// <summary>Leaves one file (and a .vcfg's twin) as the target has it, or takes it again. Only narrows: a file the rules leave stays left.</summary>
    public void LeaveFile(string section, string relative, bool leave)
    {
        string key = ConfigBackup.FileChoiceKey(section, relative);
        if (leave ? _leaveFiles.Add(key) : _leaveFiles.Remove(key))
        {
            Preview = null;
            Fingerprint = null;
        }
    }

    /// <summary>
    ///     The files each chosen file section would write, with what the preview found at each target. Twins are
    ///     listed once, by their .vcfg.
    /// </summary>
    public IReadOnlyList<FileChange> Files()
    {
        Dictionary<string, string> observed = new(StringComparer.Ordinal);
        foreach (string line in Preview?.Observed ?? [])
        {
            // "section path sha" or "section path absent"; the path may hold spaces.
            int first = line.IndexOf(' ', StringComparison.Ordinal);
            int last = line.LastIndexOf(' ');
            if (first > 0 && last > first)
            {
                observed[line[..first] + "|" + line[(first + 1)..last]] = line[(last + 1)..];
            }
        }

        List<FileChange> files = [];
        HashSet<string> listed = new(StringComparer.Ordinal);
        foreach (string section in PlayerSections.Where(s => s is Sections.Cs2 or Sections.Cs2Game))
        {
            foreach (KvNode file in Source.Manifest.Child(section)?.Child("files")?.Children ?? [])
            {
                string key = ConfigBackup.FileChoiceKey(section, file.Key);
                if (!listed.Add(key))
                {
                    continue;
                }

                string relative = key[(section.Length + 1)..];
                string native = relative.Replace('/', Path.DirectorySeparatorChar);
                KeyValuePair<string, string> hit = observed.FirstOrDefault(o => o.Key.StartsWith(section + "|", StringComparison.Ordinal)
                                                                                 && o.Key.EndsWith(Path.DirectorySeparatorChar + native, StringComparison.Ordinal));
                if (hit.Key is null)
                {
                    files.Add(new FileChange(section, relative, key, null, FileChangeKind.NotReached));
                    continue;
                }

                string target = hit.Key[(section.Length + 1)..];
                FileChangeKind kind = hit.Value == "absent" ? FileChangeKind.New
                    : string.Equals(hit.Value, file.Value, StringComparison.OrdinalIgnoreCase) ? FileChangeKind.Same
                    : FileChangeKind.Changed;
                files.Add(new FileChange(section, relative, key, target, kind));
            }
        }

        return files;
    }

    /// <summary>The backup's copy of a file, as text, for showing it; null when the backup does not hold it.</summary>
    public string? BackupText(string section, string relative)
    {
        byte[]? bytes = Source.ReadFile(section, relative);
        return bytes is null ? null : ConfigBackup.Text(bytes);
    }

    /// <summary>The programs running now that would write a chosen part's files back when they close: CS2, Steam.</summary>
    public IReadOnlyList<string> RunningWriters()
    {
        List<string> running = [];
        if (PlayerSections.Any(s => s is Sections.Cs2 or Sections.Cs2Game) && _environment.IsRunning("cs2"))
        {
            running.Add("CS2");
        }

        if (PlayerSections.Contains(Sections.Launch) && _environment.IsRunning("steam"))
        {
            running.Add("Steam");
        }

        return running;
    }

    /// <summary>
    ///     When the backup's account has not signed in to Steam here and no account was named: the backup's account
    ///     and the accounts that have CS2 settings here. Null when there is nothing to choose. Never chosen for the
    ///     person: on a shared PC the account that signed in last is usually a stranger's.
    /// </summary>
    public (string? BackupAccount, IList<SteamAccount> Accounts)? AccountChoice()
    {
        if (Request.Account is not null || !PlayerSections.Any(s => s is Sections.Cs2 or Sections.Launch))
        {
            return null;
        }

        SteamInstall? steam = _environment.LocateSteam(Request.SteamPath);
        string? backupAccount = ConfigBackup.BackupAccount(Source.Manifest);
        if (steam is null || ConfigBackup.ResolveAccount(steam, null, backupAccount) is not null)
        {
            return null;
        }

        return (backupAccount, steam.Accounts());
    }

    /// <summary>Restores the account parts into <paramref name="accountId" />, one the person picked from <see cref="AccountChoice" />.</summary>
    public void UseAccount(string accountId)
    {
        Request = Request with { Account = accountId };
        Preview = null;
        Fingerprint = null;
    }

    /// <summary>
    ///     Why a reviewed plan cannot be applied by this run before anything is previewed: another policy, or made at
    ///     another kind of prompt (an administrator's leaves the parts written through the Steam folder, and one
    ///     without writes them). Null when it can go on to the fingerprint.
    /// </summary>
    public string? PlanProblem(PlanPins pins, bool policyPinned)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (pins.PolicySha256 != Policy?.Sha256)
        {
            return policyPinned
                ? "the plan was made under another policy than the one --policy names. Make a new plan with it."
                : "the policy file changed since the plan was made. Make a new plan.";
        }

        HashSet<string> planLeft = [.. pins.LeftByAdministrator];
        if (planLeft.SetEquals(SteamLeft))
        {
            return null;
        }

        return SteamLeft.Count > planLeft.Count
            ? "the plan was made without administrator and writes through the Steam folder ("
              + string.Join(", ", SteamLeft.Select(Sections.Label))
              + "), which an administrator's acb leaves. Apply it from a prompt without administrator, or make the plan from this administrator's prompt."
            : "the plan was made from an administrator's prompt, which leaves the parts written through the Steam folder, so it is not what this run "
              + "would do. Make a new plan from this prompt.";
    }

    /// <summary>
    ///     Dry-runs every section this restore would apply, the administrator's included (reading the driver's
    ///     settings needs no administrator), drops an administrator's section with nothing to do here, and works out
    ///     the fingerprint.
    /// </summary>
    public RunReport? PreviewChanges()
    {
        List<string> previewed = [.. Restorable];
        Preview = previewed.Count == 0 ? null : ConfigBackup.Restore(Source, RestoreMode.Restore, EngineOptions(previewed, true, null), null);
        NotHere.Clear();
        NotHere.AddRange((Preview?.Results ?? []).Where(r => AdminSections.Contains(r.Section) && r.Outcome == Outcomes.Skipped));
        AdminSections.RemoveAll(s => NotHere.Exists(r => r.Section == s));
        Fingerprint = ComputeFingerprint();
        return Preview;
    }

    /// <summary>Whether a reviewed plan's fingerprint still matches what this run's preview saw.</summary>
    public bool MatchesPlan(PlanPins pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (Fingerprint is null)
        {
            throw new InvalidOperationException("Preview the changes before comparing them with a plan.");
        }

        return string.Equals(pins.Fingerprint, Fingerprint, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Applies <paramref name="sections" /> into <paramref name="store" />. A record whose every change was rolled
    ///     back is filed away, as it has nothing left to put back.
    /// </summary>
    public RunReport Apply(IReadOnlyList<string> sections, UndoStore store)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(store);
        if (sections.Any(s => !Restorable.Contains(s)))
        {
            throw new InvalidOperationException("A restore applies only the sections its rules let through.");
        }

        RunReport report = ConfigBackup.Restore(Source, RestoreMode.Restore, EngineOptions([.. sections], false, store.Root), null);
        // A driver part given up on may still add to its record, so a record that looks empty then stays. One that
        // cannot be read just now (a scanner holding it) stays too, and acb undo deals with it.
        if (report.UndoDir is not null && report.Unknown.Count == 0
                                        && store.Find(Path.GetFileName(report.UndoDir)) is { IsReadable: true, Manifest: not null, Parts.Count: 0 } empty)
        {
            try
            {
                store.FileAway(empty);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next acb undo files a record with nothing in it.
            }
        }

        return report;
    }

    private RestoreOptions EngineOptions(List<string> sections, bool dryRun, string? undoRoot) => new()
    {
        Only = sections,
        SteamRoot = Request.SteamPath,
        AccountId = Request.Account,
        IncludeGameStateIntegration = Request.Gsi,
        DryRun = dryRun,
        WriteUndo = !dryRun,
        UndoRoot = undoRoot,
        ProcessCheck = _environment.IsRunning,
        LocateSteam = _environment.LocateSteam,
        BeforeRollback = _environment.BeforeRollback,
        StartNvidia = _environment.StartNvidia,
        DriverTimeoutMs = _environment.DriverTimeoutMs,
        NvidiaDeny = Policy is null ? null : [.. Policy.NvidiaDeny],
        LeaveFiles = _leaveFiles.Count == 0 ? null : [.. _leaveFiles],
        RecordNotes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["archive"] = Path.GetFullPath(Request.Archive),
            ["archive.sha256"] = Archive.ManifestSha256,
            ["archive.level"] = ArchiveReader.Name(Archive.Level)
        }
    };

    /// <summary>
    ///     One value for everything the preview saw: the archive, the options and the policy's digest, each part's
    ///     planned outcome, and every target it would replace (with its SHA-256), so a reviewed plan is refused if any
    ///     of it differs when applied.
    /// </summary>
    private string ComputeFingerprint()
    {
        StringBuilder text = new();
        text.Append(Archive.ManifestSha256).Append('\n');
        text.Append("gsi|").Append(Request.Gsi ? '1' : '0').Append('\n');
        text.Append("policy|").Append(Policy?.Sha256 ?? "none").Append('\n');
        text.Append("only|").Append(Request.Only is null ? "*" : string.Join(',', Request.Only)).Append('\n');
        text.Append("account|").Append(Request.Account ?? "").Append('\n');
        text.Append("steam|").Append(Request.SteamPath ?? "").Append('\n');
        foreach (SectionResult result in Preview?.Results ?? [])
        {
            text.Append(result.Section).Append('|').Append(result.Outcome).Append('|').Append(result.Detail).Append('\n');
        }

        foreach (string observed in (Preview?.Observed ?? []).Order(StringComparer.Ordinal))
        {
            text.Append(observed).Append('\n');
        }

        foreach (string left in _leaveFiles.Order(StringComparer.Ordinal))
        {
            text.Append("leave|").Append(left).Append('\n');
        }

        foreach (string section in AdminSections)
        {
            text.Append("admin|").Append(section).Append('\n');
        }

        return Dsse.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    ///     Opens and verifies a v2 archive; null with <paramref name="problem" /> set when it is refused. A folder is
    ///     not an archive: v1's unzipped backups ran their own scripts, and acb reads only what ArchiveReader checks.
    /// </summary>
    public static VerifiedArchive? OpenArchive(string path, string? requirePlatform, TrustLevel minimumLevel, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(path);
        problem = null;
        if (Directory.Exists(path))
        {
            problem = $"{path} is a folder. acb reads a backup file (.zip) as it was made; an unzipped backup is not checked or restored.";
            return null;
        }

        try
        {
            return ArchiveReader.Open(path, new ArchiveReadOptions { RequirePlatform = requirePlatform, MinimumLevel = minimumLevel });
        }
        catch (ArchiveRejectedException ex)
        {
            problem = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{path} cannot be read: {ex.Message}";
        }

        return null;
    }

    /// <summary>As <see cref="OpenArchive(string, string?, TrustLevel, out string?)" />, from a zip's bytes rather than a path.</summary>
    public static VerifiedArchive? OpenArchive(byte[] zip, string? requirePlatform, TrustLevel minimumLevel, out string? problem)
    {
        problem = null;
        try
        {
            using MemoryStream stream = new(zip, writable: false);
            return ArchiveReader.Open(stream, new ArchiveReadOptions { RequirePlatform = requirePlatform, MinimumLevel = minimumLevel });
        }
        catch (ArchiveRejectedException ex)
        {
            problem = ex.Message;
        }

        return null;
    }

    /// <summary>A policy from its bytes; null with <paramref name="problem" /> set when it is refused.</summary>
    public static OrganiserPolicy? LoadPolicy(byte[] bytes, out string? problem)
    {
        problem = null;
        try
        {
            return OrganiserPolicy.Parse(bytes);
        }
        catch (PolicyException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Loads a policy file; null with <paramref name="problem" /> set when it is refused, or when none was given.</summary>
    public static OrganiserPolicy? LoadPolicy(string? path, out string? problem)
    {
        problem = null;
        if (path is null)
        {
            return null;
        }

        try
        {
            return OrganiserPolicy.Load(path);
        }
        catch (PolicyException ex)
        {
            problem = ex.Message;
            return null;
        }
    }
}
