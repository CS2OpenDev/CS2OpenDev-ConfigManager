using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>
///     <c>acb plan</c> and <c>acb restore</c>. Both verify the archive, apply the organiser's policy, and work out
///     what would change with a dry run; restore then asks (unless --yes), records and applies the player's
///     sections, and offers the administrator's sections to an elevated run. Whether this is a restore is the
///     command's decision; nothing in the archive can make it an undo.
/// </summary>
internal static class RestoreCommand
{
    private static readonly string[] PlanFlags = ["json"];
    private static readonly string[] RestoreFlags = ["json", "yes", "elevate"];
    private static readonly string[] PlanValued = ["only", "account", "steam-path", "policy", "report"];
    private static readonly string[] RestoreValued = [.. PlanValued, "plan"];

    private const string PolicyLeftDetail = "left alone by the organiser's policy";

    private sealed record Request(string Archive, IReadOnlyList<string>? Only, string? Account, string? SteamPath, string? Policy);

    private sealed class Prepared
    {
        public required Request Request { get; set; }

        public required VerifiedArchive Archive { get; init; }

        public required ArchiveSource Source { get; init; }

        public required OrganiserPolicy? Policy { get; init; }

        /// <summary>Sections this process applies, into the player's undo store.</summary>
        public required List<string> PlayerSections { get; init; }

        /// <summary>Sections only an elevated run applies, into the administrator's undo store.</summary>
        public required List<string> AdminSections { get; init; }

        /// <summary>Sections the archive holds that the organiser's policy leaves alone.</summary>
        public required List<string> PolicyLeft { get; init; }
    }

    public static int Run(IReadOnlyList<string> args, AcbHost host, bool dryRun)
    {
        string command = dryRun ? "plan" : "restore";
        CommandLine line = CommandLine.Parse(args, command, dryRun ? PlanFlags : RestoreFlags, dryRun ? PlanValued : RestoreValued);
        Output output = new(host, command, line.Flag("json"), line.Value("report"));
        bool yes = line.Flag("yes");

        CommandReport? reviewed = null;
        Request request;
        if (line.Value("plan") is { } planPath)
        {
            line.NoPositionals();
            if (line.List("only") is not null || line.Value("account") is not null || line.Value("steam-path") is not null || line.Value("policy") is not null)
            {
                throw new UsageException("acb restore --plan takes every option from the plan; give no --only, --account, --steam-path or --policy.");
            }

            reviewed = LoadPlan(planPath, out string? planProblem);
            if (reviewed?.Archive is null)
            {
                return output.Refuse(planProblem ?? $"{planPath} is not a plan made by acb plan --json.");
            }

            request = new Request(reviewed.Archive, reviewed.Only, reviewed.Account, reviewed.SteamPath, reviewed.Policy);
        }
        else
        {
            request = new Request(line.Single("a backup file"), Archives.Sections(line.List("only")), line.Value("account"),
                line.Value("steam-path") is { } steam ? Path.GetFullPath(steam) : null,
                line.Value("policy") is { } policyPath ? Path.GetFullPath(policyPath) : null);
        }

        Prepared? prepared = Prepare(request, host, output, out int refusedWith);
        if (prepared is null)
        {
            return refusedWith;
        }

        if (reviewed is not null && reviewed.PolicySha256 != prepared.Policy?.Sha256)
        {
            return output.Refuse("the policy file changed since the plan was made. Make a new plan.");
        }

        if (!dryRun && host.Interactive && !yes && reviewed is null)
        {
            WaitForGame(prepared, host, output);
            ChooseAccount(prepared, host, output);
        }

        output.Line();
        output.Line(dryRun ? "What a restore would change:" : "What this restore changes:");
        RunReport? preview = prepared.PlayerSections.Count == 0
            ? null
            : ConfigBackup.Restore(prepared.Source, RestoreMode.Restore, EngineOptions(prepared, prepared.PlayerSections, true, null, host), null);
        foreach (SectionResult result in preview?.Results ?? [])
        {
            output.Result(result);
        }

        ElevationState elevation = host.Elevation();
        foreach (string section in prepared.AdminSections)
        {
            if (elevation.Kind == ElevationKind.Elevated)
            {
                output.Result(section, "done", "applied by this run, which has an administrator");
            }
            else
            {
                output.Result(section, "admin", elevation.CanAsk
                    ? "needs an administrator: " + elevation.Detail
                    : "needs an administrator, and " + elevation.Detail);
            }
        }

        string fingerprint = Fingerprint(prepared, preview);
        output.Report.Fingerprint = fingerprint;
        output.Report.Only = request.Only is null ? null : [.. request.Only];
        output.Report.Account = request.Account;
        output.Report.SteamPath = request.SteamPath;
        output.Report.Policy = request.Policy;
        output.Report.PolicySha256 = prepared.Policy?.Sha256;
        if (dryRun)
        {
            output.Line();
            output.Note("Nothing was changed.");
            return output.Finish(ExitCodes.For(output.Report.Sections.Select(l => new SectionResult(l.Section, l.Outcome, l.Detail))));
        }

        if (reviewed is not null && reviewed.Fingerprint != fingerprint)
        {
            return output.Refuse("this PC changed since the plan was made (a file the restore replaces, the launch options, or what a part would do). Make a new plan and review it.");
        }

        if (!yes)
        {
            if (!host.Interactive)
            {
                output.Note("Nothing was changed: there is no terminal to ask on. Run it again with --yes to restore.");
                return output.Finish(ExitCodes.Refused);
            }

            if (!host.Confirm("Restore these settings onto this PC?"))
            {
                output.Note("Nothing was changed.");
                return output.Finish(ExitCodes.Refused);
            }
        }

        output.Report.Sections.Clear();
        output.Report.Sections.AddRange(prepared.PolicyLeft.Select(s => new SectionLine(s, Sections.Label(s), "skipped", PolicyLeftDetail)));
        output.Line();
        output.Line("Restoring:");
        int exit = ExitCodes.Done;
        if (prepared.PlayerSections.Count > 0)
        {
            UndoStore store = host.UserStore;
            try
            {
                store.EnsureUserStore();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return output.Refuse($"the undo store {store.Root} cannot be made ({ex.Message}), so nothing was changed.");
            }

            int code = Apply(prepared, prepared.PlayerSections, store, host, output);
            if (code is ExitCodes.Refused or ExitCodes.RecordsNeedAttention)
            {
                return output.Finish(code);
            }

            exit = code;
        }

        if (prepared.AdminSections.Count > 0)
        {
            exit = Math.Max(exit, AdminPart(prepared, host, output, yes, line.Flag("elevate")));
        }

        output.Line();
        output.Note(exit == ExitCodes.Done
            ? "Done. To put this PC back as it was, run: acb undo"
            : "Finished, with parts left as said above. To put this PC back as it was, run: acb undo");
        return output.Finish(exit);
    }

