using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>
///     <c>acb undo</c>: puts back what restores changed, from the restorer's own stores only, newest record first,
///     with an older record's part waiting while a newer one still holds it. Records are never read from an
///     archive or a folder the command was given. The administrator's store (NVIDIA settings) is undone by an
///     elevated run, offered the same way as for a restore.
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
            return SetAside(setAside, user, elevated ? machine : null, output);
        }

        if (line.Flag("list"))
        {
            return List(user, machine, elevated, output);
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
            output.Line("  administrator's records (NVIDIA settings), which an administrator puts back");
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
            OnUnreadable = host.Interactive && !yes ? record => AskUnreadable(record, host) : null
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
            exit = Math.Max(exit, AdminPart(host, elevation, machine!, only, adminIds, steamPath, yes, line.Flag("elevate"), output));
        }

        output.Line();
        output.Note(exit switch
        {
            ExitCodes.Done => dryRun ? "Nothing was changed." : "Done: the PC is back as it was before those restores.",
            ExitCodes.RecordsNeedAttention => "Some records need attention (see above). Nothing they hold was put back, and older records' parts wait for them.",
            _ => "Some parts are not put back yet (see above). Run acb undo again once the reason is gone."
        });
        return output.Finish(exit);
    }

    /// <summary>The elevated run of <c>acb undo</c>: the administrator's store only, and no question, since the first run asked.</summary>
    public static int Elevated(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "undo (administrator)", [], ["only", "id", "steam-path", "pipe"]);
        line.NoPositionals();
        Output output = new(host, "undo", json: false, reportPath: null);
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
            SteamRoot = line.Value("steam-path"),
            ProcessCheck = host.IsRunning
        }, null);
        Report(outcomes, AdminPrefix, output);
        return output.Finish(ExitCodes.For(outcomes));
    }

    private static int AdminPart(AcbHost host, ElevationState elevation, UndoStore machine, List<string>? only, List<string>? ids,
        string? steamPath, bool yes, bool elevate, Output output)
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
                SteamRoot = steamPath,
                ProcessCheck = host.IsRunning
            }, null);
            Report(outcomes, AdminPrefix, output);
            return ExitCodes.For(outcomes);
        }

        if (!elevation.CanAsk)
        {
            output.Result(Sections.Nvidia, "admin", "left: putting it back needs an administrator, and " + elevation.Detail);
            return ExitCodes.PartsLeft;
        }

        if (!(elevate || (!yes && host.Confirm($"The administrator's records (NVIDIA settings) need an administrator. {elevation.Detail}. Ask Windows now?"))))
        {
            output.Result(Sections.Nvidia, "admin", yes
                ? "left: putting it back needs an administrator; pass --elevate to have Windows ask for one"
                : "left: putting it back needs an administrator");
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

        if (steamPath is not null)
        {
            arguments.AddRange(["--steam-path", steamPath]);
        }

        return RestoreCommand.Merge(host.Elevator.Run(arguments), [Sections.Nvidia], output);
    }

    private static UnreadableChoice AskUnreadable(UndoRecord record, AcbHost host)
    {
        host.Out.WriteLine($"Record {record.Name} cannot be read ({record.Problem}). Until it is read or set aside, every older record's parts wait for it.");
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
                output.Line($"  {name}: cannot be read ({outcome.Unreadable}); left in place. acb undo --set-aside {name} lets older records go ahead without it");
                output.Report.Records.Add(new RecordLine(name, null, null, [], "unreadable"));
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
                output.Result(section, "left", "waiting: a newer record of the same part has to be put back first");
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
        output.Report.Records!.Add(new RecordLine(name, record.Created, record.ArchiveDigest, record.Parts, state));
        output.Line(!record.IsReadable
            ? $"  {name}: cannot be read ({record.Problem})"
            : record.Manifest is null
                ? $"  {name}: holds no record, so that restore changed nothing"
                : $"  {name}: made {record.Created} from {record.Manifest.Get("archive") ?? "a backup"}; holds {string.Join(", ", record.Parts.Select(Sections.Label))}");
    }

    private static int SetAside(string id, UndoStore user, UndoStore? machine, Output output)
    {
        bool admin = id.StartsWith(AdminPrefix, StringComparison.Ordinal);
        UndoStore? store = admin ? machine : user;
        if (store is null)
        {
            return output.Refuse($"{id} is an administrator's record; set it aside from acb run as administrator.");
        }

        UndoRecord? record = store.Find(admin ? id[AdminPrefix.Length..] : id);
        if (record is null)
        {
            return output.Refuse($"there is no undo record {id}. acb undo --list shows them.");
        }

        if (record.IsReadable && record.Parts.Count > 0)
        {
            return output.Refuse($"{id} can be read, so it is put back with acb undo rather than set aside.");
        }

        store.SetAside(record);
        output.Note($"{id} is set aside in {Path.Combine(store.Root, UndoStore.SetAsideFolder)}; older records no longer wait for it.");
        return output.Finish(ExitCodes.Done);
    }

    private static int List(UndoStore user, UndoStore? machine, bool elevated, Output output)
    {
        output.Report.Records = [];
        IReadOnlyList<UndoRecord> records = user.Records();
        output.Line(records.Count == 0 ? "No undo records wait in " + user.Root : "Undo records in " + user.Root + ", newest first:");
        foreach (UndoRecord record in records)
        {
            Describe(record, "", output);
        }

        bool unreadable = records.Any(r => !r.IsReadable);
        if (machine is not null && elevated && Directory.Exists(machine.Root))
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
            output.Line("Administrator's records (NVIDIA settings) wait too; acb run as administrator lists them.");
        }

        return output.Finish(unreadable ? ExitCodes.RecordsNeedAttention : ExitCodes.Done);
    }
}
