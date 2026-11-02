using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>
///     <c>acb plan</c> and <c>acb restore</c>: the words, questions and exit codes around a
///     <see cref="RestoreSession" />, which makes every decision. Restore asks (unless --yes), applies the player's
///     sections, and offers the administrator's sections to an elevated run. Whether this is a restore is the
///     command's decision; nothing in the archive can make it an undo.
/// </summary>
internal static class RestoreCommand
{
    private static readonly string[] PlanFlags = ["json", "gsi"];
    private static readonly string[] RestoreFlags = ["json", "gsi", "yes", "elevate"];
    private static readonly string[] PlanValued = ["only", "account", "steam-path", "policy", "report"];
    private static readonly string[] RestoreValued = [.. PlanValued, "plan"];

    /// <summary>
    ///     Why an administrator's acb leaves the parts written through the Steam folder: any user may change that
    ///     folder, and a link planted in it would turn an administrator's write into one anywhere on the PC. Where
    ///     the account runs nothing without administrator, the advice says what can be done instead.
    /// </summary>
    internal static string SteamLeftDetail(ElevationState elevation) => elevation.AlwaysElevated
        ? "left: on this PC every program this account starts runs as an administrator (User Account Control is off, or this is "
          + "the built-in Administrator account), and an administrator's acb writes nothing through the Steam folder, which other "
          + "users of this PC can change. Restore this part signed in to Windows with a standard account, or once User Account Control is on"
        : "left: acb runs as an administrator, and an administrator's acb writes nothing through the Steam folder, "
          + "which other users of this PC can change. Run acb without administrator for this part";

