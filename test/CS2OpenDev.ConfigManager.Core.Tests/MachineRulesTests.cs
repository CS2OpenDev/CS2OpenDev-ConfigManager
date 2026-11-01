using CS2OpenDev.ConfigManager.Kit;
using KitBackup = CS2OpenDev.ConfigManager.Kit.ConfigBackup;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The decisions behind the machine-wide parts, as the pure functions the kit makes them with: what a backup
///     records of cs2.exe's values and how a restore merges it, which bit of the accessibility flags moves, how a
///     power plan is recorded and reached, and which NVIDIA settings are carried. None of these touch the machine.
/// </summary>
public class MachineRulesTests
{
    private const string Layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string GpuPreferences = @"Software\Microsoft\DirectX\UserGpuPreferences";

    [Test]
    [Arguments("~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", "DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [Arguments("~ RUNASADMIN", null)]
    [Arguments("~ WIN7RTM HIGHDPIAWARE", null)]
    public async Task ABackup_RecordsOnlyTheFullscreenOptimizationsLayer(string data, string? recorded)
    {
        await Assert.That(WindowsSettings.ValueForBackup(Layers, data)).IsEqualTo(recorded);
    }

    [Test]
    [Arguments("GpuPreference=2;", "GpuPreference=2;")]
    [Arguments("SwapEffectUpgradeEnable=1;GpuPreference=2;", "GpuPreference=2;")]
    [Arguments("GpuPreference=1;", null)]
    [Arguments("SwapEffectUpgradeEnable=1;", null)]
    public async Task ABackup_RecordsOnlyTheHighPerformanceGpu(string data, string? recorded)
    {
        await Assert.That(WindowsSettings.ValueForBackup(GpuPreferences, data)).IsEqualTo(recorded);
    }

    /// <summary>
    ///     A source PC without the fullscreen-optimizations layer records it absent whether cs2.exe has no Layers
    ///     value or one holding other layers, so either way the restore takes the target's off.
    /// </summary>
    [Test]
    [Arguments(Layers, false, null, true, null)]
    [Arguments(Layers, true, "~ RUNASADMIN", true, null)]
    [Arguments(Layers, true, "~ RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", true, "DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [Arguments(GpuPreferences, false, null, false, null)]
    [Arguments(GpuPreferences, true, "GpuPreference=1;", false, null)]
    [Arguments(GpuPreferences, true, "GpuPreference=2;", true, "GpuPreference=2;")]
    [Arguments(@"Software\Microsoft\GameBar", false, null, true, null)]
    [Arguments(@"Software\Microsoft\GameBar", true, "0", true, "0")]
    public async Task ABackup_RecordsWhatTheRestoreNeeds_ForEachSourceState(string subKey, bool present, string? data, bool recorded, string? recordedData)
    {
        string? value = data;

        bool record = KitBackup.RegistryEntryForBackup(subKey, present, ref value);

        await Assert.That(record).IsEqualTo(recorded);
        if (record)
        {
            await Assert.That(value).IsEqualTo(recordedData);
        }
    }

    [Test]
    [Arguments("~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ HIGHDPIAWARE")]
    [Arguments("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", null)]
    public async Task ASourceWithNoLayersValue_TakesTheLayerOffTheTarget(string target, string? written)
    {
        string? value = null;
        await Assert.That(KitBackup.RegistryEntryForBackup(Layers, false, ref value)).IsTrue();
        await Assert.That(WindowsSettings.MergeValue(Layers, target, value)).IsEqualTo(written);
    }

    [Test]
    [Arguments(null, "DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [Arguments("~ HIGHDPIAWARE", "DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    [Arguments("~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE", null, "~ HIGHDPIAWARE")]
    [Arguments("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", null, null)]
    [Arguments("~ HIGHDPIAWARE", "RUNASADMIN DISABLEDXMAXIMIZEDWINDOWEDMODE", "~ HIGHDPIAWARE DISABLEDXMAXIMIZEDWINDOWEDMODE")]
    public async Task ARestore_MergesTheLayerIntoTheTargets_AndNeverAddsAnother(string? target, string? backup, string? written)
    {
        await Assert.That(WindowsSettings.MergeValue(Layers, target, backup)).IsEqualTo(written);
    }

    [Test]
    [Arguments(null, "GpuPreference=2;", "GpuPreference=2;")]
    [Arguments("SwapEffectUpgradeEnable=1;GpuPreference=1;", "GpuPreference=2;", "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [Arguments("SwapEffectUpgradeEnable=1;", "GpuPreference=2;", "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    [Arguments("GpuPreference=1;", "GpuPreference=1;", "GpuPreference=1;")]
    public async Task ARestore_SetsOnlyHighPerformance_KeepingTheTargetsOtherPairs(string? target, string? backup, string? written)
    {
        await Assert.That(WindowsSettings.MergeValue(GpuPreferences, target, backup)).IsEqualTo(written);
    }

    [Test]
    public async Task TheHotkeyBit_IsTheOnlyAccessibilityFlagThatMoves()
    {
        // SKF_STICKYKEYSON (1) and FKF_FILTERKEYSON (1) stay as the target has them.
        await Assert.That(WindowsSettings.WithHotkey(0x7F, false)).IsEqualTo(0x7B);
        await Assert.That(WindowsSettings.WithHotkey(0x01, false)).IsEqualTo(0x01);
        await Assert.That(WindowsSettings.WithHotkey(0x01, true)).IsEqualTo(0x05);
    }

    private const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Copy = "11111111-2222-3333-4444-555555555555";

    [Test]
    [Arguments(Ultimate, "Ultimate Performance", null, Balanced, "copy", Ultimate)]
    [Arguments(Ultimate, "Ultimate Performance", null, Ultimate, "already", Ultimate)]
    [Arguments(Copy, "My plan", null, Balanced, "missing", null)]
    [Arguments(Copy, "Balanced", null, Ultimate, "activate", Balanced)]
    [Arguments(Copy, "Ultimate Performance", Ultimate, Balanced, "copy", Ultimate)]
    [Arguments(Copy, "Ultimate Performance", Ultimate, Ultimate, "already", Ultimate)]
    [Arguments(Copy, "Hoechstleistung", HighPerformance, Balanced, "activate", HighPerformance)]
    public async Task APowerPlan_IsReachedByGuid_ThenName_ThenItsWindowsTemplate(string plan, string name, string? template, string active, string action, string? use)
    {
        // The target lists Balanced, High performance under another name, and Ultimate only when it is active.
        Dictionary<string, string> plans = new(StringComparer.OrdinalIgnoreCase) { [Balanced] = "Balanced", [HighPerformance] = "Hoechstleistung (OEM)" };
        if (active == Ultimate)
        {
            plans[Ultimate] = "Ultimate Performance";
        }

        string decided = WindowsSettings.PowerPlanAction(plan, name, template, plans, active, out string? useGuid);

        await Assert.That(decided).IsEqualTo(action);
        await Assert.That(useGuid).IsEqualTo(use);
    }

    [Test]
    [Arguments(Ultimate, "Ultimate Performance", Ultimate)]
    [Arguments(Copy, "Ultimate Performance", Ultimate)]
    [Arguments(Copy, "ultimate performance", Ultimate)]
    [Arguments(Copy, "Hoechstleistung", HighPerformance)]
    [Arguments(Copy, "My plan", null)]
    public async Task ACaptureRecordsTheTemplate_OfAPlanNamedLikeOne(string plan, string name, string? template)
    {
        // The capture PC lists High performance under its own (localized) name.
        Dictionary<string, string> plans = new(StringComparer.OrdinalIgnoreCase) { [Balanced] = "Ausbalanciert", [HighPerformance] = "Hoechstleistung" };

        await Assert.That(WindowsSettings.PowerPlanTemplate(plan, name, plans)).IsEqualTo(template);
    }

    [Test]
    public async Task ThePreview_CountsOnlyTheNvidiaSettingsTheBackupSets()
    {
        KvNode settings = new("settings");
        settings.Section("0x00A879CF").Set("value", "1");
        settings.Section("0x10835002").Set("absent", "1");
        settings.Section("0x1074C972").Set("absent", "1");

        await Assert.That(KitBackup.DescribeNvidia(settings)).IsEqualTo("1 settings, 2 at the driver's default");
        await Assert.That(KitBackup.DescribeNvidia(null)).IsEqualTo("0 settings");
    }

    [Test]
    public async Task OnlyTheListedNvidiaSettings_AreCarried()
    {
        await Assert.That(NvidiaDrs.IsCarriedSetting(0x00A879CF)).IsTrue().Because("Vertical Sync");
        await Assert.That(NvidiaDrs.IsCarriedSetting(0x10835002)).IsTrue().Because("Frame Rate Limiter");
        await Assert.That(NvidiaDrs.IsCarriedSetting(0x1094F1F7)).IsFalse().Because("G-SYNC state belongs to the display, not the player");
        await Assert.That(NvidiaDrs.IsCarriedSetting(0x12345678)).IsFalse();
    }
}
