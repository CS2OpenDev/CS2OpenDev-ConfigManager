
namespace CS2OpenDev.ConfigManager.Kit;

/// <summary>
///     What the engine reads and changes on the PC beyond files: the current user's registry, the accessibility
///     hotkeys, the power plans, and NVIDIA's driver. <see cref="RealMachine" /> is this PC; tests restore, roll back
///     and undo against a fake, and while a test assembly runs, the engine's default refuses the real one.
/// </summary>
internal interface IMachine
{
    /// <summary>One value under HKCU, as <see cref="WindowsSettings.ReadRegistry" /> reads it. False when absent.</summary>
    bool ReadRegistry(string subKey, string valueName, out string? kind, out string? data);

    /// <summary>Writes one value under HKCU, or deletes it when <paramref name="kind" /> is null.</summary>
    void WriteRegistry(string subKey, string valueName, string? kind, string? data);

    /// <summary>Why a value under this key cannot be written by this user, or null; checks without changing anything.</summary>
    string? RegistryWriteProblem(string subKey, string valueName);

    /// <summary>Whether each accessibility feature has its hotkey on, as "1" or "0".</summary>
    IDictionary<string, string> ReadHotkeys();

    void SetHotkey(string feature, bool active);

    /// <summary>The active power plan's GUID and name, or null.</summary>
    string[]? ActivePowerPlan();

    /// <summary>Every listed power plan, GUID to name.</summary>
    IDictionary<string, string> PowerPlans();

    /// <summary>A new plan copied from a template: its GUID, or null with the reason.</summary>
    string? DuplicatePowerPlan(string templateGuid, out string? problem);

    /// <summary>Null once the plan is active, otherwise why not.</summary>
    string? ActivatePowerPlan(string guid);

    /// <summary>Null once the plan is removed, otherwise why not.</summary>
    string? DeletePowerPlan(string guid);

    /// <summary>Starts NVIDIA's driver API: null once it is up, otherwise why not, and whether that may pass.</summary>
    string? StartNvidia(out bool mayPass);

    /// <summary>The driver's version, or null.</summary>
    string? NvidiaDriverVersion();

    /// <summary>A session on the driver's settings database; the driver must have started.</summary>
    INvidiaSession OpenNvidia();

    /// <summary>Each NVIDIA display's vibrance as current, min and max, in the driver's order; null for a display it skips.</summary>
    IList<int[]?> ReadVibrance();

    void SetVibrance(int index, int level);

    /// <summary>The user's home folder, where the Linux driver's application profiles live; null when unknown.</summary>
    string? HomeFolder();
}

/// <summary>A session on NVIDIA's settings database (DRS): profiles by handle, settings by id, saved together.</summary>
internal interface INvidiaSession : IDisposable
{
    IntPtr FindCs2Profile();

    IntPtr GlobalProfile();

    string ProfileName(IntPtr profile);

    NvSetting EffectiveSetting(IntPtr profile, uint id, out bool userSet);

    NvSetting UserSetting(IntPtr profile, uint id);

    bool InheritsUserGlobalValue(IntPtr profile, uint id);

    NvSetting PredefinedSetting(IntPtr profile, uint id);

    void SetSetting(IntPtr profile, NvSetting setting);

    void ResetSetting(IntPtr profile, uint id);

    void Save();
}

/// <summary>This PC, through Windows' registry, SystemParametersInfo, powercfg and NVAPI.</summary>
internal sealed class RealMachine : IMachine
{
    public static readonly RealMachine Instance = new();

    private RealMachine()
    {
    }

    public bool ReadRegistry(string subKey, string valueName, out string? kind, out string? data)
    {
        kind = null;
        data = null;
        return OperatingSystem.IsWindows() && WindowsSettings.ReadRegistry(subKey, valueName, out kind, out data);
    }

    public void WriteRegistry(string subKey, string valueName, string? kind, string? data)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The registry is Windows only.");
        }

        WindowsSettings.WriteRegistry(subKey, valueName, kind, data);
    }

    public string? RegistryWriteProblem(string subKey, string valueName) =>
        OperatingSystem.IsWindows() ? WindowsSettings.RegistryWriteProblem(subKey) : "the registry is Windows only";

    public IDictionary<string, string> ReadHotkeys() => WindowsSettings.ReadHotkeys();

    public void SetHotkey(string feature, bool active) => WindowsSettings.SetHotkey(feature, active);

    public string[]? ActivePowerPlan() => WindowsSettings.ActivePowerPlan();

    public IDictionary<string, string> PowerPlans() => WindowsSettings.PowerPlans();

    public string? DuplicatePowerPlan(string templateGuid, out string? problem) => WindowsSettings.DuplicatePowerPlan(templateGuid, out problem);

    public string? ActivatePowerPlan(string guid) => WindowsSettings.ActivatePowerPlan(guid);

    public string? DeletePowerPlan(string guid) => WindowsSettings.DeletePowerPlan(guid);

    public string? StartNvidia(out bool mayPass) => NvidiaDrs.Initialize(out mayPass);

    public string? NvidiaDriverVersion() => NvidiaDrs.DriverVersion();

    public INvidiaSession OpenNvidia() => NvidiaDrs.Open();

    public IList<int[]?> ReadVibrance() => NvidiaDrs.ReadVibrance()!;

    public void SetVibrance(int index, int level) => NvidiaDrs.SetVibrance(index, level);

    public string? HomeFolder() => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : null;
}

/// <summary>
///     The engine's default while a test assembly runs: every call throws before it reaches the PC, reading
///     included, so no test can read or change this PC's registry, power plan or driver. Tests pass a fake.
/// </summary>
internal sealed class RefusingMachine : IMachine
{
    public const string Message = "a test run never reads or writes this PC's Windows or driver settings; pass a fake machine";

    public static readonly RefusingMachine Instance = new();

    private RefusingMachine()
    {
    }

    public bool ReadRegistry(string subKey, string valueName, out string? kind, out string? data) => throw Refused();

    public void WriteRegistry(string subKey, string valueName, string? kind, string? data) => throw Refused();

    public string? RegistryWriteProblem(string subKey, string valueName) => throw Refused();

    public IDictionary<string, string> ReadHotkeys() => throw Refused();

    public void SetHotkey(string feature, bool active) => throw Refused();

    public string[]? ActivePowerPlan() => throw Refused();

    public IDictionary<string, string> PowerPlans() => throw Refused();

    public string? DuplicatePowerPlan(string templateGuid, out string? problem) => throw Refused();

    public string? ActivatePowerPlan(string guid) => throw Refused();

    public string? DeletePowerPlan(string guid) => throw Refused();

    public string? StartNvidia(out bool mayPass) => throw Refused();

    public string? NvidiaDriverVersion() => throw Refused();

    public INvidiaSession OpenNvidia() => throw Refused();

    public IList<int[]?> ReadVibrance() => throw Refused();

    public void SetVibrance(int index, int level) => throw Refused();

    public string? HomeFolder() => throw Refused();

    private static InvalidOperationException Refused() => new(Message);
}

/// <summary>
///     One setting a restore or undo changes: <paramref name="Current" /> is what the PC has, <paramref name="Wanted" />
///     what it gets; null for "not set", where the driver or Windows decides.
/// </summary>
public sealed record SettingChange(string Section, string Name, string? Current, string? Wanted);