    /// <summary>
    ///     Runs <c>acb plan</c> (<paramref name="dryRun" />) or <c>acb restore</c>. <c>acb restore --dry-run</c> is a plan
    ///     that takes restore's options (<paramref name="asRestore" />), so adding it to a working restore line only
    ///     stops the changes; with --plan it checks the reviewed plan against the PC and applies nothing.
    /// </summary>
    public static int Run(IReadOnlyList<string> args, AcbHost host, bool dryRun, bool asRestore)
    {
        string command = dryRun ? "plan" : "restore";
        CommandLine line = CommandLine.Parse(args, asRestore ? "restore" : "plan", asRestore ? RestoreFlags : PlanFlags, asRestore ? RestoreValued : PlanValued);
        Output output = new(host, command, line.Flag("json"), line.Value("report"));
        bool yes = line.Flag("yes");

        CommandReport? reviewed = null;
        RestoreRequest request;
        string? pinnedPolicy = line.Value("policy") is { } policyArgument ? Path.GetFullPath(policyArgument) : null;
        if (line.Value("plan") is { } planPath)
        {
            line.NoPositionals();
            if (line.List("only") is not null || line.Value("account") is not null || line.Value("steam-path") is not null)
            {
                throw new UsageException("acb restore --plan takes the sections, account and Steam folder from the plan; give no --only, --account or --steam-path.");
            }

            reviewed = LoadPlan(planPath, out string? planProblem);
            if (reviewed?.Archive is null)
            {
                return output.Refuse(planProblem ?? $"{planPath} is not a plan made by acb plan --json.");
            }

            // A plan file is not signed, so what would widen the restore must be said again on this command line.
            bool planGsi = reviewed.GameStateIntegration == true;
            if (planGsi != line.Flag("gsi"))
            {
                return output.Refuse(planGsi
                    ? "the plan writes game state integration files, which send live game data to the address they name. Give --gsi as well to apply it."
                    : "the plan was made without --gsi, so it writes no game state integration files. Make a new plan with --gsi to write them.");
            }

            if (pinnedPolicy is not null && reviewed.PolicySha256 is null)
            {
                return output.Refuse("the plan was made without a policy, so it is not the one --policy names. Make a new plan with --policy.");
            }

            request = new RestoreRequest(reviewed.Archive, reviewed.Only, reviewed.Account, reviewed.SteamPath, pinnedPolicy ?? reviewed.Policy, planGsi);
        }
        else
        {
            request = new RestoreRequest(line.Single("a backup file"), Archives.Sections(line.List("only")), line.Value("account"),
                line.Value("steam-path") is { } steam ? Path.GetFullPath(steam) : null, pinnedPolicy, line.Flag("gsi"));
        }

        ElevationState elevation = host.Elevation();
        RestoreSession? session = Open(request, host, elevation, output, out int refusedWith);
        if (session is null)
        {
            return refusedWith;
        }

        PlanPins? pins = reviewed is null
            ? null
            : new PlanPins(reviewed.PolicySha256, [.. reviewed.Sections.Where(l => l.LeftByAdministrator == true).Select(l => l.Section)], reviewed.Fingerprint!);
        if (pins is not null && pins.PolicySha256 != session.Policy?.Sha256)
        {
            return output.Refuse(session.PlanProblem(pins, pinnedPolicy is not null)!);
        }

        if (session.Policy is not null)
        {
            output.Line("Policy:   " + request.Policy + " (" + session.Policy.Sha256 + ")");
        }

        if (request.Gsi)
        {
            output.Line("GSI:      game state integration files are written too (--gsi)");
        }

        string steamLeft = SteamLeftDetail(elevation);
        foreach (string section in session.SteamLeft)
        {
            output.Result(section, Outcomes.Deferred, steamLeft, leftByAdministrator: true);
        }

        // A plan made with administrator leaves the parts written through the Steam folder, and one made without
        // writes them, so applying it at the other kind of prompt would do something else. Said here, before the
        // fingerprint would refuse it as a change to the PC that did not happen.
        if (pins is not null && session.PlanProblem(pins, pinnedPolicy is not null) is { } mismatch)
        {
            return output.Refuse(mismatch);
        }

        if (!dryRun && host.Interactive && !yes && reviewed is null)
        {
            WaitForGame(session, host, output);
            ChooseAccount(session, host, output);
        }

        output.Line();
        output.Line(dryRun ? "What a restore would change:" : "What this restore changes:");
        RunReport? preview = session.PreviewChanges();
        foreach (SectionResult result in preview?.Results ?? [])
        {
            output.Result(result);
        }

        if (elevation.Kind != ElevationKind.Elevated)
        {
            foreach (string section in session.AdminSections)
            {
                output.Result(section, Outcomes.Admin, elevation.CanAsk
                    ? "needs an administrator: " + elevation.Detail
                    : "needs an administrator, and " + elevation.Detail);
            }
        }

        output.Report.Fingerprint = session.Fingerprint;
        output.Report.Only = request.Only is null ? null : [.. request.Only];
        output.Report.Account = session.Request.Account;
        output.Report.SteamPath = request.SteamPath;
        output.Report.Policy = request.Policy;
        output.Report.PolicySha256 = session.Policy?.Sha256;
        output.Report.GameStateIntegration = request.Gsi ? true : null;
        if (pins is not null && !session.MatchesPlan(pins))
        {
            return output.Refuse("this PC changed since the plan was made (a file the restore replaces, the launch options, or what a part would do). Make a new plan and review it.");
        }

        if (dryRun)
        {
            output.Line();
            if (reviewed is not null)
            {
                output.Note("The PC still matches the plan, so acb restore --plan applies it as reviewed.");
            }

            output.Note("Nothing was changed.");
            return output.Finish(ExitCodes.For(output.Report.Sections.Select(l => new SectionResult(l.Section, l.Outcome, l.Detail))));
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

        bool elevate = line.Flag("elevate");
        int exit = ApplyAll(session, host, output, elevation, question => elevate || (!yes && host.Confirm(question)), yes, out bool stopped);
        if (stopped)
        {
            return exit;
        }

        output.Line();
        output.Note(exit switch
        {
            ExitCodes.Done => "Done. To put this PC back as it was, run: acb undo",
            ExitCodes.RecordsNeedAttention => "Finished, but the administrator's part may have changed this PC without saying what (see above). To put this PC back as it was, run: acb undo",
            _ => "Finished, with parts left as said above. To put this PC back as it was, run: acb undo"
        });
        return output.Finish(exit);
    }

    /// <summary>
    ///     Applies a previewed session the person agreed to: the player's sections into their store, then the
    ///     administrator's, run here when elevated or through Windows when <paramref name="askWindows" /> (given the
    ///     question) says so; <paramref name="yes" /> words a part left without asking for the command line.
    ///     <paramref name="stopped" />: the run ended here and its report is finished.
    /// </summary>
    internal static int ApplyAll(RestoreSession session, AcbHost host, Output output, ElevationState elevation, Func<string, bool> askWindows, bool yes, out bool stopped)
    {
        stopped = false;
        string steamLeft = SteamLeftDetail(elevation);
        output.Report.Sections.Clear();
        output.Report.Sections.AddRange(session.PolicyLeft.Select(s => new SectionLine(s, Sections.Label(s), Outcomes.Skipped, RestoreSession.PolicyLeftDetail)));
        output.Report.Sections.AddRange(session.SteamLeft.Select(s => new SectionLine(s, Sections.Label(s), Outcomes.Deferred, steamLeft, true)));
        output.Report.Sections.AddRange(session.NotHere.Select(r => new SectionLine(r.Section, Sections.Label(r.Section), r.Outcome, r.Detail)));
        output.Line();
        output.Line("Restoring:");
        int exit = session.SteamLeft.Count > 0 ? ExitCodes.PartsLeft : ExitCodes.Done;
        if (session.PlayerSections.Count > 0)
        {
            UndoStore store = host.UserStore;
            try
            {
                store.EnsureUserStore();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stopped = true;
                return output.Refuse($"the undo store {store.Root} cannot be made ({ex.Message}), so nothing was changed.");
            }

            int code = Apply(session, session.PlayerSections, store, output);
            if (code is ExitCodes.Refused or ExitCodes.RecordsNeedAttention)
            {
                stopped = true;
                return output.Finish(code);
            }

            exit = Math.Max(exit, code);
        }

        if (session.AdminSections.Count > 0)
        {
            // Once the player's sections changed the PC, an administrator's run that refuses leaves its parts; it
            // cannot make the whole run "nothing changed".
            bool changed = output.Report.Sections.Any(l => l.Outcome == Outcomes.Done);
            exit = Math.Max(exit, AfterChanges(AdminPart(session, host, output, askWindows, yes), changed));
        }

        return exit;
    }

    /// <summary>The environment a restore from this host runs in.</summary>
    internal static RestoreEnvironment Environment(AcbHost host, ElevationState elevation) => new()
    {
        Platform = host.Platform,
        IsRunning = host.IsRunning,
        LocateSteam = host.LocateSteam,
        Machine = host.Machine,
        AsAdministrator = elevation.Kind == ElevationKind.Elevated,
        BeforeRollback = host.BeforeRollback,
        StartNvidia = host.StartNvidia,
        DriverTimeoutMs = host.DriverTimeoutMs
    };

    /// <summary>
    ///     Opens the session, showing the backup's header once it is verified and the sections the policy leaves;
    ///     null when it is refused, with the refusal said.
    /// </summary>
    private static RestoreSession? Open(RestoreRequest request, AcbHost host, ElevationState elevation, Output output, out int refusedWith,
        byte[]? archiveBytes = null, byte[]? policyBytes = null)
    {
        refusedWith = ExitCodes.Refused;
        RestoreSession session;
        try
        {
            session = RestoreSession.Open(request, Environment(host, elevation), archiveBytes, policyBytes);
        }
        catch (RestoreRefusedException ex)
        {
            if (ex.Archive is not null)
            {
                Archives.Header(output, request.Archive, ex.Archive);
            }

            refusedWith = output.Refuse(ex.Message);
            return null;
        }

        Archives.Header(output, request.Archive, session.Archive);
        foreach (string section in session.PolicyLeft)
        {
            output.Result(section, Outcomes.Skipped, RestoreSession.PolicyLeftDetail);
        }

        return session;
    }

    /// <summary>
    ///     The run acb starts through the UAC prompt for the administrator's sections. It opens no path it was given:
    ///     the backup's and the policy's bytes come over the pipe from the run that started it, and the paths on its
    ///     command line are only shown and recorded, so it never reaches a share, or a link the player made, as the
    ///     administrator. It refuses the backup unless its manifest digest is the one the first run showed the person,
    ///     reads a policy only with the digest the first run read, applies nothing but the administrator's sections,
    ///     and records them in the administrator's store.
    /// </summary>
    public static int Elevated(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "restore (administrator)", [], ["digest", "only", "policy", "policy-sha256", "pipe"]);
        Output output = new(host, "restore", json: false, reportPath: null);
        using IElevatedChannel? channel = line.Value("pipe") is { } pipe ? host.ConnectElevated(pipe) : null;
        int code = ElevatedCore(line, channel?.Input, host, output);
        channel?.Send(output.Report);
        return code;
    }