    /// <summary>
    ///     The run acb starts through the UAC prompt for the administrator's sections. It trusts only its own
    ///     command line: it reads the archive again and refuses it unless its manifest digest is the one the first
    ///     run showed the person, reads a policy only with the digest the first run read, applies nothing but the
    ///     administrator's sections, and records them in the administrator's store.
    /// </summary>
    public static int Elevated(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "restore (administrator)", [],
            ["digest", "only", "account", "steam-path", "policy", "policy-sha256", "pipe"]);
        Output output = new(host, "restore", json: false, reportPath: null);
        int code = ElevatedCore(line, host, output);
        if (line.Value("pipe") is { } pipe && OperatingSystem.IsWindows())
        {
            WindowsElevator.Send(pipe, output.Report);
        }

        return code;
    }

    private static int ElevatedCore(CommandLine line, AcbHost host, Output output)
    {
        if (host.Elevation().Kind != ElevationKind.Elevated)
        {
            return output.Refuse("the administrator's part runs only as an administrator.");
        }

        string digest = line.Value("digest") ?? throw new UsageException("--digest is required.");
        List<string> only = Archives.Sections(line.List("only")) ?? throw new UsageException("--only is required.");
        if (only.Find(s => !RestoreRules.NeedsAdministrator(s)) is { } other)
        {
            throw new UsageException($"The administrator's run applies only the sections that need one; {other} does not.");
        }

        string? policyPath = line.Value("policy");
        if (policyPath is not null && line.Value("policy-sha256") is null)
        {
            throw new UsageException("--policy needs --policy-sha256.");
        }

        Request request = new(line.Single("a backup file"), only, line.Value("account"), line.Value("steam-path"), policyPath);
        Prepared? prepared = Prepare(request, host, output, out int refusedWith);
        if (prepared is null)
        {
            return refusedWith;
        }

        if (!string.Equals(prepared.Archive.ManifestSha256, digest, StringComparison.Ordinal))
        {
            return output.Refuse("the backup is not the one the first run checked: it was replaced in between. Nothing was changed.");
        }

        if (policyPath is not null && !string.Equals(prepared.Policy?.Sha256, line.Value("policy-sha256"), StringComparison.Ordinal))
        {
            return output.Refuse("the policy file is not the one the first run read: it was replaced in between. Nothing was changed.");
        }

        string root = host.MachineStoreRoot ?? throw new PlatformNotSupportedException("There is no administrator's undo store on this platform.");
        try
        {
            host.EnsureMachineStore(root, host.MachineStoreParent!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return output.Refuse(ex.Message);
        }

        output.Line("Restoring as an administrator:");
        return output.Finish(Apply(prepared, prepared.AdminSections, new UndoStore(root, isMachine: true), host, output));
    }

    /// <summary>Verifies, applies the policy, and sorts the sections into the player's and the administrator's.</summary>
    private static Prepared? Prepare(Request request, AcbHost host, Output output, out int refusedWith)
    {
        refusedWith = ExitCodes.Refused;
        if (host.Platform is null)
        {
            refusedWith = output.Refuse("acb restores only on Windows x64 and Linux x64.");
            return null;
        }

        OrganiserPolicy? policy = Archives.Policy(request.Policy, out string? policyProblem);
        if (policyProblem is not null)
        {
            refusedWith = output.Refuse(policyProblem);
            return null;
        }

        VerifiedArchive? archive = Archives.Open(request.Archive, host.Platform, policy?.MinimumLevel ?? TrustLevel.Hash, out string? problem);
        if (archive is null)
        {
            refusedWith = output.Refuse(problem!);
            return null;
        }

        Archives.Header(output, request.Archive, archive);
        if (policy?.ArchiveProblem(archive) is { } policyRefusal)
        {
            refusedWith = output.Refuse(policyRefusal);
            return null;
        }

        ArchiveSource source;
        try
        {
            source = new ArchiveSource(archive);
        }
        catch (ArchiveRejectedException ex)
        {
            refusedWith = output.Refuse(ex.Message);
            return null;
        }

        if (policy is { RequireCs2Closed: true } && host.IsRunning("cs2"))
        {
            refusedWith = output.Refuse("the organiser's policy restores only while CS2 is closed, and it is running. Close CS2 and run this again.");
            return null;
        }

        List<string> chosen = [];
        List<string> policyLeft = [];
        foreach (string section in Sections.All)
        {
            if (source.Manifest.Child(section) is null || (request.Only is not null && !request.Only.Contains(section)))
            {
                continue;
            }

            if (policy is not null && !policy.Allows(section))
            {
                output.Result(section, "skipped", PolicyLeftDetail);
                policyLeft.Add(section);
                continue;
            }

            chosen.Add(section);
        }

        return new Prepared
        {
            Request = request,
            Archive = archive,
            Source = source,
            Policy = policy,
            PlayerSections = [.. chosen.Where(s => !RestoreRules.NeedsAdministrator(s))],
            AdminSections = [.. chosen.Where(RestoreRules.NeedsAdministrator)],
            PolicyLeft = policyLeft
        };
    }

    private static RestoreOptions EngineOptions(Prepared prepared, List<string> sections, bool dryRun, string? undoRoot, AcbHost host) => new()
    {
        Only = sections,
        SteamRoot = prepared.Request.SteamPath,
        AccountId = prepared.Request.Account,
        DryRun = dryRun,
        WriteUndo = !dryRun,
        UndoRoot = undoRoot,
        ProcessCheck = host.IsRunning,
        BeforeRollback = host.BeforeRollback,
        NvidiaDeny = prepared.Policy is null ? null : [.. prepared.Policy.NvidiaDeny],
        RecordNotes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["archive"] = Path.GetFullPath(prepared.Request.Archive),
            ["archive.sha256"] = prepared.Archive.ManifestSha256,
            ["archive.level"] = ArchiveReader.Name(prepared.Archive.Level)
        }
    };

    /// <summary>Applies sections into one store and reports them; a rollback that failed or a claim that could not be made stops the run.</summary>
    private static int Apply(Prepared prepared, List<string> sections, UndoStore store, AcbHost host, Output output)
    {
        RunReport report = ConfigBackup.Restore(prepared.Source, RestoreMode.Restore, EngineOptions(prepared, sections, false, store.Root, host), null);
        foreach (SectionResult result in report.Results)
        {
            output.Result(result.Section, result.Outcome,
                report.RolledBack.Contains(result.Section) && result.Outcome == "failed"
                    ? result.Detail + "; what it had changed was put back"
                    : result.Detail);
        }

        if (report.UndoDir is not null)
        {
            output.Report.UndoRecord = report.UndoDir;
            if (store.Find(Path.GetFileName(report.UndoDir)) is { Parts.Count: 0 } empty)
            {
                // Every change it recorded was rolled back, so it has nothing left to put back.
                TryFileAway(store, empty);
            }
        }

        if (report.ClaimProblem is not null)
        {
            output.Note("Stopped before the first change: " + report.ClaimProblem);
            return ExitCodes.Refused;
        }

        if (report.RollbackProblem is not null)
        {
            output.Note("Stopped: a part failed partway and could not be put back. " + report.RollbackProblem);
            output.Note($"Its record is kept in {report.UndoDir}. Run acb undo once the cause is fixed, or acb undo --list to see it.");
            return ExitCodes.RecordsNeedAttention;
        }

        return ExitCodes.For(report.Results);
    }

    private static void TryFileAway(UndoStore store, UndoRecord record)
    {
        try
        {
            store.FileAway(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next acb undo files a record with nothing in it.
        }
    }

    /// <summary>
    ///     The sections only an administrator applies. Told up front when Windows will not ask; offered when it
    ///     will (default no); headless only with --elevate. An elevated acb applies them itself.
    /// </summary>
    private static int AdminPart(Prepared prepared, AcbHost host, Output output, bool yes, bool elevate)
    {
        ElevationState elevation = host.Elevation();
        string labels = string.Join(", ", prepared.AdminSections.Select(Sections.Label));
        if (elevation.Kind == ElevationKind.Elevated)
        {
            string root = host.MachineStoreRoot!;
            try
            {
                host.EnsureMachineStore(root, host.MachineStoreParent!);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                foreach (string section in prepared.AdminSections)
                {
                    output.Result(section, "admin", "left: " + ex.Message);
                }

                return ExitCodes.PartsLeft;
            }

            return Apply(prepared, prepared.AdminSections, new UndoStore(root, isMachine: true), host, output);
        }

        if (!elevation.CanAsk)
        {
            foreach (string section in prepared.AdminSections)
            {
                output.Result(section, "admin", "left: it needs an administrator, and " + elevation.Detail);
            }

            return ExitCodes.PartsLeft;
        }

        bool ask = elevate || (!yes && host.Confirm($"{labels} need an administrator. {elevation.Detail}. Ask Windows now?"));
        if (!ask)
        {
            foreach (string section in prepared.AdminSections)
            {
                output.Result(section, "admin", yes
                    ? "left: it needs an administrator; pass --elevate to have Windows ask for one"
                    : "left: it needs an administrator");
            }

            return ExitCodes.PartsLeft;
        }

        List<string> arguments = ["__elevated", "restore", Path.GetFullPath(prepared.Request.Archive),
            "--digest", prepared.Archive.ManifestSha256, "--only", string.Join(',', prepared.AdminSections)];
        if (prepared.Request.Account is { } account)
        {
            arguments.AddRange(["--account", account]);
        }

        if (prepared.Request.SteamPath is { } steam)
        {
            arguments.AddRange(["--steam-path", steam]);
        }

        if (prepared.Policy is { } policy)
        {
            arguments.AddRange(["--policy", prepared.Request.Policy!, "--policy-sha256", policy.Sha256]);
        }

        return Merge(host.Elevator.Run(arguments), prepared.AdminSections, output);
    }

    /// <summary>Reports an elevated run's sections as if they were this run's; one that sent nothing is reported by its exit code.</summary>
    internal static int Merge(ElevatedResult result, IReadOnlyList<string> sections, Output output)
    {
        if (result.Report is { } report)
        {
            foreach (SectionLine line in report.Sections)
            {
                output.Result(line.Section, line.Outcome, line.Detail);
            }

            foreach (string note in report.Notes)
            {
                output.Note("Administrator's run: " + note);
            }

            if (report.UndoRecord is not null)
            {
                output.Note("The administrator's run kept its record in " + report.UndoRecord);
            }

            return result.ExitCode;
        }

        foreach (string section in sections)
        {
            output.Result(section, "admin", "left: " + (result.Problem ?? "the administrator's run ended with exit code " + result.ExitCode));
        }

        return result.Declined ? ExitCodes.PartsLeft : Math.Max(ExitCodes.PartsLeft, result.ExitCode);
    }

    /// <summary>Interactive only: CS2 and Steam write their files back when they close, so offer to wait (default: leave those parts).</summary>
    private static void WaitForGame(Prepared prepared, AcbHost host, Output output)
    {
        while (true)
        {
            List<string> running = [];
            if (prepared.PlayerSections.Any(s => s is Sections.Cs2 or Sections.Cs2Game) && host.IsRunning("cs2"))
            {
                running.Add("CS2");
            }

            if (prepared.PlayerSections.Contains(Sections.Launch) && host.IsRunning("steam"))
            {
                running.Add("Steam");
            }

            if (running.Count == 0)
            {
                return;
            }

            output.Line($"{string.Join(" and ", running)} {(running.Count == 1 ? "is" : "are")} running, and writes its settings back when it closes.");
            string? answer = host.Ask("Close it, then type r and Enter to check again; Enter alone leaves those parts for later:");
            if (!string.Equals(answer, "r", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
    }

    /// <summary>
    ///     Interactive only: the backup's account has not signed in to Steam here. Never guessed (on a shared PC the
    ///     account that signed in last is usually a stranger's); offered as a menu whose default leaves the parts.
    /// </summary>
    private static void ChooseAccount(Prepared prepared, AcbHost host, Output output)
    {
        if (prepared.Request.Account is not null || !prepared.PlayerSections.Any(s => s is Sections.Cs2 or Sections.Launch))
        {
            return;
        }

        SteamInstall? steam = SteamInstall.Locate(prepared.Request.SteamPath);
        string? backupAccount = ConfigBackup.BackupAccount(prepared.Source.Manifest);
        if (steam is null || ConfigBackup.ResolveAccount(steam, null, backupAccount) is not null)
        {
            return;
        }

        IList<SteamAccount> accounts = steam.Accounts();
        output.Line($"Your Steam account ({backupAccount ?? "unknown"}) has not signed in to Steam on this PC yet.");
        output.Line("Best: sign in to Steam with it, start CS2 once, close CS2, then restore again.");
        if (accounts.Count == 0)
        {
            return;
        }

        output.Line("These accounts have CS2 settings here. Pick one only if it is yours under another number:");
        for (int i = 0; i < accounts.Count; i++)
        {
            output.Line($"  {i + 1}. {accounts[i].AccountId}{(accounts[i].MostRecent ? " (signed in last)" : "")}");
        }

        string? pick = host.Ask("Its number, or Enter alone to leave your CS2 settings and launch options:");
        if (int.TryParse(pick, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= accounts.Count)
        {
            prepared.Request = prepared.Request with { Account = accounts[index - 1].AccountId };
        }
    }

    /// <summary>
    ///     One value for everything the plan saw: the archive, each part's planned outcome, and every target it
    ///     would replace (with its SHA-256), so a reviewed plan is refused if any of it differs when applied.
    /// </summary>
    private static string Fingerprint(Prepared prepared, RunReport? preview)
    {
        StringBuilder text = new();
        text.Append(prepared.Archive.ManifestSha256).Append('\n');
        foreach (SectionResult result in preview?.Results ?? [])
        {
            text.Append(result.Section).Append('|').Append(result.Outcome).Append('|').Append(result.Detail).Append('\n');
        }

        foreach (string observed in (preview?.Observed ?? []).Order(StringComparer.Ordinal))
        {
            text.Append(observed).Append('\n');
        }

        foreach (string section in prepared.AdminSections)
        {
            text.Append("admin|").Append(section).Append('\n');
        }

        return Dsse.Sha256Hex(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static CommandReport? LoadPlan(string path, out string? problem)
    {
        problem = null;
        try
        {
            CommandReport? plan = JsonSerializer.Deserialize(File.ReadAllBytes(path), CliJson.Default.CommandReport);
            if (plan is null || plan.Command != "plan" || plan.Fingerprint is null)
            {
                problem = $"{path} is not a plan made by acb plan --json.";
                return null;
            }

            return plan;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = $"the plan {path} cannot be read: {ex.Message}";
            return null;
        }
    }
}
