using System.Text.Json;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     The administrator's side of acb on Windows: its undo store, its records in <c>acb undo</c>, and how an
///     administrator's run that refuses is weighed against what the player's run already changed. No test here lets
///     the NVIDIA section reach the driver for writing: records are empty or unreadable, and the elevated run is the
///     stand-in.
/// </summary>
public class AdministratorTests
{
    private const string Autoexec = "autoexec.cfg";
    private const string RecordName = "000001-20261101-180000Z";

    private static readonly ElevationState Elevated = new(ElevationKind.Elevated, "acb runs as an administrator");

    [Test]
    public async Task SetAside_OfAnAdministratorsRecord_ChecksTheStoreFirst_AndMovesNothingWhenItFails()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new() { Elevation = Elevated };
        string record = PlantRecord(host, null);
        host.MachineStoreProblem = new UnauthorizedAccessException("C:\\ProgramData\\CS2OpenDev is owned by PC\\player, not by administrators");

        int refused = host.Run("undo", "--set-aside", "admin:" + RecordName);

        await Assert.That(refused).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.MachineStoreChecks.Single()).IsEqualTo(host.MachineStore);
        await Assert.That(Directory.Exists(record)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(host.MachineStore, UndoStore.SetAsideFolder))).IsFalse();

        int listed = host.Run("undo", "--list");

        await Assert.That(listed).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        await Assert.That(host.Output).Contains("The administrator's records were not read");
        await Assert.That(host.Output).DoesNotContain("admin:" + RecordName);

        host.MachineStoreProblem = null;
        await Assert.That(host.Run("undo", "--list")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("admin:" + RecordName);
        await Assert.That(host.Run("undo", "--set-aside", "admin:" + RecordName)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(Directory.Exists(Path.Combine(host.MachineStore, UndoStore.SetAsideFolder, RecordName))).IsTrue();
    }

    [Test]
    public async Task AsAPlayer_TheAdministratorsRecords_AreNamedButNotRead_AndSetAsideSaysWhoCan()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        PlantRecord(host, NvidiaRecord);

        await Assert.That(host.Run("undo", "--list")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("Administrator's records (NVIDIA 3D settings for CS2) wait too");
        await Assert.That(host.MachineStoreChecks).IsEmpty();

        await Assert.That(host.Run("undo", "--set-aside", "admin:" + RecordName)).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("from an administrator's prompt");
    }

    /// <summary>
    ///     A readable record of NVIDIA settings a driver will not take back (one updated since) can be set aside by an
    ///     administrator, so older records go ahead; only when asked, and naming the ids that then stay changed.
    /// </summary>
    [Test]
    public async Task SetAside_OfAReadableNvidiaRecord_AsAnAdministrator_IsAskedFor_AndNamesItsSettings()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new() { Elevation = Elevated };
        string record = PlantRecord(host, NvidiaRecord);

        int unasked = host.Run("undo", "--set-aside", "admin:" + RecordName);

        await Assert.That(unasked).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(Directory.Exists(record)).IsTrue();
        await Assert.That(host.Output).Contains("NVIDIA 3D settings for CS2: 0x10835002");

        int code = host.Run("undo", "--set-aside", "admin:" + RecordName, "--yes");

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(Directory.Exists(Path.Combine(host.MachineStore, UndoStore.SetAsideFolder, RecordName))).IsTrue();
    }

    /// <summary>
    ///     A driver that stops answering does not hold the plan: after the limit the NVIDIA part is left, said to have
    ///     changed nothing, and the run ends. Not in parallel, since the driver's worker is shared by the process.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task Plan_WithADriverThatStopsAnswering_LeavesTheNvidiaPart_AndEnds()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using ManualResetEventSlim release = new();
        using ManualResetEventSlim returned = new();
        using TestHost host = new()
        {
            DriverTimeoutMs = 300,
            StartNvidia = (out bool mayPass) =>
            {
                release.Wait();
                mayPass = true;
                returned.Set();
                return "no NVIDIA driver is installed";
            }
        };
        using FakeSteam target = new();
        int code;
        try
        {
            code = host.Run("plan", "--steam-path", target.Root, FailurePolicyTests.NvidiaArchive(host.Temp));
        }
        finally
        {
            release.Set();
            returned.Wait(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
        }

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("the NVIDIA driver did not answer");
        await Assert.That(host.Output).Contains("nothing of this part was changed");
    }

    /// <summary>
    ///     Vibrance starts the driver as the NVIDIA part does: a driver that may yet start (updating, recovering)
    ///     leaves the part for later with exit 2, and only a lasting failure (no NVIDIA GPU) skips it.
    /// </summary>
    [Test]
    [Arguments(true, "deferred")]
    [Arguments(false, "skipped")]
    public async Task Plan_VibranceWithADriverThatDidNotStart_IsLeftOnlyWhenItMayPass(bool mayPass, string outcome)
    {
        if (SkipOffWindows())
        {
            return;
        }

        bool passes = mayPass;
        using TestHost host = new()
        {
            StartNvidia = (out bool may) =>
            {
                may = passes;
                return passes ? "the NVIDIA driver did not start (NVAPI -1); it may be updating or recovering" : "no NVIDIA GPU drives a display here";
            }
        };
        using FakeSteam target = new();
        string archive = TestArchives.Craft(host.Temp, """
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"vibrance"
            	{
            		"0"		"80"
            	}
            }
            """);

        int code = host.Run("plan", "--json", "--steam-path", target.Root, archive);

        await Assert.That(code).IsEqualTo(mayPass ? ExitCodes.PartsLeft : ExitCodes.Done).Because(host.All);
        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Sections.Single(l => l.Section == "vibrance").Outcome).IsEqualTo(outcome);
        await Assert.That(host.All).DoesNotContain("NVIDIA displays");
    }

    /// <summary>
    ///     An administrator's restore that ends cleanly but does not report a section it was given leaves that
    ///     section: the run says so, its JSON holds an "admin" line for it, and it exits 2, not 0.
    /// </summary>
    [Test]
    public async Task Restore_TheAdministratorsRunEndsCleanlyWithoutASection_LeavesIt_AndExits2()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);
        host.Elevator.Answer = new ElevatedResult(false, ExitCodes.Done, new CommandReport { Sections = [] }, null);

        int code = host.Run("restore", "--yes", "--elevate", "--json", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Sections.Single(l => l.Section == Sections.Nvidia).Outcome).IsEqualTo(Outcomes.Admin);
        await Assert.That(report.Sections.Single(l => l.Section == Sections.Cs2Game).Outcome).IsEqualTo(Outcomes.Done);
    }

    /// <summary>The arguments the administrator's undo is started with: only its sections, its record ids without the prefix, and the Steam folder.</summary>
    [Test]
    public async Task Undo_AsAPlayer_AsksWindowsForTheAdministratorsRecords_WithTheirIds()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        PlantRecord(host, NvidiaRecord);

        int code = host.Run("undo", "--yes", "--elevate", "--id", "admin:1", "--only", "cs2game,nvidia", "--steam-path", target.Root);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(string.Join('|', host.Elevator.Calls.Single()))
            .IsEqualTo(string.Join('|', "__elevated", "undo", "--only", "nvidia", "--id", "1"))
            .Because("the driver's settings need no Steam folder, and the administrator's run opens no path a player named");
        await Assert.That(host.Output).Contains("the Windows prompt was cancelled or refused");
    }

    [Test]
    public async Task Undo_AsAnAdministrator_ChecksTheStore_ThenFilesARecordThatHoldsNothing()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new() { Elevation = Elevated };
        PlantRecord(host, EmptyRecord);
        host.MachineStoreProblem = new IOException("C:\\ProgramData\\CS2OpenDev\\ConfigBackup\\undo is a link");

        await Assert.That(host.Run("__elevated", "undo")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(Directory.Exists(Path.Combine(host.MachineStore, RecordName))).IsTrue();

        host.MachineStoreProblem = null;
        int code = host.Run("undo", "--yes");

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.MachineStoreChecks.Count).IsEqualTo(2);
        await Assert.That(Directory.Exists(Path.Combine(host.MachineStore, UndoStore.AppliedFolder, RecordName))).IsTrue();
    }

    [Test]
    public async Task Undo_AsAnAdministrator_LeavesAnUnreadableRecord_AndSaysAnAdministratorSetsItAside()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new() { Elevation = Elevated };
        string record = PlantRecord(host, "{{{ damaged");

        int code = host.Run("undo", "--yes");

        await Assert.That(code).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        await Assert.That(host.Output).Contains("An administrator can run acb undo --set-aside admin:" + RecordName);
        await Assert.That(Directory.Exists(record)).IsTrue();
    }

    /// <summary>An administrator at the terminal is asked about an unreadable administrator's record, as a player is about their own.</summary>
    [Test]
    public async Task Undo_AsAnAdministratorAtATerminal_OffersToSetAsideAnUnreadableRecord()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new() { Elevation = Elevated, Interactive = true };
        string record = PlantRecord(host, "{{{ damaged");
        host.Answers.Enqueue("y");
        host.Answers.Enqueue("s");

        int code = host.Run("undo");

        await Assert.That(code).IsNotEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("Record admin:" + RecordName + " cannot be read");
        await Assert.That(Directory.Exists(record)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(host.MachineStore, UndoStore.SetAsideFolder, RecordName))).IsTrue();
    }

    /// <summary>The administrator's run closes its own console, so the records it reports are shown and kept by the player's run.</summary>
    [Test]
    public async Task Undo_AsAPlayer_ShowsAnUnreadableRecordTheAdministratorsRunFound()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        PlantRecord(host, NvidiaRecord);
        host.Elevator.Answer = new ElevatedResult(false, ExitCodes.RecordsNeedAttention, new CommandReport
        {
            Records = [new RecordLine("admin:" + RecordName, null, null, [], "unreadable", "backup.vdf is damaged")]
        }, null);

        int code = host.Run("undo", "--yes", "--elevate", "--json");

        await Assert.That(code).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        RecordLine line = report.Records!.Single(r => r.Name == "admin:" + RecordName);
        await Assert.That(line.State).IsEqualTo("unreadable");
        await Assert.That(line.Problem).IsEqualTo("backup.vdf is damaged");
        await Assert.That(host.Errors + host.Output).Contains("acb undo --set-aside admin:" + RecordName + " from an administrator's prompt");
    }

    /// <summary>An administrator's undo that ends cleanly with nothing to put back leaves no NVIDIA part behind.</summary>
    [Test]
    public async Task Undo_AnAdministratorsRunThatFilesOnlyAnEmptyRecord_ReportsNothingLeft()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        PlantRecord(host, EmptyRecord);
        host.Elevator.Answer = new ElevatedResult(false, ExitCodes.Done, new CommandReport
        {
            Records = [new RecordLine("admin:" + RecordName, null, null, [], "applied")]
        }, null);

        int code = host.Run("undo", "--yes", "--elevate", "--json");

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Sections.Where(l => l.Outcome == "admin")).IsEmpty().Because(host.All);
        await Assert.That(report.Records!.Single(r => r.Name == "admin:" + RecordName).State).IsEqualTo("applied");
        await Assert.That(host.All).DoesNotContain("did not apply it");
    }

    /// <summary>
    ///     An administrator's run that refuses after the player's sections were written leaves its sections: the run
    ///     is "done with parts left", never "refused, nothing changed". With nothing else changed, it stays refused.
    /// </summary>
    [Test]
    [Arguments("report")]
    [Arguments("no report")]
    [Arguments("nothing else")]
    public async Task Restore_AnAdministratorsRunThatRefuses_AfterThePlayersPartsChanged_Exits2(string how)
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);
        host.Elevator.Answer = how == "no report"
            ? new ElevatedResult(false, ExitCodes.Usage, null, "the administrator's run ended without a report", Started: true)
            : new ElevatedResult(false, ExitCodes.Refused,
                new CommandReport { Notes = ["Refused: the backup is not the one the first run checked: it was replaced in between. Nothing was changed."] }, null);

        int code = how == "nothing else"
            ? host.Run("restore", "--yes", "--elevate", "--json", "--only", "nvidia", "--steam-path", target.Root, zip)
            : host.Run("restore", "--yes", "--elevate", "--json", "--steam-path", target.Root, zip);

        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Sections.Where(l => l.Section == Sections.Nvidia).Select(l => l.Outcome)).Contains("admin").Because(host.All);
        if (how == "nothing else")
        {
            await Assert.That(code).IsEqualTo(ExitCodes.Refused).Because(host.All);
            await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
            return;
        }

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(report.ExitCode).IsEqualTo(ExitCodes.PartsLeft);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec");
    }

    /// <summary>
    ///     An administrator's run that started and sent no report (stopped with Task Manager, its window closed, or
    ///     its pipe lost) may have changed NVIDIA's settings whatever its exit code, so its part is unknown and the
    ///     run exits 4, never 1 ("nothing changed") or 2.
    /// </summary>
    [Test]
    [Arguments("restore", 1)]
    [Arguments("restore", 0)]
    [Arguments("restore", -1073741510)]
    [Arguments("undo", 1)]
    [Arguments("undo", 0)]
    public async Task AnAdministratorsRunThatEndsWithoutAReport_IsUnknown_AndExits4(string command, int exitCode)
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        host.Elevator.Answer = new ElevatedResult(false, exitCode, null, "the administrator's run ended without a report", Started: true);
        int code;
        if (command == "restore")
        {
            using FakeSteam target = new();
            code = host.Run("restore", "--yes", "--elevate", "--json", "--only", "nvidia", "--steam-path", target.Root, FailurePolicyTests.NvidiaArchive(host.Temp));
        }
        else
        {
            PlantRecord(host, NvidiaRecord);
            code = host.Run("undo", "--yes", "--elevate", "--json");
        }

        await Assert.That(code).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        SectionLine nvidia = report.Sections.Single(l => l.Section == Sections.Nvidia && l.Outcome == "admin");
        await Assert.That(nvidia.Detail).Contains("may have changed this part");
        await Assert.That(nvidia.Detail).Contains("exit code " + exitCode);
        await Assert.That(host.All).DoesNotContain("the PC is back as it was");
    }

    /// <summary>
    ///     On a PC where the account runs everything as an administrator (User Account Control off), "run acb without
    ///     administrator" cannot be followed, so the parts written through the Steam folder say what can be done.
    /// </summary>
    [Test]
    public async Task AsAnAdministratorWithNoOtherKindOfPrompt_TheSteamFoldersPartsSayWhatCanBeDone()
    {
        using TestHost host = new() { Elevation = new ElevationState(ElevationKind.Elevated, "acb runs as an administrator", AlwaysElevated: true) };
        using FakeSteam source = new();
        using FakeSteam target = new();
        source.Write(FakeSteam.Cfg(Autoexec), "echo restored");
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("every program this account starts runs as an administrator");
        await Assert.That(host.Output).Contains("signed in to Windows with a standard account");
        await Assert.That(host.Output).DoesNotContain("Run acb without administrator");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
    }

    /// <summary>
    ///     On a PC with no NVIDIA driver the NVIDIA part has nothing to do, then or later: the plan and the restore
    ///     skip it, and no administrator is offered or asked for.
    /// </summary>
    [Test]
    public async Task OnAPcWithoutAnNvidiaDriver_TheNvidiaPartIsSkipped_AndNoAdministratorIsAsked()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new()
        {
            StartNvidia = (out bool mayPass) =>
            {
                mayPass = false;
                return "no NVIDIA driver is installed";
            }
        };
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);

        int restore = host.Run("restore", "--yes", "--elevate", "--json", "--steam-path", target.Root, zip);
        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        int plan = host.Run("plan", "--steam-path", target.Root, zip);

        await Assert.That(plan).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(restore).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Elevator.Calls).IsEmpty();
        await Assert.That(host.All).DoesNotContain("needs an administrator");
        SectionLine nvidia = report.Sections.Single(l => l.Section == Sections.Nvidia);
        await Assert.That(nvidia.Outcome).IsEqualTo("skipped");
        await Assert.That(nvidia.Detail).IsEqualTo("no NVIDIA driver is installed");
    }

    [Test]
    public async Task Undo_AnAdministratorsRunThatRefuses_AfterThePlayersRecordsWentBack_Exits2()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        PlantRecord(host, NvidiaRecord);
        host.Elevator.Answer = new ElevatedResult(false, ExitCodes.Refused, new CommandReport { Notes = ["Refused: the store failed its check."] }, null);

        int code = host.Run("undo", "--yes", "--elevate", "--steam-path", target.Root);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
        await Assert.That(host.Output).Contains("the administrator's run did not apply it");
    }

    /// <summary>
    ///     A plan previews the NVIDIA section with the engine, which only reads the driver, so it is part of what the
    ///     plan shows and fingerprints; an administrator's plan never calls a section it has not applied done.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Plan_PreviewsTheNvidiaSection_AndNeverCallsItApplied(bool elevated)
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        if (elevated)
        {
            host.Elevation = Elevated;
        }

        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);

        int code = host.Run("plan", "--json", "--steam-path", target.Root, zip);

        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        List<SectionLine> nvidia = [.. report.Sections.Where(l => l.Section == Sections.Nvidia)];
        await Assert.That(nvidia.Where(l => l.Outcome != "admin")).IsNotEmpty().Because(host.All);
        await Assert.That(host.All).DoesNotContain("applied by this run");
        await Assert.That(nvidia.Where(l => l.Detail.StartsWith("changed", StringComparison.Ordinal))).IsEmpty();
        await Assert.That(nvidia.Any(l => l.Outcome == "admin")).IsEqualTo(!elevated);
        await Assert.That(code).IsNotEqualTo(ExitCodes.Refused);
        await Assert.That(host.MachineStoreChecks).IsEmpty();
    }

    /// <summary>
    ///     An administrator's acb writes nothing through the Steam folder, which any user may change: a junction a
    ///     player planted there would turn the write into one anywhere on the PC. Those parts are left, restore and
    ///     undo alike, and the record keeps them for a run without administrator.
    /// </summary>
    [Test]
    public async Task AsAnAdministrator_TheSteamFoldersPartsAreLeft_RestoreAndUndo()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        source.Write(FakeSteam.Cfg(Autoexec), "echo restored");
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);

        host.Elevation = Elevated;
        int restore = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(restore).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("writes nothing through the Steam folder");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
        await Assert.That(host.Records()).IsEmpty();

        host.Elevation = new ElevationState(ElevationKind.NotNeeded, "");
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        host.Elevation = Elevated;
        int undo = host.Run("undo", "--yes");

        await Assert.That(undo).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo restored");
        await Assert.That(host.Records().Single().Parts).Contains(Sections.Cs2Game);

        host.Elevation = new ElevationState(ElevationKind.NotNeeded, "");
        await Assert.That(host.Run("undo", "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
    }

    /// <summary>
    ///     The administrator's run is handed the backup's and the policy's bytes over its pipe and given no Steam
    ///     folder or account, so it opens no path the player named.
    /// </summary>
    [Test]
    public async Task Restore_HandsTheAdministratorsRunTheBytes_AndNoPath()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);
        string policy = host.Temp.Sub("policy.json");
        await File.WriteAllTextAsync(policy, """{ "sections": { "allow": ["nvidia"] } }""");

        await Assert.That(host.Run("restore", "--yes", "--elevate", "--policy", policy, "--account", FakeSteam.AccountId, "--steam-path", target.Root, zip))
            .IsEqualTo(ExitCodes.PartsLeft).Because(host.All);

        IReadOnlyList<string> call = host.Elevator.Calls.Single();
        await Assert.That(call).DoesNotContain("--steam-path");
        await Assert.That(call).DoesNotContain("--account");
        IReadOnlyList<byte[]?> input = host.Elevator.Inputs.Single();
        await Assert.That(input[0]).IsEquivalentTo(await File.ReadAllBytesAsync(zip));
        await Assert.That(input[1]).IsEquivalentTo(await File.ReadAllBytesAsync(policy));
    }

    /// <summary>What travels over the pipe is read back whole, and a cut-off or oversized input is refused, never half-read.</summary>
    [Test]
    public async Task TheElevatedRunsInput_RoundTrips_AndATruncatedOneIsRefused()
    {
        byte[] encoded = ElevatedInput.Encode([[1, 2, 3], null]);

        List<byte[]?>? decoded = ElevatedInput.Decode(new MemoryStream(encoded));

        await Assert.That(decoded).IsNotNull();
        await Assert.That(decoded![0]).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(decoded[1]).IsNull();
        await Assert.That(ElevatedInput.Decode(new MemoryStream(encoded[..^5]))).IsNull();
        await Assert.That(ElevatedInput.Decode(new MemoryStream(ElevatedInput.Encode([null, null, null, null, null])))).IsNull();
    }

    // -- Helpers ------------------------------------------------------------------------------------------

    private const string EmptyRecord = "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n\t\"kind\"\t\t\"undo\"\n\t\"created\"\t\t\"2026-11-01 18:00:00Z\"\n}\n";

    /// <summary>A record of the NVIDIA section, never applied by these tests: only a stand-in elevated run sees it.</summary>
    private const string NvidiaRecord = "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n\t\"kind\"\t\t\"undo\"\n\t\"created\"\t\t\"2026-11-01 18:00:00Z\"\n"
                                        + "\t\"nvidia\"\n\t{\n\t\t\"cs2\"\n\t\t{\n\t\t\t\"settings\"\n\t\t\t{\n\t\t\t\t\"0x10835002\"\n\t\t\t\t{\n\t\t\t\t\t\"absent\"\t\t\"1\"\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n";

    /// <summary>A record folder in the administrator's store, as a player could make one: with this backup.vdf, or none.</summary>
    private static string PlantRecord(TestHost host, string? backupVdf)
    {
        string dir = Path.Combine(host.MachineStore, RecordName);
        Directory.CreateDirectory(dir);
        if (backupVdf is not null)
        {
            File.WriteAllText(Path.Combine(dir, ConfigBackup.ManifestName), backupVdf);
        }

        return dir;
    }

    private static bool SkipOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Skip.Test("only Windows has an administrator's store");
        return true;
    }
}
