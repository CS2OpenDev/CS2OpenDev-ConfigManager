using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The Windows and driver sections restored, rolled back and undone against a fake machine: captured from one
///     fake PC, restored onto another, and put back. Windows only, as those sections are.
/// </summary>
[NotInParallel("nvidia-driver-worker")]
public class MachineSectionsTests
{
    private const uint FrameRateLimiter = 0x10835002;
    private const string GameBar = @"Software\Microsoft\GameBar";
    private const string GameConfig = @"System\GameConfigStore";
    private const string GpuPreferences = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static readonly string[] MachineSections = [Sections.Input, Sections.GameBar, Sections.Cs2Exe, Sections.Power, Sections.Nvidia, Sections.Vibrance];

    private static bool SkipOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        Skip.Test("the machine-wide sections are Windows only");
        return true;
    }

    /// <summary>A player's PC whose settings differ from the target's in every section.</summary>
    private static FakeMachine PlayersPc(FakeSteam steam)
    {
        FakeMachine pc = new() { ActivePlan = FakeMachine.HighPerformance };
        pc.Hotkeys["stickykeys"] = "0";
        pc.Registry[FakeMachine.Key(GameBar, "AutoGameModeEnabled")] = ("dword", "0");
        pc.Registry[FakeMachine.Key(GameConfig, "GameDVR_Enabled")] = ("dword", "0");
        pc.Registry[FakeMachine.Key(GpuPreferences, Cs2Exe(steam))] = ("string", WindowsSettings.HighPerformanceGpu);
        pc.Cs2User[FrameRateLimiter] = new NvSetting(FrameRateLimiter, "Frame Rate Limiter", "dword", "240");
        pc.Vibrance[0] = [80, 0, 100];
        return pc;
    }

    /// <summary>The PC restored onto: its own values, and a frame rate limit on its global profile.</summary>
    private static FakeMachine TargetPc()
    {
        FakeMachine pc = new();
        pc.Registry[FakeMachine.Key(GameConfig, "GameDVR_Enabled")] = ("dword", "1");
        pc.GlobalUser[FrameRateLimiter] = new NvSetting(FrameRateLimiter, "Frame Rate Limiter", "dword", "60");
        return pc;
    }

    private static string Cs2Exe(FakeSteam steam) => SteamInstall.Locate(steam.Root)!.Cs2Exe()!;

    private static string Capture(FakeMachine pc, FakeSteam steam, TempDir temp)
    {
        string dir = temp.Sub("capture");
        CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test", Machine = pc };
        options.Include.Clear();
        options.Include.AddRange(MachineSections);
        RunReport report = ConfigBackup.Capture(options, dir, TextWriter.Null);
        foreach (SectionResult result in report.Results)
        {
            if (result.Outcome != Outcomes.Done)
            {
                throw new InvalidOperationException("capture: " + result);
            }
        }

        return dir;
    }

    private static RestoreOptions Restoring(FakeMachine pc, FakeSteam steam, string undoRoot, params string[] only) => new()
    {
        Only = only.Length == 0 ? MachineSections : only,
        SteamRoot = steam.Root,
        SkipProcessChecks = true,
        UndoRoot = undoRoot,
        Machine = pc
    };

    private static string Snapshot(FakeMachine pc) =>
        string.Join('\n', pc.Registry.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)
                          .Concat(pc.Hotkeys.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
                          .Append("plan=" + pc.ActivePlan)
                          .Append("plans=" + string.Join(',', pc.Plans.Keys.Order(StringComparer.OrdinalIgnoreCase)))
                          .Concat(pc.Cs2User.OrderBy(p => p.Key).Select(p => "cs2 " + p.Key + "=" + p.Value.Value))
                          .Concat(pc.GlobalUser.OrderBy(p => p.Key).Select(p => "global " + p.Key + "=" + p.Value.Value))
                          .Append("vibrance=" + string.Join(',', pc.Vibrance.Select(v => v?[0]))));

    /// <summary>Every Windows section: restored onto the target as the player had it, then undone to exactly what the target had.</summary>
    [Test]
    public async Task EveryWindowsSection_RestoresThenUndoes_ToExactlyWhatTheTargetHad()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string captured = Capture(PlayersPc(source), source, temp);
        FakeMachine pc = TargetPc();
        string before = Snapshot(pc);

        RunReport restore = Engine.Restore(captured, Restoring(pc, target, temp.Sub("undo")));

        foreach (SectionResult result in restore.Results)
        {
            await Assert.That(result.Outcome).IsEqualTo(Outcomes.Done).Because(result.ToString());
        }

        await Assert.That(pc.Hotkeys["stickykeys"]).IsEqualTo("0");
        await Assert.That(pc.Registry[FakeMachine.Key(GameBar, "AutoGameModeEnabled")]).IsEqualTo(("dword", "0"));
        await Assert.That(pc.Registry[FakeMachine.Key(GameConfig, "GameDVR_Enabled")]).IsEqualTo(("dword", "0"));
        await Assert.That(pc.Registry[FakeMachine.Key(GpuPreferences, Cs2Exe(target))].Data).Contains(WindowsSettings.HighPerformanceGpu)
            .Because("the GPU preference is keyed by this PC's own cs2.exe");
        await Assert.That(pc.ActivePlan).IsEqualTo(FakeMachine.HighPerformance);
        await Assert.That(pc.Cs2User[FrameRateLimiter].Value).IsEqualTo("240");
        await Assert.That(pc.GlobalUser[FrameRateLimiter].Value).IsEqualTo("60").Because("only the CS2 profile is written");
        await Assert.That(pc.Vibrance[0]![0]).IsEqualTo(80);

        UndoRecord record = new UndoStore(temp.Sub("undo"), isMachine: false).Records().Single();
        RunReport undo = Engine.Undo(record.Dir, new RestoreOptions { SteamRoot = target.Root, SkipProcessChecks = true, Machine = pc, WriteUndo = false });

        foreach (SectionResult result in undo.Results)
        {
            await Assert.That(Outcomes.IsSettled(result.Outcome)).IsTrue().Because(result.ToString());
        }

        await Assert.That(Snapshot(pc)).IsEqualTo(before);
    }

    /// <summary>
    ///     The NVIDIA part writes the CS2 profile only, never the global one, whatever the global profile holds: a
    ///     restore, and its undo. This is what the profile rule means on a driver.
    /// </summary>
    [Test]
    public async Task Nvidia_WritesOnlyTheCs2Profile_OnRestoreAndUndo()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string captured = Capture(PlayersPc(source), source, temp);
        FakeMachine pc = TargetPc();

        Engine.Restore(captured, Restoring(pc, target, temp.Sub("undo"), Sections.Nvidia));
        UndoRecord record = new UndoStore(temp.Sub("undo"), isMachine: false).Records().Single();
        Engine.Undo(record.Dir, new RestoreOptions { SteamRoot = target.Root, SkipProcessChecks = true, Machine = pc, WriteUndo = false });

        List<string> driverWrites = [.. pc.Writes.Where(w => w.StartsWith("nvidia set", StringComparison.Ordinal) || w.StartsWith("nvidia reset", StringComparison.Ordinal))];
        await Assert.That(driverWrites).IsNotEmpty();
        await Assert.That(driverWrites.TrueForAll(w => w.Contains("Counter-Strike 2", StringComparison.Ordinal))).IsTrue().Because(string.Join("; ", driverWrites));
        await Assert.That(pc.GlobalUser[FrameRateLimiter].Value).IsEqualTo("60");
        await Assert.That(pc.Cs2User).IsEmpty().Because("undo puts the CS2 profile back to inheriting");
        await Assert.That(NvidiaDrs.IsCarriedSetting(0xDEADBEEF)).IsFalse();
    }

    /// <summary>A registry value that cannot be written fails the part partway, and what the part had already changed is put back.</summary>
    [Test]
    public async Task ARegistryWriteThatFailsPartway_IsRolledBack()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string captured = Capture(PlayersPc(source), source, temp);
        FakeMachine pc = TargetPc();
        string before = Snapshot(pc);
        pc.FailingRegistry.Add(FakeMachine.Key(GameConfig, "GameDVR_Enabled"));

        RunReport report = Engine.Restore(captured, Restoring(pc, target, temp.Sub("undo"), Sections.GameBar));

        SectionResult result = report.Results.Single();
        await Assert.That(result.Outcome).IsEqualTo(Outcomes.Failed).Because(result.ToString());
        await Assert.That(report.RolledBack).Contains(Sections.GameBar);
        await Assert.That(pc.Writes).Contains("registry " + FakeMachine.Key(GameBar, "AutoGameModeEnabled") + " = dword 0")
            .Because("the part had changed a value before the denied one");
        await Assert.That(Snapshot(pc)).IsEqualTo(before);
    }

    /// <summary>A registry key this user may not write stops the part before its first write, in the plan and the restore alike.</summary>
    [Test]
    public async Task ARegistryKeyThisUserMayNotWrite_BlocksThePartBeforeAnyWrite()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string captured = Capture(PlayersPc(source), source, temp);
        FakeMachine pc = TargetPc();
        pc.DeniedRegistry.Add(FakeMachine.Key(GameConfig, "GameDVR_Enabled"));
        RestoreOptions plan = Restoring(pc, target, temp.Sub("undo"), Sections.GameBar);
        plan.DryRun = true;
        plan.WriteUndo = false;
        plan.UndoRoot = null;

        SectionResult planned = Engine.Restore(captured, plan).Results.Single();
        SectionResult restored = Engine.Restore(captured, Restoring(pc, target, temp.Sub("undo"), Sections.GameBar)).Results.Single();

        await Assert.That(planned.Outcome).IsEqualTo(Outcomes.Blocked).Because(planned.ToString());
        await Assert.That(restored.Outcome).IsEqualTo(Outcomes.Blocked).Because(restored.ToString());
        await Assert.That(restored.Detail).Contains("GameDVR_Enabled cannot be written");
        await Assert.That(pc.Writes).IsEmpty();
        await Assert.That(new UndoStore(temp.Sub("undo"), isMachine: false).Records()).IsEmpty();
    }

    /// <summary>A driver that refuses to save (no administrator) changes nothing, and the part says why.</summary>
    [Test]
    public async Task ADriverThatRefusesToSave_ChangesNothing()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string captured = Capture(PlayersPc(source), source, temp);
        FakeMachine pc = TargetPc();
        pc.SaveNeedsAdministrator = true;
        string before = Snapshot(pc);

        RunReport report = Engine.Restore(captured, Restoring(pc, target, temp.Sub("undo"), Sections.Nvidia));

        await Assert.That(report.Results.Single().Outcome).IsNotEqualTo(Outcomes.Done).Because(report.Results.Single().ToString());
        await Assert.That(Snapshot(pc)).IsEqualTo(before);
        await Assert.That(pc.Writes).DoesNotContain("nvidia save");
    }

    /// <summary>A plan reads the target's settings but changes nothing.</summary>
    [Test]
    public async Task APlan_ChangesNothing()
    {
        if (SkipOffWindows())
        {
            return;
        }

        using TempDir temp = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string captured = Capture(PlayersPc(source), source, temp);
        FakeMachine pc = TargetPc();
        RestoreOptions options = Restoring(pc, target, temp.Sub("undo"));
        options.DryRun = true;
        options.WriteUndo = false;
        options.UndoRoot = null;

        RunReport report = Engine.Restore(captured, options);

        await Assert.That(report.Results.Count).IsEqualTo(MachineSections.Length);
        await Assert.That(pc.Writes).IsEmpty();
        // What the review shows: each setting the restore would change, as this PC has it and as it would get it.
        await Assert.That(report.Settings).Contains(new SettingChange(Sections.Input, "stickykeys hotkey", "on", "off"));
        await Assert.That(report.Settings).Contains(new SettingChange(Sections.GameBar, GameConfig + "\\GameDVR_Enabled", "1", "0"));
        await Assert.That(report.Settings).Contains(new SettingChange(Sections.GameBar, GameBar + "\\AutoGameModeEnabled", null, "0"));
        await Assert.That(report.Settings).Contains(new SettingChange(Sections.Power, "active power plan", "Balanced", "High performance"));
        await Assert.That(report.Settings).Contains(new SettingChange(Sections.Nvidia, "Frame Rate Limiter", null, "240"));
        await Assert.That(report.Settings).Contains(new SettingChange(Sections.Vibrance, "display 1 vibrance", "50", "80"));
        await Assert.That(report.Settings.Exists(c => c.Section == Sections.Cs2Exe && c.Name.EndsWith("cs2.exe", StringComparison.Ordinal))).IsTrue();
    }
}