    private static int ElevatedCore(CommandLine line, IReadOnlyList<byte[]?>? input, AcbHost host, Output output)
    {
        ElevationState elevation = host.Elevation();
        if (elevation.Kind != ElevationKind.Elevated)
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
        if ((policyPath is null) != (line.Value("policy-sha256") is null))
        {
            throw new UsageException("--policy and --policy-sha256 go together.");
        }

        if (input is not [{ } archiveBytes, var policyBytes] || (policyPath is null) != (policyBytes is null))
        {
            return output.Refuse("the run that started this one sent no backup, so nothing was changed. The administrator's part reads a backup only from that run, never from a path.");
        }

        RestoreRequest request = new(line.Single("the backup's path, as the first run showed it"), only, null, null, policyPath, Gsi: false);
        RestoreSession? session = Open(request, host, elevation, output, out int refusedWith, archiveBytes, policyBytes);
        if (session is null)
        {
            return refusedWith;
        }

        if (!string.Equals(session.Archive.ManifestSha256, digest, StringComparison.Ordinal))
        {
            return output.Refuse("the backup is not the one the first run checked: it was replaced in between. Nothing was changed.");
        }

        if (policyPath is not null && !string.Equals(session.Policy?.Sha256, line.Value("policy-sha256"), StringComparison.Ordinal))
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
        return output.Finish(Apply(session, session.AdminSections, new UndoStore(root, isMachine: true), output));
    }

