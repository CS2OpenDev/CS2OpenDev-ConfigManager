#nullable disable
#pragma warning disable CA1720, CA1861
// v1's C# 5 style; see the header of KeyValues.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CS2OpenDev.ConfigManager.Kit
{
    /// <summary>
    ///     A per-user registry value the backup carries. <see cref="ValueName" /> may be the token
    ///     <see cref="Cs2ExeToken" />, which stands for the path of cs2.exe on whichever machine reads it.
    /// </summary>
    public sealed class RegistryItem
    {
        public const string Cs2ExeToken = "{cs2.exe}";

        public RegistryItem(string group, string subKey, string valueName, string kind)
        {
            Group = group;
            SubKey = subKey;
            ValueName = valueName;
            Kind = kind;
        }

        public string Group { get; private set; }

        /// <summary>Under HKEY_CURRENT_USER.</summary>
        public string SubKey { get; private set; }

        public string ValueName { get; private set; }

        /// <summary>The kind Windows reads the value as (dword or string); a backup carries no other.</summary>
        public string Kind { get; private set; }
    }

    /// <summary>
    ///     Windows settings that matter to CS2: the accessibility hotkeys that steal Shift and NumLock, Game Mode
    ///     and background capture, cs2.exe's GPU preference and fullscreen optimizations, and the power plan.
    /// </summary>
    public static class WindowsSettings
    {
        private const uint SpiGetFilterKeys = 0x0032;
        private const uint SpiGetToggleKeys = 0x0034;
        private const uint SpiGetStickyKeys = 0x003A;
        private const uint SpifPersist = 0x01 | 0x02;

        /// <summary>SKF_, TKF_ and FKF_HOTKEYACTIVE share this bit: the shortcut that opens the feature's dialog.</summary>
        public const int HotkeyActive = 0x04;

        /// <summary>The accessibility features whose hotkey the input part carries, by the names backup.vdf uses.</summary>
        public static readonly string[] HotkeyFeatures = { "stickykeys", "togglekeys", "filterkeys" };

        private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
        private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        /// <summary>The one compatibility layer the cs2exe part carries: "Disable fullscreen optimizations".</summary>
        public const string DisableFullscreenOptimizations = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

        /// <summary>The one GPU preference the cs2exe part carries: "High performance".</summary>
        public const string HighPerformanceGpu = "GpuPreference=2;";

        /// <summary>The registry values the backup carries, by group.</summary>
        public static readonly RegistryItem[] RegistryItems =
        {
            new RegistryItem("gamebar", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", "dword"),
            new RegistryItem("gamebar", @"Software\Microsoft\GameBar", "AllowAutoGameMode", "dword"),
            new RegistryItem("gamebar", @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", "dword"),
            new RegistryItem("gamebar", @"System\GameConfigStore", "GameDVR_Enabled", "dword"),
            new RegistryItem("gamebar", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "dword"),
            new RegistryItem("cs2exe", GpuPreferencesKey, RegistryItem.Cs2ExeToken, "string"),
            new RegistryItem("cs2exe", LayersKey, RegistryItem.Cs2ExeToken, "string")
        };

        /// <summary>The carried item for a key and value name, or null when the backup carries no such value.</summary>
        public static RegistryItem CarriedItem(string subKey, string valueName)
        {
            foreach (RegistryItem item in RegistryItems)
            {
                if (string.Equals(item.SubKey, subKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.ValueName, valueName, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        // -- Accessibility hotkeys, through SystemParametersInfo ---------------------------------------

        /// <summary>Whether each feature in <see cref="HotkeyFeatures" /> has its hotkey on, as "1" or "0".</summary>
        public static IDictionary<string, string> ReadHotkeys()
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            foreach (string feature in HotkeyFeatures)
            {
                int[] fields = ReadFlagsStruct(feature);
                if (fields != null)
                {
                    values[feature] = (fields[1] & HotkeyActive) != 0 ? "1" : "0";
                }
            }

            return values;
        }

        /// <summary>
        ///     Turns one feature's hotkey on or off, live and saved to the profile, keeping every other field as
        ///     this PC has it: whether the feature itself is on, and Filter Keys' timings.
        /// </summary>
        public static void SetHotkey(string feature, bool active)
        {
            int[] fields = ReadFlagsStruct(feature);
            if (fields == null)
            {
                throw new InvalidOperationException("Windows did not report the " + feature + " setting.");
            }

            fields[1] = WithHotkey(fields[1], active);
            uint getAction;
            int size;
            FlagsStruct(feature, out getAction, out size);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < fields.Length; i++)
                {
                    Marshal.WriteInt32(buffer, i * 4, fields[i]);
                }

                Check(SystemParametersInfo(getAction + 1, (uint)size, buffer, SpifPersist), feature);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>A feature's dwFlags with only the hotkey bit changed.</summary>
        public static int WithHotkey(int flags, bool active)
        {
            return active ? flags | HotkeyActive : flags & ~HotkeyActive;
        }

        // STICKYKEYS and TOGGLEKEYS are cbSize and dwFlags; FILTERKEYS adds four timings. Each set action is its get + 1.
        private static void FlagsStruct(string feature, out uint getAction, out int size)
        {
            switch (feature)
            {
                case "stickykeys":
                    getAction = SpiGetStickyKeys;
                    size = 8;
                    return;
                case "togglekeys":
                    getAction = SpiGetToggleKeys;
                    size = 8;
                    return;
                case "filterkeys":
                    getAction = SpiGetFilterKeys;
                    size = 24;
                    return;
                default:
                    throw new ArgumentException("Unknown accessibility feature '" + feature + "'.");
            }
        }

        private static int[] ReadFlagsStruct(string feature)
        {
            uint getAction;
            int size;
            FlagsStruct(feature, out getAction, out size);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i += 4)
                {
                    Marshal.WriteInt32(buffer, i, 0);
                }

                Marshal.WriteInt32(buffer, 0, size);
                if (!SystemParametersInfo(getAction, (uint)size, buffer, 0))
                {
                    return null;
                }

                int[] fields = new int[size / 4];
                for (int i = 0; i < fields.Length; i++)
                {
                    fields[i] = Marshal.ReadInt32(buffer, i * 4);
                }

                fields[0] = size;
                return fields;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException("Windows refused the " + what + " setting (error " + Inv.S(Marshal.GetLastWin32Error()) + ").");
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        // -- cs2.exe's two values, each carried as the one choice in it that matters ------------------

        /// <summary>Whether this item's value is merged into the target's rather than written over it.</summary>
        public static bool IsMergedValue(string subKey)
        {
            return string.Equals(subKey, LayersKey, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(subKey, GpuPreferencesKey, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Whether a backup without the merged value's choice takes it off the target: true for fullscreen
        ///     optimizations, false for the GPU preference, which a restore only ever sets to high performance.
        /// </summary>
        public static bool AbsenceRemovesChoice(string subKey)
        {
            return string.Equals(subKey, LayersKey, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     What a backup records for a value read from this PC. For the two cs2exe values that is the one
        ///     choice carried (fullscreen optimizations off, high-performance GPU), or null when the value does
        ///     not hold it; every other layer, such as run as administrator, stays behind.
        /// </summary>
        public static string ValueForBackup(string subKey, string data)
        {
            if (string.Equals(subKey, LayersKey, StringComparison.OrdinalIgnoreCase))
            {
                return HasLayer(data, DisableFullscreenOptimizations) ? DisableFullscreenOptimizations : null;
            }

            if (string.Equals(subKey, GpuPreferencesKey, StringComparison.OrdinalIgnoreCase))
            {
                return GpuPreference(data) == "2" ? HighPerformanceGpu : null;
            }

            return data;
        }

        /// <summary>
        ///     The value a restore writes for a merged item: the target's own value with the backup's choice
        ///     applied and everything else in it kept. Null deletes the value.
        /// </summary>
        public static string MergeValue(string subKey, string targetData, string backupData)
        {
            if (string.Equals(subKey, LayersKey, StringComparison.OrdinalIgnoreCase))
            {
                return WithLayer(targetData, DisableFullscreenOptimizations, HasLayer(backupData, DisableFullscreenOptimizations));
            }

            if (string.Equals(subKey, GpuPreferencesKey, StringComparison.OrdinalIgnoreCase))
            {
                return GpuPreference(backupData) == "2" ? WithGpuPreference(targetData, "2") : targetData;
            }

            return backupData;
        }

        private static bool HasLayer(string layers, string layer)
        {
            foreach (string token in (layers ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(token, layer, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     A Layers string with one layer added or removed and the rest kept in order; null when no layer is
        ///     left, so the value is deleted rather than left as a bare marker.
        /// </summary>
        public static string WithLayer(string layers, string layer, bool present)
        {
            List<string> markers = new List<string>();
            List<string> kept = new List<string>();
            foreach (string token in (layers ?? "").Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "~" || token == "$" || token == "#")
                {
                    markers.Add(token);
                }
                else if (!string.Equals(token, layer, StringComparison.OrdinalIgnoreCase))
                {
                    kept.Add(token);
                }
            }

            if (present)
            {
                kept.Add(layer);
            }

            if (kept.Count == 0)
            {
                return null;
            }

            if (markers.Count == 0)
            {
                markers.Add("~");
            }

            markers.AddRange(kept);
            return string.Join(" ", markers.ToArray());
        }

        private static string GpuPreference(string data)
        {
            foreach (string part in (data ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && string.Equals(part.Substring(0, eq).Trim(), "GpuPreference", StringComparison.OrdinalIgnoreCase))
                {
                    return part.Substring(eq + 1).Trim();
                }
            }

            return null;
        }

        /// <summary>A UserGpuPreferences string with GpuPreference set and every other pair kept.</summary>
        public static string WithGpuPreference(string data, string preference)
        {
            StringBuilder sb = new StringBuilder();
            bool set = false;
            foreach (string part in (data ?? "").Split(';'))
            {
                if (part.Trim().Length == 0)
                {
                    continue;
                }

                int eq = part.IndexOf('=');
                if (eq > 0 && string.Equals(part.Substring(0, eq).Trim(), "GpuPreference", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("GpuPreference=").Append(preference).Append(';');
                    set = true;
                }
                else
                {
                    sb.Append(part).Append(';');
                }
            }

            if (!set)
            {
                sb.Insert(0, "GpuPreference=" + preference + ";");
            }

            return sb.ToString();
        }

        // -- Registry values --------------------------------------------------------------------------

        /// <summary>
        ///     Reads one value under HKCU: <paramref name="kind" /> is dword, qword, string, expandstring or binary and
        ///     <paramref name="data" /> its text form. False when the value is absent.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static bool ReadRegistry(string subKey, string valueName, out string kind, out string data)
        {
            kind = null;
            data = null;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                if (key == null)
                {
                    return false;
                }

                object value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null)
                {
                    return false;
                }

                switch (key.GetValueKind(valueName))
                {
                    case RegistryValueKind.DWord:
                        kind = "dword";
                        data = Inv.S(unchecked((uint)(int)value));
                        return true;
                    case RegistryValueKind.QWord:
                        kind = "qword";
                        data = Inv.S((long)value);
                        return true;
                    case RegistryValueKind.Binary:
                        kind = "binary";
                        data = Inv.Hex((byte[])value);
                        return true;
                    case RegistryValueKind.String:
                        kind = "string";
                        data = (string)value;
                        return true;
                    case RegistryValueKind.ExpandString:
                        kind = "expandstring";
                        data = (string)value;
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>Writes one value under HKCU, or deletes it when <paramref name="kind" /> is null.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static void WriteRegistry(string subKey, string valueName, string kind, string data)
        {
            if (kind == null)
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(subKey, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(valueName, false);
                    }
                }

                return;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(subKey))
            {
                switch (kind)
                {
                    case "dword":
                        key.SetValue(valueName, unchecked((int)uint.Parse(data, System.Globalization.CultureInfo.InvariantCulture)), RegistryValueKind.DWord);
                        break;
                    case "qword":
                        key.SetValue(valueName, long.Parse(data, System.Globalization.CultureInfo.InvariantCulture), RegistryValueKind.QWord);
                        break;
                    case "binary":
                        key.SetValue(valueName, Inv.Unhex(data), RegistryValueKind.Binary);
                        break;
                    case "string":
                        key.SetValue(valueName, data, RegistryValueKind.String);
                        break;
                    case "expandstring":
                        key.SetValue(valueName, data, RegistryValueKind.ExpandString);
                        break;
                    default:
                        throw new FormatException("Unknown registry kind '" + kind + "'.");
                }
            }
        }

        /// <summary>
        ///     Why this user cannot write a value under <paramref name="subKey" /> in HKCU, or null: the key, or the
        ///     nearest one above it that exists (where a missing key would be made), opened for writing and closed
        ///     again. Nothing is created or changed.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static string RegistryWriteProblem(string subKey)
        {
            string path = subKey;
            while (true)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, true))
                    {
                        if (key != null)
                        {
                            return null;
                        }
                    }
                }
                catch (System.Security.SecurityException ex)
                {
                    return ex.Message;
                }
                catch (UnauthorizedAccessException ex)
                {
                    return ex.Message;
                }

                int cut = path.LastIndexOf('\\');
                if (cut < 0)
                {
                    return null;
                }

                path = path.Substring(0, cut);
            }
        }

        /// <summary>Whether <see cref="WriteRegistry" /> can write this kind and data, checked before any value is written.</summary>
        public static bool IsValidRegistryData(string kind, string data)
        {
            uint dword;
            long qword;
            switch (kind)
            {
                case "dword":
                    return uint.TryParse(data, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out dword);
                case "qword":
                    return long.TryParse(data, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out qword);
                case "binary":
                    try
                    {
                        Inv.Unhex(data);
                        return true;
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                case "string":
                case "expandstring":
                    return data != null;
                default:
                    return false;
            }
        }

        // -- Power plan, through powercfg -------------------------------------------------------------

        private static readonly Regex GuidPattern = new Regex(
            "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\\s*\\((.*)\\)");

        /// <summary>The active power plan's GUID and name, or null.</summary>
        public static string[] ActivePowerPlan()
        {
            string output;
            if (PowerCfg("/getactivescheme", out output) != 0)
            {
                return null;
            }

            Match m = GuidPattern.Match(output);
            return m.Success ? new string[] { m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim() } : null;
        }

        /// <summary>Every power plan this machine lists, as GUID to name.</summary>
        public static IDictionary<string, string> PowerPlans()
        {
            Dictionary<string, string> plans = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string output;
            if (PowerCfg("/list", out output) != 0)
            {
                return plans;
            }

            foreach (Match m in GuidPattern.Matches(output))
            {
                plans[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value.Trim();
            }

            return plans;
        }

        /// <summary>
        ///     The templates Windows ships: Balanced, High performance, Power saver, Ultimate Performance. A PC
        ///     may hide all but Balanced from its list; the template stays, so a copy of it can be made.
        /// </summary>
        public static bool IsBuiltInPowerPlan(string guid)
        {
            switch ((guid ?? "").ToLowerInvariant())
            {
                case "381b4222-f694-41f0-9685-ff5bb260df2e":
                case "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c":
                case "a1841308-3541-4fab-bc81-f71556f20b4a":
                case "e9a42b02-d5df-448d-aa00-03f14749eb61":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>The English name Windows gives a template, or null for any other GUID.</summary>
        private static string TemplateName(string guid)
        {
            switch ((guid ?? "").ToLowerInvariant())
            {
                case "381b4222-f694-41f0-9685-ff5bb260df2e": return "Balanced";
                case "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c": return "High performance";
                case "a1841308-3541-4fab-bc81-f71556f20b4a": return "Power saver";
                case "e9a42b02-d5df-448d-aa00-03f14749eb61": return "Ultimate Performance";
                default: return null;
            }
        }

        private static readonly string[] Templates =
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e", "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            "a1841308-3541-4fab-bc81-f71556f20b4a", "e9a42b02-d5df-448d-aa00-03f14749eb61"
        };

        /// <summary>
        ///     The Windows template a plan is, or is a copy of by name, or null for a plan of the user's own. A
        ///     plan named like a template, in English or as this PC lists the template, counts as a copy of it:
        ///     Ultimate Performance is usually enabled with powercfg -duplicatescheme, which gives it a new GUID.
        /// </summary>
        public static string PowerPlanTemplate(string guid, string name, IDictionary<string, string> plans)
        {
            if (IsBuiltInPowerPlan(guid))
            {
                return guid.ToLowerInvariant();
            }

            foreach (string template in Templates)
            {
                string listed;
                if (string.Equals(name, TemplateName(template), StringComparison.OrdinalIgnoreCase)
                    || (plans.TryGetValue(template, out listed) && string.Equals(name, listed, StringComparison.OrdinalIgnoreCase)))
                {
                    return template;
                }
            }

            return null;
        }

        /// <summary>
        ///     How a restore reaches the backup's plan on a PC listing <paramref name="plans" />: "already" when it
        ///     is active; "activate" with the GUID of a listed plan of that GUID or name, else of the plan's
        ///     template (its own GUID or the backup's <paramref name="template" />); "copy" with the template's
        ///     GUID when the template can only be copied; or "missing".
        /// </summary>
        public static string PowerPlanAction(string guid, string name, string template, IDictionary<string, string> plans, string activeGuid, out string useGuid)
        {
            useGuid = null;
            if (plans.ContainsKey(guid))
            {
                useGuid = guid;
            }
            else if (!string.IsNullOrEmpty(name))
            {
                foreach (KeyValuePair<string, string> plan in plans)
                {
                    if (string.Equals(plan.Value, name, StringComparison.OrdinalIgnoreCase))
                    {
                        useGuid = plan.Key;
                        break;
                    }
                }
            }

            string copyOf = IsBuiltInPowerPlan(guid) ? guid : IsBuiltInPowerPlan(template) ? template : null;
            if (useGuid == null && copyOf != null && plans.ContainsKey(copyOf))
            {
                useGuid = copyOf.ToLowerInvariant();
            }

            if (useGuid != null)
            {
                return string.Equals(useGuid, activeGuid, StringComparison.OrdinalIgnoreCase) ? "already" : "activate";
            }

            if (copyOf == null)
            {
                return "missing";
            }

            useGuid = copyOf.ToLowerInvariant();
            return "copy";
        }

        /// <summary>Copies a Windows template into a new listed plan. The new plan's GUID, or null with powercfg's message.</summary>
        public static string DuplicatePowerPlan(string templateGuid, out string problem)
        {
            string output;
            problem = null;
            if (PowerCfg("/duplicatescheme " + templateGuid, out output) == 0)
            {
                Match m = GuidPattern.Match(output);
                if (m.Success)
                {
                    return m.Groups[1].Value.ToLowerInvariant();
                }
            }

            problem = output.Trim();
            return null;
        }

        /// <summary>Makes a listed plan the active one. Null on success, otherwise powercfg's message.</summary>
        public static string ActivatePowerPlan(string guid)
        {
            string output;
            return PowerCfg("/setactive " + guid, out output) == 0 ? null : output.Trim();
        }

        /// <summary>Removes a plan from the list. Null on success, otherwise powercfg's message.</summary>
        public static string DeletePowerPlan(string guid)
        {
            if (guid == null || !Regex.IsMatch(guid, "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$") || IsBuiltInPowerPlan(guid))
            {
                return "not a plan this tool can remove: " + (guid ?? "none");
            }

            string output;
            return PowerCfg("/delete " + guid, out output) == 0 ? null : output.Trim();
        }

        private const int PowerCfgTimeoutMs = 30000;

        private static int PowerCfg(string arguments, out string output)
        {
            // By full path: a bare name is looked for in the current folder before System32.
            ProcessStartInfo info = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "powercfg.exe"), arguments);
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.CreateNoWindow = true;
            try
            {
                using (Process p = Process.Start(info))
                {
                    // Read while it runs, and stopped when it hangs, so a stuck powercfg fails one part, not the run.
                    Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                    Task<string> stderr = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(PowerCfgTimeoutMs))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                        }
                        catch (System.ComponentModel.Win32Exception)
                        {
                        }

                        output = "powercfg did not finish within " + Inv.S(PowerCfgTimeoutMs / 1000) + " seconds";
                        return -1;
                    }

                    output = stdout.Result + stderr.Result;
                    return p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        // -- Elevation and the Windows user --------------------------------------------------------------

        /// <summary>The SID of the Windows user this process runs as, whose profile the per-user parts write.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static string CurrentUserSid()
        {
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                return identity.User == null ? null : identity.User.Value;
            }
        }

        /// <summary>The user this process runs as, as DOMAIN\name.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static string CurrentUserName()
        {
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                return identity.Name;
            }
        }

        /// <summary>
        ///     The user signed in to this Windows session, as DOMAIN\name, or null when Windows does not say. It
        ///     differs from <see cref="CurrentUserName" /> when an administrator's password was typed at a UAC
        ///     prompt, so the process runs as that administrator.
        /// </summary>
        public static string SessionUserName()
        {
            try
            {
                string user = SessionString(WtsUserName);
                string domain = SessionString(WtsDomainName);
                if (string.IsNullOrEmpty(user))
                {
                    return null;
                }

                return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
        }

        private const int WtsUserName = 5;
        private const int WtsDomainName = 7;

        private static string SessionString(int infoClass)
        {
            IntPtr buffer;
            int bytes;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, infoClass, out buffer, out bytes))
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void WTSFreeMemory(IntPtr memory);

        /// <summary>
        ///     Whether a reparse tag redirects a path somewhere else (a junction, mount point or symbolic link): its
        ///     name-surrogate bit. Cloud Files placeholders, deduplicated and compressed files carry tags without it.
        /// </summary>
        public static bool IsNameSurrogateTag(uint tag)
        {
            return (tag & 0x20000000) != 0;
        }

        /// <summary>
        ///     Whether a file or folder is a link to somewhere else. A reparse point whose tag cannot be read
        ///     counts as a link.
        /// </summary>
        public static bool IsLink(string path)
        {
            if ((System.IO.File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            FindData data;
            IntPtr handle = FindFirstFileW(path.TrimEnd('\\'), out data);
            if (handle == new IntPtr(-1))
            {
                return true;
            }

            FindClose(handle);
            return (data.Attributes & (uint)System.IO.FileAttributes.ReparsePoint) == 0 || IsNameSurrogateTag(data.Reserved0);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public uint CreationLow;
            public uint CreationHigh;
            public uint AccessLow;
            public uint AccessHigh;
            public uint WriteLow;
            public uint WriteHigh;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Reserved0;
            public uint Reserved1;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string FileName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string AlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr FindFirstFileW(string fileName, out FindData data);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool FindClose(IntPtr handle);

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        public static bool IsAdministrator()
        {
            using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
        }
    }
}
