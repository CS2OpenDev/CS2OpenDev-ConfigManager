using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     A PC's registry, accessibility hotkeys, power plans and NVIDIA driver in memory, for restoring, rolling back
///     and undoing the machine-wide sections in tests. It behaves as the real ones do where the engine relies on it:
///     the registry is case-insensitive, a DRS session's changes reach the driver only when saved, the active or a
///     built-in power plan cannot be deleted. <see cref="Writes" /> logs every change, in order.
/// </summary>
internal sealed class FakeMachine : IMachine
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    public static readonly IntPtr Cs2Profile = new(1);
    public static readonly IntPtr GlobalProfileHandle = new(2);

    private int _plansMade;

    public FakeMachine()
    {
        foreach (string feature in WindowsSettings.HotkeyFeatures)
        {
            Hotkeys[feature] = "1";
        }
    }

    /// <summary>HKCU values by "subkey|name", case-insensitive as Windows' registry is.</summary>
    public Dictionary<string, (string Kind, string Data)> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registry values this user may not write, as an ACL denial says before anything is written, by "subkey|name".</summary>
    public HashSet<string> DeniedRegistry { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registry values that pass the check but fail when written (the key changed in between), by "subkey|name".</summary>
    public HashSet<string> FailingRegistry { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Hotkeys { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Plans { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Balanced] = "Balanced",
        [HighPerformance] = "High performance",
        [PowerSaver] = "Power saver"
    };

    public string ActivePlan { get; set; } = Balanced;

    /// <summary>Why the driver does not start (no NVIDIA GPU, say), or null when it does.</summary>
    public string? NvidiaProblem { get; set; }

    public bool NvidiaMayPass { get; set; }

    public string? DriverVersion { get; set; } = "566.36";

    public bool HasCs2Profile { get; set; } = true;

    /// <summary>Settings a user set on the CS2 profile, as the driver holds them (saved).</summary>
    public Dictionary<uint, NvSetting> Cs2User { get; } = [];

    /// <summary>Settings a user set on the global profile.</summary>
    public Dictionary<uint, NvSetting> GlobalUser { get; } = [];

    /// <summary>The driver's own values for the CS2 profile.</summary>
    public Dictionary<uint, NvSetting> Cs2Predefined { get; } = [];

    /// <summary>The driver refuses to save, as it does for a standard user.</summary>
    public bool SaveNeedsAdministrator { get; set; }

    /// <summary>Each NVIDIA display's vibrance as current, min and max; null for a display the driver skips.</summary>
    public List<int[]?> Vibrance { get; } = [[50, 0, 100]];

    public List<string> Writes { get; } = [];

    /// <summary>The home folder the Linux NVIDIA profiles are read from and written to: a temp folder of the test's, or none.</summary>
    public string? Home { get; set; }

    public string? HomeFolder() => Home;

    public static string Key(string subKey, string valueName) => subKey + "|" + valueName;

    public bool ReadRegistry(string subKey, string valueName, out string? kind, out string? data)
    {
        if (Registry.TryGetValue(Key(subKey, valueName), out (string Kind, string Data) value))
        {
            (kind, data) = value;
            return true;
        }

        kind = null;
        data = null;
        return false;
    }

    public void WriteRegistry(string subKey, string valueName, string? kind, string? data)
    {
        string key = Key(subKey, valueName);
        if (DeniedRegistry.Contains(key) || FailingRegistry.Contains(key))
        {
            throw new UnauthorizedAccessException("Access to the registry key '" + subKey + "' is denied.");
        }

        Writes.Add("registry " + key + " = " + (kind is null ? "(deleted)" : kind + " " + data));
        if (kind is null)
        {
            Registry.Remove(key);
        }
        else
        {
            Registry[key] = (kind, data!);
        }
    }

    public string? RegistryWriteProblem(string subKey, string valueName) =>
        DeniedRegistry.Contains(Key(subKey, valueName)) ? "Access to the registry key '" + subKey + "' is denied." : null;

    public IDictionary<string, string> ReadHotkeys() => new Dictionary<string, string>(Hotkeys, StringComparer.Ordinal);

    public void SetHotkey(string feature, bool active)
    {
        Writes.Add("hotkey " + feature + " = " + (active ? "on" : "off"));
        Hotkeys[feature] = active ? "1" : "0";
    }

    public string[]? ActivePowerPlan() => Plans.TryGetValue(ActivePlan, out string? name) ? [ActivePlan, name] : null;

    public IDictionary<string, string> PowerPlans() => new Dictionary<string, string>(Plans, StringComparer.OrdinalIgnoreCase);

    public string? DuplicatePowerPlan(string templateGuid, out string? problem)
    {
        if (!Plans.TryGetValue(templateGuid, out string? name))
        {
            problem = "The power scheme, subgroup or setting specified does not exist.";
            return null;
        }

        string guid = $"00000000-0000-4000-8000-{++_plansMade:x12}";
        Writes.Add("power duplicate " + templateGuid + " as " + guid);
        Plans[guid] = name;
        problem = null;
        return guid;
    }

    public string? ActivatePowerPlan(string guid)
    {
        if (!Plans.ContainsKey(guid))
        {
            return "The power scheme, subgroup or setting specified does not exist.";
        }

        Writes.Add("power activate " + guid);
        ActivePlan = guid;
        return null;
    }

    public string? DeletePowerPlan(string guid)
    {
        if (WindowsSettings.IsBuiltInPowerPlan(guid))
        {
            return "not a plan this tool can remove: " + guid;
        }

        if (string.Equals(guid, ActivePlan, StringComparison.OrdinalIgnoreCase))
        {
            return "Unable to delete the active power scheme.";
        }

        Writes.Add("power delete " + guid);
        return Plans.Remove(guid) ? null : "The power scheme, subgroup or setting specified does not exist.";
    }

    public string? StartNvidia(out bool mayPass)
    {
        mayPass = NvidiaMayPass;
        return NvidiaProblem;
    }

    public string? NvidiaDriverVersion() => NvidiaProblem is null ? DriverVersion : null;

    public INvidiaSession OpenNvidia() =>
        NvidiaProblem is null ? new Session(this) : throw new InvalidOperationException("the driver has not started");

    public IList<int[]?> ReadVibrance() => NvidiaProblem is null ? [.. Vibrance.Select(v => v?.ToArray())] : [];

    public void SetVibrance(int index, int level)
    {
        int[] display = Vibrance[index] ?? throw new InvalidOperationException("this PC has no NVIDIA display number " + (index + 1));
        Writes.Add("vibrance " + index + " = " + level);
        display[0] = level;
    }

    /// <summary>A DRS session: changes go to copies, and reach the fake driver only on <see cref="Save" />.</summary>
    private sealed class Session(FakeMachine machine) : INvidiaSession
    {
        private readonly Dictionary<uint, NvSetting> _cs2 = new(machine.Cs2User);
        private readonly Dictionary<uint, NvSetting> _global = new(machine.GlobalUser);

        public IntPtr FindCs2Profile() => machine.HasCs2Profile ? Cs2Profile : IntPtr.Zero;

        public IntPtr GlobalProfile() => GlobalProfileHandle;

        public string ProfileName(IntPtr profile) => profile == Cs2Profile ? "Counter-Strike 2" : profile == GlobalProfileHandle ? "Base Profile" : throw Unknown(profile);

        public NvSetting EffectiveSetting(IntPtr profile, uint id, out bool userSet)
        {
            NvSetting? user = Profile(profile).GetValueOrDefault(id);
            NvSetting? predefined = profile == Cs2Profile ? machine.Cs2Predefined.GetValueOrDefault(id) : null;
            NvSetting? global = profile == Cs2Profile ? _global.GetValueOrDefault(id) : null;
            userSet = user is not null || (predefined is null && global is not null);
            return user ?? predefined ?? global!;
        }

        public NvSetting UserSetting(IntPtr profile, uint id) => Profile(profile).GetValueOrDefault(id)!;

        public bool InheritsUserGlobalValue(IntPtr profile, uint id) =>
            profile == Cs2Profile && !_cs2.ContainsKey(id) && !machine.Cs2Predefined.ContainsKey(id) && _global.ContainsKey(id);

        public NvSetting PredefinedSetting(IntPtr profile, uint id) =>
            (profile == Cs2Profile ? machine.Cs2Predefined.GetValueOrDefault(id) : null)!;

        public void SetSetting(IntPtr profile, NvSetting setting)
        {
            machine.Writes.Add("nvidia set " + ProfileName(profile) + " " + setting.IdText + " = " + setting.Value);
            Profile(profile)[setting.Id] = setting;
        }

        public void ResetSetting(IntPtr profile, uint id)
        {
            machine.Writes.Add("nvidia reset " + ProfileName(profile) + " 0x" + id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
            Profile(profile).Remove(id);
        }

        public void Save()
        {
            if (machine.SaveNeedsAdministrator)
            {
                throw new NvAdminRequiredException();
            }

            machine.Writes.Add("nvidia save");
            Copy(_cs2, machine.Cs2User);
            Copy(_global, machine.GlobalUser);
        }

        public void Dispose()
        {
        }

        private Dictionary<uint, NvSetting> Profile(IntPtr profile) =>
            profile == Cs2Profile ? _cs2 : profile == GlobalProfileHandle ? _global : throw Unknown(profile);

        private static void Copy(Dictionary<uint, NvSetting> from, Dictionary<uint, NvSetting> to)
        {
            to.Clear();
            foreach (KeyValuePair<uint, NvSetting> pair in from)
            {
                to[pair.Key] = pair.Value;
            }
        }

        private static ArgumentException Unknown(IntPtr profile) => new("Not a profile of this session: " + profile, nameof(profile));
    }
}