    /// <summary>Applies sections into one store and reports them; a rollback that failed or a claim that could not be made stops the run.</summary>
    private static int Apply(RestoreSession session, List<string> sections, UndoStore store, Output output)
    {
        RunReport report = session.Apply(sections, store);
        foreach (SectionResult result in report.Results)
        {
            output.Result(result.Section, result.Outcome,
                report.RolledBack.Contains(result.Section) && result.Outcome == Outcomes.Failed
                    ? result.Detail + "; what it had changed was put back"
                    : result.Detail);
        }

        if (report.UndoDir is not null)
        {
            output.Report.UndoRecord = report.UndoDir;
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

        if (report.Unknown.Count > 0)
        {
            output.Note("Stopped: the NVIDIA driver stopped answering after " + string.Join(", ", report.Unknown.Select(Sections.Label))
                        + " may have started changing this PC, so what it changed is not known. To put this PC back as it was, run: acb undo");
            return ExitCodes.RecordsNeedAttention;
        }

        return ExitCodes.For(report.Results);
    }

    /// <summary>
    ///     The sections only an administrator applies. Told up front when Windows will not ask; offered when it
    ///     will (default no); headless only with --elevate. An elevated acb applies them itself.
    /// </summary>
    private static int AdminPart(RestoreSession session, AcbHost host, Output output, Func<string, bool> askWindows, bool yes)
    {
        ElevationState elevation = host.Elevation();
        string labels = string.Join(", ", session.AdminSections.Select(Sections.Label));
        if (elevation.Kind == ElevationKind.Elevated)
        {
            string root = host.MachineStoreRoot!;
            try
            {
                host.EnsureMachineStore(root, host.MachineStoreParent!);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                foreach (string section in session.AdminSections)
                {
                    output.Result(section, Outcomes.Admin, "left: " + ex.Message);
                }

                return ExitCodes.PartsLeft;
            }

            return Apply(session, session.AdminSections, new UndoStore(root, isMachine: true), output);
        }

        if (!elevation.CanAsk)
        {
            foreach (string section in session.AdminSections)
            {
                output.Result(section, Outcomes.Admin, "left: it needs an administrator, and " + elevation.Detail);
            }

            return ExitCodes.PartsLeft;
        }

        if (!askWindows($"{labels} need an administrator. {elevation.Detail}. Ask Windows now?"))
        {
            foreach (string section in session.AdminSections)
            {
                output.Result(section, Outcomes.Admin, yes
                    ? "left: it needs an administrator; pass --elevate to have Windows ask for one"
                    : "left: it needs an administrator");
            }

            return ExitCodes.PartsLeft;
        }

        return Merge(Elevate(session, host), session.AdminSections, output);
    }

    /// <summary>
    ///     Starts the administrator's run for the session's administrator's sections. It opens no path: it gets the
    ///     bytes, read here as the player, and checks them against the digests the person was shown. The
    ///     administrator's sections need no Steam folder or account.
    /// </summary>
    internal static ElevatedResult Elevate(RestoreSession session, AcbHost host)
    {
        byte[] archiveBytes;
        byte[]? policyBytes;
        try
        {
            archiveBytes = File.ReadAllBytes(session.Request.Archive);
            policyBytes = session.Policy is null ? null : File.ReadAllBytes(session.Request.Policy!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return new ElevatedResult(false, ExitCodes.PartsLeft, null, "the backup could not be read again for the administrator's run (" + ex.Message + ")");
        }

        List<string> arguments = ["__elevated", "restore", Path.GetFullPath(session.Request.Archive),
            "--digest", session.Archive.ManifestSha256, "--only", string.Join(',', session.AdminSections)];
        if (session.Policy is { } policy)
        {
            arguments.AddRange(["--policy", session.Request.Policy!, "--policy-sha256", policy.Sha256]);
        }

        return host.Elevator.Run(arguments, [archiveBytes, policyBytes]);
    }

    /// <summary>
    ///     Reports an elevated run's sections as if they were this run's. A section it did not report is reported as
    ///     left; without <paramref name="everySection" /> (an undo, which reports a section only when a record held
    ///     it) that is so only when the run did not end cleanly. A run that started and sent no report may have
    ///     changed its sections before it was stopped or lost its pipe, whatever its exit code, so they are reported
    ///     as unknown with exit 4; only a usage error, which stops it before it reads anything, stays refused. A
    ///     refusal stays "nothing changed" only for the administrator's part; the caller weighs it against what this
    ///     run already changed.
    /// </summary>
    internal static int Merge(ElevatedResult result, IReadOnlyList<string> sections, Output output, bool everySection = true)
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

            List<string> missing = everySection || result.ExitCode != ExitCodes.Done
                ? [.. sections.Where(s => !report.Sections.Exists(l => l.Section == s))]
                : [];
            foreach (string section in missing)
            {
                output.Result(section, Outcomes.Admin, "left: the administrator's run did not apply it (exit code " + result.ExitCode + ")");
            }

            int code = result.ExitCode == ExitCodes.Usage ? ExitCodes.Refused : result.ExitCode;
            return code == ExitCodes.Done && missing.Count > 0 ? ExitCodes.PartsLeft : code;
        }

        if (!result.Started || result.ExitCode == ExitCodes.Usage)
        {
            foreach (string section in sections)
            {
                output.Result(section, Outcomes.Admin, "left: " + (result.Started
                    ? "the administrator's run refused its command line (exit code " + result.ExitCode + ")"
                    : result.Problem ?? "the administrator's run did not start"));
            }

            return result.Started ? ExitCodes.Refused : ExitCodes.PartsLeft;
        }

        foreach (string section in sections)
        {
            output.Result(section, Outcomes.Admin, "unknown: " + (result.Problem ?? "the administrator's run ended without a report") + " (exit code "
                                                   + result.ExitCode + "), so it may have changed this part. From an administrator's prompt, acb undo --list shows whether it left a record");
        }

        return ExitCodes.RecordsNeedAttention;
    }

