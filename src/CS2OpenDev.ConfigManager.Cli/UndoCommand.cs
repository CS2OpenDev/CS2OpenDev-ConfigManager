using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>
///     <c>acb undo</c>: puts back what restores changed, from the restorer's own stores only, newest record first,
///     with an older record's part waiting while a newer one still holds it. Records are never read from an
///     archive or a folder the command was given. The administrator's store (the sections that need one, NVIDIA's
///     settings on Windows) is undone by an elevated run, offered the same way as for a restore.
/// </summary>
internal static class UndoCommand
{
    private const string AdminPrefix = "admin:";

    public static int Run(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "undo", ["list", "yes", "elevate", "json", "dry-run"],
            ["id", "only", "set-aside", "steam-path", "report"]);
        line.NoPositionals();
        Output output = new(host, "undo", line.Flag("json"), line.Value("report"));
        try
        {
            return Run(line, host, output);
        }
        catch (UndoStoreUnreachableException ex)
        {
            // Never "nothing to undo": the records may be there, out of reach.
            output.Note("Stopped: " + ex.Message + ". Run acb undo again once it can be reached.");
            return output.Finish(ExitCodes.RecordsNeedAttention);
        }
    }

    private static int Run(CommandLine line, AcbHost host, Output output)
    {
        List<string>? only = Archives.Sections(line.List("only"));
        IReadOnlyList<string>? ids = line.List("id");
        List<string>? userIds = ids is null ? null : [.. ids.Where(i => !i.StartsWith(AdminPrefix, StringComparison.Ordinal))];
        List<string>? adminIds = ids is null ? null : [.. ids.Where(i => i.StartsWith(AdminPrefix, StringComparison.Ordinal)).Select(i => i[AdminPrefix.Length..])];
        string? steamPath = line.Value("steam-path") is { } steam ? Path.GetFullPath(steam) : null;
        bool dryRun = line.Flag("dry-run");
        bool yes = line.Flag("yes");
        UndoStore user = host.UserStore;
        UndoStore? machine = host.MachineStore;
        ElevationState elevation = host.Elevation();
        bool elevated = elevation.Kind == ElevationKind.Elevated;

        if (line.Value("set-aside") is { } setAside)
        {
            return SetAside(setAside, user, elevated ? machine : null, yes, dryRun, host, output);
        }

        if (line.Flag("list"))
        {
            return List(user, machine, elevated, host, output);
        }

        IReadOnlyList<UndoRecord> waiting = user.Records();
        bool adminWaiting = machine is not null && (only is null || only.Any(RestoreRules.NeedsAdministrator))
                            && (ids is null || adminIds!.Count > 0) && machine.HasRecords();
        if (waiting.Count == 0 && !adminWaiting)
        {
            output.Note("Nothing to undo: no restore has changed this PC, or every one is already put back.");
            return output.Finish(ExitCodes.Done);
        }

        output.Line("Undo records waiting, newest first:");
        output.Report.Records = [];
        foreach (UndoRecord record in waiting)
        {
            Describe(record, "", output);
        }

        if (adminWaiting)
        {
            output.Line($"  administrator's records ({AdminLabels}), which an administrator puts back");
        }

        if (!dryRun && !yes)
        {
            if (!host.Interactive)
            {
                output.Note("Nothing was changed: there is no terminal to ask on. Run it again with --yes to put the PC back.");
                return output.Finish(ExitCodes.Refused);
            }

            if (!host.Confirm("Put these back as they were before those restores?"))
            {
                output.Note("Nothing was changed.");
                return output.Finish(ExitCodes.Refused);
            }
        }

        output.Line();
        output.Line(dryRun ? "What an undo would put back:" : "Putting back:");
        UndoRunOptions options = new()
        {
            Only = only,
            Ids = userIds,
            SteamRoot = steamPath,
            DryRun = dryRun,
            ProcessCheck = host.IsRunning,
            LocateSteam = host.LocateSteam,
            OnUnreadable = host.Interactive && !yes ? record => AskUnreadable(record, "", host) : null,
            // An administrator's acb writes nothing through the Steam folder, which other users can change.
            Leave = elevated ? Sections.All.Where(RestoreRules.LeftByAdministrator).ToDictionary(s => s, _ => RestoreCommand.SteamLeftDetail(elevation)) : null
        };
        int exit = ExitCodes.Done;
        if (ids is null || userIds!.Count > 0)
        {
            IReadOnlyList<RecordOutcome> outcomes = UndoRunner.Run(user, options, null);
            Report(outcomes, "", output);
            exit = ExitCodes.For(outcomes);
        }

        if (adminWaiting && !dryRun)
        {
            bool changed = output.Report.Sections.Any(l => l.Outcome == Outcomes.Done);
            exit = Math.Max(exit, RestoreCommand.AfterChanges(
                AdminPart(host, elevation, machine!, only, adminIds, yes, line.Flag("elevate"), output), changed));
        }

        output.Line();
        if (output.Report.Records?.Exists(r => r.State == "set-aside") == true)
        {
            output.Note("Records set aside were not put back: the changes they recorded stay on this PC.");
        }

        output.Note(exit switch
        {
            ExitCodes.Done => dryRun ? "Nothing was changed." : "Done: the PC is back as it was before those restores.",
            ExitCodes.RecordsNeedAttention => "Some records need attention (see above), and older records' parts wait for them.",
            _ => "Some parts are not put back yet (see above). Run acb undo again once the reason is gone."
        });
        return output.Finish(exit);
    }

    /// <summary>
    ///     The elevated run of <c>acb undo</c>: the administrator's store only, and no question, since the first run
    ///     asked. It takes no Steam folder: the driver's settings need none, and it opens no path a player named.
    /// </summary>
    public static int Elevated(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "undo (administrator)", [], ["only", "id", "pipe"]);
        line.NoPositionals();
        Output output = new(host, "undo", json: false, reportPath: null);
        using IElevatedChannel? channel = line.Value("pipe") is { } pipe ? host.ConnectElevated(pipe) : null;
        int code = ElevatedCore(line, host, output);
        channel?.Send(output.Report);
        return code;
    }

    private static int ElevatedCore(CommandLine line, AcbHost host, Output output)
    {
        if (host.Elevation().Kind != ElevationKind.Elevated)
        {
            return output.Refuse("the administrator's part runs only as an administrator.");
        }

        if (host.MachineStoreRoot is not { } root)
        {
            return output.Refuse("there is no administrator's undo store on this platform.");
        }

        try
        {
            host.EnsureMachineStore(root, host.MachineStoreParent!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return output.Refuse(ex.Message);
        }

        IReadOnlyList<RecordOutcome> outcomes = UndoRunner.Run(new UndoStore(root, isMachine: true), new UndoRunOptions
        {
            Only = Archives.Sections(line.List("only")),
            Ids = line.List("id") is { } ids ? [.. ids] : null,
            ProcessCheck = host.IsRunning,
            LocateSteam = host.LocateSteam
        }, null);
        Report(outcomes, AdminPrefix, output);
        return output.Finish(ExitCodes.For(outcomes));
    }

    private static int AdminPart(AcbHost host, ElevationState elevation, UndoStore machine, List<string>? only, List<string>? ids,
        bool yes, bool elevate, Output output)
    {
        if (elevation.Kind == ElevationKind.Elevated)
        {
            try
            {
                host.EnsureMachineStore(machine.Root, host.MachineStoreParent!);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                output.Note("The administrator's records were left: " + ex.Message);
                return ExitCodes.RecordsNeedAttention;
            }

            IReadOnlyList<RecordOutcome> outcomes = UndoRunner.Run(machine, new UndoRunOptions
            {
                Only = only,
                Ids = ids is { Count: > 0 } ? ids : null,
                ProcessCheck = host.IsRunning,
                LocateSteam = host.LocateSteam,
                OnUnreadable = host.Interactive && !yes ? record => AskUnreadable(record, AdminPrefix, host) : null
            }, null);
            Report(outcomes, AdminPrefix, output);
            return ExitCodes.For(outcomes);
        }

        IReadOnlyList<string> sections = [.. RestoreRules.AdministratorSections.Where(s => only is null || only.Contains(s))];
        if (!elevation.CanAsk)
        {
            foreach (string section in sections)
            {
                output.Result(section, Outcomes.Admin, "left: putting it back needs an administrator, and " + elevation.Detail);
            }

            return ExitCodes.PartsLeft;
        }

        if (!(elevate || (!yes && host.Confirm($"The administrator's records ({AdminLabels}) need an administrator. {elevation.Detail}. Ask Windows now?"))))
        {
            foreach (string section in sections)
            {
                output.Result(section, Outcomes.Admin, yes
                    ? "left: putting it back needs an administrator; pass --elevate to have Windows ask for one"
                    : "left: putting it back needs an administrator");
            }

            return ExitCodes.PartsLeft;
        }

        List<string> arguments = ["__elevated", "undo"];
        if (only is not null)
        {
            arguments.AddRange(["--only", string.Join(',', only.Where(RestoreRules.NeedsAdministrator))]);
        }

        foreach (string id in ids is { Count: > 0 } ? ids : [])
        {
            arguments.AddRange(["--id", id]);
        }

        ElevatedResult result = host.Elevator.Run(arguments, []);
        foreach (RecordLine record in result.Report?.Records ?? [])
        {
            output.Report.Records!.Add(record);
            if (record.State == "unreadable")
            {
                output.Line(UnreadableLine(record.Name, record.Problem ?? "no reason given", admin: true));
            }
            else if (record.State == "set-aside")
            {
                output.Line($"  {record.Name}: set aside in {UndoStore.SetAsideFolder}/; what it recorded was not put back");
            }
        }

        return RestoreCommand.Merge(result, sections, output, everySection: false);
    }

    /// <summary>What the administrator's records hold, by section label.</summary>
    private static string AdminLabels => string.Join(", ", RestoreRules.AdministratorSections.Select(Sections.Label));

    private static string UnreadableLine(string name, string problem, bool admin) => admin
        ? $"  {name}: cannot be read ({problem}); left in place. An administrator can run acb undo --set-aside {name} from an administrator's prompt, so older records go ahead without it"
        : $"  {name}: cannot be read ({problem}); left in place. acb undo --set-aside {name} lets older records go ahead without it";

    private static UnreadableChoice AskUnreadable(UndoRecord record, string prefix, AcbHost host)
    {
        host.Out.WriteLine($"Record {prefix}{record.Name} cannot be read ({record.Problem}). Until it is read or set aside, every older record's parts wait for it.");
        string? answer = host.Ask("Type r to read it again, s to set it aside (what it recorded is then not put back), or Enter alone to leave it:");
        return answer?.ToUpperInvariant() switch
        {
            "R" => UnreadableChoice.Retry,
            "S" => UnreadableChoice.SetAside,
            _ => UnreadableChoice.Leave
        };
    }

    private static void Report(IReadOnlyList<RecordOutcome> outcomes, string prefix, Output output)
    {
        output.Report.Records ??= [];
        foreach (RecordOutcome outcome in outcomes)
        {
            string name = prefix + outcome.Record.Name;
            if (outcome.SetAside)
            {
                output.Line($"  {name}: set aside in {UndoStore.SetAsideFolder}/; what it recorded was not put back");
                output.Report.Records.Add(new RecordLine(name, null, null, [], "set-aside"));
                continue;
            }

            if (outcome.Unreadable is not null)
            {
                output.Line(UnreadableLine(name, outcome.Unreadable, prefix.Length > 0));
                output.Report.Records.Add(new RecordLine(name, null, null, [], "unreadable", outcome.Unreadable));
                continue;
            }

            if (outcome.Report is null && !outcome.Filed && outcome.Left.Count > 0)
            {
                output.Report.Records.Add(new RecordLine(name, outcome.Record.Created, outcome.Record.ArchiveDigest, outcome.Left, "not chosen"));
                continue;
            }

            output.Line($"  {name}:");
            foreach (SectionResult result in outcome.Report?.Results ?? [])
            {
                output.Result(result);
            }

            foreach (string section in outcome.Waiting)
            {
                output.Result(section, Outcomes.Left, "waiting: a newer record of the same part has to be put back first");
            }

            if (outcome.FileProblem is not null)
            {
                output.Line($"    put back, but could not be moved into {UndoStore.AppliedFolder}/ ({outcome.FileProblem}); the next acb undo files it");
            }

            output.Report.Records.Add(new RecordLine(name, outcome.Record.Created, outcome.Record.ArchiveDigest, outcome.Left,
                outcome.Filed ? "applied" : outcome.Left.Count > 0 ? "parts left" : "put back"));
        }
    }

    private static void Describe(UndoRecord record, string prefix, Output output)
    {
        string name = prefix + record.Name;
        string state = !record.IsReadable ? "unreadable" : record.Manifest is null ? "empty" : "waiting";
        output.Report.Records!.Add(new RecordLine(name, record.Created, record.ArchiveDigest, record.Parts, state, record.IsReadable ? null : record.Problem));
        output.Line(!record.IsReadable
            ? $"  {name}: cannot be read ({record.Problem})"
            : record.Manifest is null
                ? $"  {name}: holds no record, so that restore changed nothing"
                : $"  {name}: made {record.Created} from {record.Manifest.Get("archive") ?? "a backup"}; holds {string.Join(", ", record.Parts.Select(Sections.Label))}");
    }

    /// <summary>
    ///     Moves a record aside so older records go ahead: one that cannot be read, or one that can, when everything it
    ///     still holds was written through a Steam folder whose Steam, account or CS2 install is gone, so it does not
    ///     hold older records back forever, or is an administrator's record of driver settings, which a driver after
    ///     an update may refuse for good. The second kind is asked about, naming what is then never put back. A dry
    ///     run says what would be set aside and moves nothing.
    /// </summary>
    private static int SetAside(string id, UndoStore user, UndoStore? machine, bool yes, bool dryRun, AcbHost host, Output output)
    {
        bool admin = id.StartsWith(AdminPrefix, StringComparison.Ordinal);
        UndoStore? store = admin ? machine : user;
        if (store is null)
        {
            return output.Refuse($"{id} is an administrator's record; an administrator sets it aside, running acb undo --set-aside {id} from an administrator's prompt.");
        }

        // A dry run never makes the administrator's store; one that is not there holds no record to name.
        if (admin && !(dryRun && !Directory.Exists(store.Root)) && MachineStoreProblem(store, host) is { } problem)
        {
            return output.Refuse("the administrator's store was not used: " + problem);
        }

        UndoRecord? record = store.Find(admin ? id[AdminPrefix.Length..] : id);
        if (record is null)
        {
            return output.Refuse($"there is no undo record {id}. acb undo --list shows them.");
        }

        if (record.IsReadable && record.Parts.Count > 0)
        {
            bool driverRecord = admin && record.Parts.All(RestoreRules.NeedsAdministrator);
            if (!driverRecord)
            {
                if (!record.Parts.All(Sections.NeedsSteam))
                {
                    return output.Refuse($"{id} can be read, so it is put back with acb undo rather than set aside. A record that can be read is set aside "
                                         + "only when everything it still holds was written through a Steam folder whose Steam, account or CS2 install is gone, "
                                         + "or is an administrator's record of NVIDIA settings this driver will not take back.");
                }

                List<string> stillThere = [.. record.Parts.Where(p => ConfigBackup.StrandedReason(record.Manifest!, p) is null)];
                if (stillThere.Count > 0)
                {
                    return output.Refuse($"{id} can still be put back ({string.Join(", ", stillThere.Select(Sections.Label))}): the Steam folder, account "
                                         + "and CS2 install its restore wrote to are still there. Run acb undo; a part left for later (CS2 running) goes "
                                         + "back once CS2 is closed.");
                }

                foreach (string part in record.Parts)
                {
                    output.Line($"  {Sections.Label(part)}: {ConfigBackup.StrandedReason(record.Manifest!, part)}");
                }
            }

            output.Line($"{id} can be read. Set aside, what it holds ({string.Join(", ", record.Parts.Select(p => Holds(record, p)))}) is never put back.");
            if (dryRun)
            {
                output.Note("Nothing was changed: a dry run sets nothing aside.");
                return output.Finish(ExitCodes.Done);
            }

            if (!yes)
            {
                if (!host.Interactive)
                {
                    output.Note("Nothing was changed: there is no terminal to ask on. Run it again with --yes to set it aside.");
                    return output.Finish(ExitCodes.Refused);
                }

                if (!host.Confirm("Set it aside, so these parts are never put back?"))
                {
                    output.Note("Nothing was changed.");
                    return output.Finish(ExitCodes.Refused);
                }
            }
        }

        if (dryRun)
        {
            output.Note($"{id} would be set aside{(record.IsReadable ? "" : $" ({record.Problem})")}; a dry run sets nothing aside.");
            return output.Finish(ExitCodes.Done);
        }

        store.SetAside(record);
        output.Note($"{id} is set aside in {Path.Combine(store.Root, UndoStore.SetAsideFolder)}; older records no longer wait for it.");
        return output.Finish(ExitCodes.Done);
    }

    /// <summary>A part's label, with the setting ids an NVIDIA part still holds, so the person sees what stays changed.</summary>
    private static string Holds(UndoRecord record, string section)
    {
        if (section != Sections.Nvidia || record.Manifest?.Path(Sections.Nvidia, "cs2", "settings") is not { } settings)
        {
            return Sections.Label(section);
        }

        return Sections.Label(section) + ": " + string.Join(", ", settings.Children.Select(c => c.Key));
    }

    /// <summary>
    ///     Checks the administrator's store from its parent down before an administrator's acb reads or moves
    ///     anything in it, so a folder or link a player made there first is refused; null when it may be used.
    /// </summary>
    private static string? MachineStoreProblem(UndoStore machine, AcbHost host)
    {
        try
        {
            host.EnsureMachineStore(machine.Root, host.MachineStoreParent!);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return ex.Message;
        }
    }

    private static int List(UndoStore user, UndoStore? machine, bool elevated, AcbHost host, Output output)
    {
        output.Report.Records = [];
        IReadOnlyList<UndoRecord> records = user.Records();
        output.Line(records.Count == 0 ? "No undo records wait in " + user.Root : "Undo records in " + user.Root + ", newest first:");
        foreach (UndoRecord record in records)
        {
            Describe(record, "", output);
        }

        bool unreadable = records.Any(r => !r.IsReadable);
        if (machine is not null && elevated && Directory.Exists(machine.Root) && MachineStoreProblem(machine, host) is { } problem)
        {
            output.Line("The administrator's records were not read: " + problem);
            unreadable = true;
        }
        else if (machine is not null && elevated && Directory.Exists(machine.Root))
        {
            IReadOnlyList<UndoRecord> admin = machine.Records();
            output.Line(admin.Count == 0 ? "No administrator's records wait." : "Administrator's records in " + machine.Root + ":");
            foreach (UndoRecord record in admin)
            {
                Describe(record, AdminPrefix, output);
            }

            unreadable |= admin.Any(r => !r.IsReadable);
        }
        else if (machine?.HasRecords() == true)
        {
            output.Line($"Administrator's records ({AdminLabels}) wait too; acb run as administrator lists them.");
        }

        return output.Finish(unreadable ? ExitCodes.RecordsNeedAttention : ExitCodes.Done);
    }
}