    /// <summary>
    ///     The administrator's part's code within a run that may already have changed the PC: "refused, nothing
    ///     changed" is true of the whole run only when nothing else changed, and otherwise those parts were left.
    /// </summary>
    internal static int AfterChanges(int adminCode, bool changed) =>
        adminCode == ExitCodes.Refused && changed ? ExitCodes.PartsLeft : adminCode;

    /// <summary>Interactive only: CS2 and Steam write their files back when they close, so offer to wait (default: leave those parts).</summary>
    private static void WaitForGame(RestoreSession session, AcbHost host, Output output)
    {
        while (session.RunningWriters() is { Count: > 0 } running)
        {
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
    private static void ChooseAccount(RestoreSession session, AcbHost host, Output output)
    {
        if (session.AccountChoice() is not { } choice)
        {
            return;
        }

        output.Line($"Your Steam account ({choice.BackupAccount ?? "unknown"}) has not signed in to Steam on this PC yet.");
        output.Line("Best: sign in to Steam with it, start CS2 once, close CS2, then restore again.");
        if (choice.Accounts.Count == 0)
        {
            return;
        }

        output.Line("These accounts have CS2 settings here. Pick one only if it is yours under another number:");
        for (int i = 0; i < choice.Accounts.Count; i++)
        {
            output.Line($"  {i + 1}. {choice.Accounts[i].AccountId}{(choice.Accounts[i].MostRecent ? " (signed in last)" : "")}");
        }

        string? pick = host.Ask("Its number, or Enter alone to leave your CS2 settings and launch options:");
        if (int.TryParse(pick, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            && index >= 1 && index <= choice.Accounts.Count)
        {
            session.UseAccount(choice.Accounts[index - 1].AccountId);
        }
    }

    /// <summary>
    ///     Reads a plan file, saved as acb wrote it or by a shell that added a byte order mark or wrote UTF-16. A plan
    ///     from another acb version is refused for that reason first: what each part says, which the fingerprint covers,
    ///     may be worded differently there, and its fields may differ.
    /// </summary>
    private static CommandReport? LoadPlan(string path, out string? problem)
    {
        problem = null;
        try
        {
            byte[] bytes = JsonText.Utf8(File.ReadAllBytes(path));
            using (JsonDocument document = JsonDocument.Parse(bytes))
            {
                JsonElement root = document.RootElement;
                string? format = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("format", out JsonElement f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                string? version = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("acbVersion", out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (format != CommandReport.CurrentFormat)
                {
                    problem = $"{path} is not a plan made by acb plan --json.";
                    return null;
                }

                if (version != Archives.Version())
                {
                    problem = $"the plan was made by acb {version ?? "of an earlier version"}, and this is acb {Archives.Version()}. Make a new plan with this acb and review it.";
                    return null;
                }
            }

            CommandReport? plan = JsonSerializer.Deserialize(bytes, CliJson.Default.CommandReport);
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
