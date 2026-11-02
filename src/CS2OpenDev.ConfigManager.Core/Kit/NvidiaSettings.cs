#nullable disable
#pragma warning disable CA1859
// v1's C# 5 style; see the header of KeyValues.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CS2OpenDev.ConfigManager.Kit
{
    /// <summary>One NVIDIA driver setting: what NVIDIA Control Panel's "Manage 3D settings" edits.</summary>
    public sealed class NvSetting
    {
        public NvSetting(uint id, string name, string type, string value)
        {
            Id = id;
            Name = name;
            Type = type;
            Value = value;
        }

        public uint Id { get; private set; }

        /// <summary>The driver's own name for it; shown, never used to find it.</summary>
        public string Name { get; private set; }

        /// <summary>dword, qword, wstring or binary.</summary>
        public string Type { get; private set; }

        /// <summary>Decimal for dword and qword, the text for wstring, hex for binary.</summary>
        public string Value { get; private set; }

        public string IdText
        {
            get { return "0x" + Id.ToString("X8", System.Globalization.CultureInfo.InvariantCulture); }
        }
    }

    /// <summary>The driver refused a write because it needs an administrator.</summary>
    public sealed class NvAdminRequiredException : Exception
    {
        public NvAdminRequiredException()
            : base("Saving NVIDIA settings needs an administrator.")
        {
        }
    }

    /// <summary>
    ///     A session on the NVIDIA driver's settings database (DRS), through nvapi64.dll. The function ids and
    ///     struct layouts are NVIDIA's published ones (NVIDIA/nvapi on GitHub: nvapi_interface.h and nvapi.h;
    ///     NVDRS_SETTING is packed to 4 and 12320 bytes). Reading needs no rights; saving needs an administrator.
    /// </summary>
    public sealed class NvidiaDrs : IDisposable
    {
        public const string Cs2Exe = "cs2.exe";

        private const int Ok = 0;
        private const int IncompatibleStructVersion = -9;
        private const int InvalidUserPrivilege = -137;
        private const int SettingNotFound = -160;
        private const int LocationCurrent = 0;
        private const int LocationGlobal = 1;
        private const int ProfileNotFound = -163;
        private const int ExecutableNotFound = -166;

        private const int UnicodeBytes = 4096;

        /// <summary>NVDRS_BINARY_SETTING's valueData: the most a binary setting holds.</summary>
        private const int BinaryBytes = 4096;
        private const int SettingSize = 12320;
        private const int SettingName = 4;
        private const int SettingId = 4100;
        private const int SettingType = 4104;
        private const int SettingLocation = 4108;
        private const int SettingIsCurrentPredefined = 4112;
        private const int SettingIsPredefinedValid = 4116;
        private const int SettingPredefined = 4120;
        private const int SettingCurrent = 8220;
        private const int ProfileSize = 4116;
        private const int ProfileIsPredefined = 4104;

        private const uint TypeDword = 0;
        private const uint TypeBinary = 1;
        private const uint TypeString = 2;
        private const uint TypeWString = 3;
        private const uint TypeQword = 4;

        private static readonly object InitLock = new object();
        private static bool _initialized;

        private IntPtr _session;

        private NvidiaDrs(IntPtr session)
        {
            _session = session;
        }

        // -- Entry points -----------------------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OutHandle(out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OnHandle(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionOutHandle(IntPtr session, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionHandlePtr(IntPtr session, IntPtr profile, IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindApp(IntPtr session, IntPtr name, out IntPtr profile, IntPtr app);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, IntPtr setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResetSettingFn(IntPtr session, IntPtr profile, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDisplayFn(uint index, out IntPtr display);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDvcFn(IntPtr display, uint output, IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetDvcFn(IntPtr display, uint output, int level);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DriverVersionFn(out uint version, IntPtr branch);

        // From System32 only: on a PC without an NVIDIA driver the name would otherwise be looked for in the
        // current folder and on PATH, where anyone could have left a DLL of that name.
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr QueryInterface64(uint id);

        [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr QueryInterface32(uint id);

        private static T Fn<T>(uint id) where T : class
        {
            IntPtr pointer = IntPtr.Size == 8 ? QueryInterface64(id) : QueryInterface32(id);
            if (pointer == IntPtr.Zero)
            {
                throw new NotSupportedException("This NVIDIA driver has no function 0x" + id.ToString("X8", CultureInfo.InvariantCulture) + ".");
            }

            // The generic overload: Native AOT builds each delegate's marshalling ahead of time, for every T used here.
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        /// <summary>
        ///     Loads NVAPI. Null when it loaded, otherwise why not, in user terms: no NVIDIA driver, or a driver
        ///     too old for what this needs.
        /// </summary>
        public static string Initialize()
        {
            bool mayPass;
            return Initialize(out mayPass);
        }

        /// <summary>
        ///     <see cref="Initialize()" />, also saying whether the reason may pass: false only for a PC with no NVIDIA
        ///     driver or no NVIDIA GPU, where trying again later changes nothing.
        /// </summary>
        public static string Initialize(out bool mayPass)
        {
            mayPass = true;
            // A driver that is updating or recovering can block NVAPI's start for good; the caller gets a reason
            // after the timeout instead, and the stuck call is left on a background thread of its own.
            if (!Monitor.TryEnter(InitLock, InitTimeoutMs))
            {
                return NoAnswer;
            }

            try
            {
                if (_initialized)
                {
                    return null;
                }

                if (_initWorker != null && _initWorker.IsAlive)
                {
                    return NoAnswer;
                }

                _initWorker = new Thread(new ThreadStart(InitializeOnWorker));
                _initWorker.IsBackground = true;
                _initWorker.Start();
                if (!_initWorker.Join(InitTimeoutMs))
                {
                    return NoAnswer;
                }

                if (_initProblem == null)
                {
                    _initialized = true;
                }

                mayPass = !_initPermanent;
                return _initProblem;
            }
            finally
            {
                Monitor.Exit(InitLock);
            }
        }

        private const int InitTimeoutMs = 30000;

        private const string NoAnswer = "the NVIDIA driver did not answer within 30 seconds";

        private static Thread _initWorker;

        private static string _initProblem;

        /// <summary>Whether <see cref="_initProblem" /> is one that stays: no NVIDIA driver, or no NVIDIA GPU.</summary>
        private static bool _initPermanent;

        /// <summary>NVAPI_LIBRARY_NOT_FOUND.</summary>
        private const int LibraryNotFound = -2;

        /// <summary>NVAPI_NVIDIA_DEVICE_NOT_FOUND.</summary>
        private const int DeviceNotFound = -6;

        /// <summary>
        ///     Whether NvAPI_Initialize's <paramref name="status" /> stays so: no NVIDIA GPU, or no driver library. Any
        ///     other failure may be a driver that is updating or recovering, so it is tried again later.
        /// </summary>
        internal static bool IsLastingInitStatus(int status)
        {
            return status == DeviceNotFound || status == LibraryNotFound;
        }

        /// <summary>Why NvAPI_Initialize's <paramref name="status" /> stopped NVAPI, or null for OK.</summary>
        internal static string InitProblem(int status)
        {
            if (status == Ok)
            {
                return null;
            }

            return IsLastingInitStatus(status)
                ? "no NVIDIA GPU drives a display here (NVAPI " + Inv.S(status) + ")"
                : "the NVIDIA driver did not start (NVAPI " + Inv.S(status) + "); it may be updating or recovering";
        }

        private static void InitializeOnWorker()
        {
            // Each attempt says for itself whether its reason stays.
            _initPermanent = false;
            try
            {
                int status = Fn<NoArgs>(0x0150E828)();
                _initProblem = InitProblem(status);
                _initPermanent = IsLastingInitStatus(status);
            }
            catch (DllNotFoundException)
            {
                _initProblem = "no NVIDIA driver is installed";
                _initPermanent = true;
            }
            catch (EntryPointNotFoundException)
            {
                _initProblem = "no NVIDIA driver is installed";
                _initPermanent = true;
            }
            catch (BadImageFormatException)
            {
                _initProblem = "the NVIDIA driver library could not be loaded";
            }
            catch (NotSupportedException ex)
            {
                _initProblem = ex.Message;
            }
            catch (Exception ex)
            {
                _initProblem = "the NVIDIA driver library failed to start: " + ex.Message;
            }
        }

        /// <summary>Opens the settings database, or throws with the reason <see cref="Initialize()" /> gave.</summary>
        public static NvidiaDrs Open()
        {
            string why = Initialize();
            if (why != null)
            {
                throw new InvalidOperationException(why);
            }

            IntPtr session;
            Check(Fn<OutHandle>(0x0694D52E)(out session), "create a settings session");
            NvidiaDrs drs = new NvidiaDrs(session);
            try
            {
                Check(Fn<OnHandle>(0x375DBD6B)(session), "load the driver settings");
            }
            catch
            {
                drs.Dispose();
                throw;
            }

            return drs;
        }

        /// <summary>The driver version as NVIDIA writes it (581.57), or null.</summary>
        public static string DriverVersion()
        {
            if (Initialize() != null)
            {
                return null;
            }

            IntPtr branch = Marshal.AllocHGlobal(64);
            try
            {
                uint version;
                if (Fn<DriverVersionFn>(0x2926AAAD)(out version, branch) != Ok)
                {
                    return null;
                }

                return Inv.S(version / 100) + "." + (version % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                Marshal.FreeHGlobal(branch);
            }
        }

        public void Dispose()
        {
            if (_session != IntPtr.Zero)
            {
                Fn<OnHandle>(0xDAD9CFF8)(_session);
                _session = IntPtr.Zero;
            }
        }

        // -- Profiles ---------------------------------------------------------------------------------

        /// <summary>The profile the driver applies to cs2.exe, or IntPtr.Zero when there is none.</summary>
        public IntPtr FindCs2Profile()
        {
            IntPtr name = WideBuffer(Cs2Exe);
            try
            {
                // Older drivers took the smaller application structs; a newer one may refuse those.
                int[][] versions = { new int[] { 16396, 3 }, new int[] { 16392, 2 }, new int[] { 12296, 1 } };
                foreach (int[] version in versions)
                {
                    IntPtr app = Zeroed(20492);
                    try
                    {
                        Marshal.WriteInt32(app, 0, Version(version[0], version[1]));
                        IntPtr profile;
                        int status = Fn<FindApp>(0xEEE566B2)(_session, name, out profile, app);
                        if (status == Ok)
                        {
                            return profile;
                        }

                        if (status == ExecutableNotFound || status == ProfileNotFound)
                        {
                            return IntPtr.Zero;
                        }

                        if (status != IncompatibleStructVersion)
                        {
                            Check(status, "find the CS2 profile");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(app);
                    }
                }

                throw new NotSupportedException("This NVIDIA driver accepts none of the known application formats.");
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }

        /// <summary>The profile NVIDIA Control Panel's "Global Settings" tab edits.</summary>
        public IntPtr GlobalProfile()
        {
            IntPtr profile;
            Check(Fn<SessionOutHandle>(0x617BFF9F)(_session, out profile), "find the global profile");
            return profile;
        }

        public string ProfileName(IntPtr profile)
        {
            IntPtr info = Zeroed(ProfileSize);
            try
            {
                Marshal.WriteInt32(info, 0, Version(ProfileSize, 1));
                Check(Fn<SessionHandlePtr>(0x61CD6FD6)(_session, profile, info), "read a profile");
                return Marshal.PtrToStringUni(new IntPtr(info.ToInt64() + 4));
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        // -- Settings ---------------------------------------------------------------------------------

        /// <summary>
        ///     The 3D settings a backup carries, by NVIDIA's ids and names (NvApiDriverSettings.h): the ones NVIDIA
        ///     Control Panel shows that change how CS2 runs or looks. Any other id in a backup is refused.
        /// </summary>
        public static readonly uint[] CarriedSettings =
        {
            0x007BA09E, // Maximum pre-rendered frames (Low Latency Mode)
            0x10835002, // Frame Rate Limiter (Max Frame Rate)
            0x1057EB71, // Power management mode
            0x00A879CF, // Vertical Sync
            0x005A375C, // Vertical Sync Tear Control
            0x20C1221E, // Threaded optimization
            0x00CE2691, // Texture filtering - Quality
            0x00E73211, // Texture filtering - Anisotropic sample optimization
            0x0084CD70, // Texture filtering - Anisotropic filter optimization
            0x002ECAF2, // Texture filtering - Trilinear optimization
            0x0019BB68, // Texture filtering - Negative LOD bias
            0x101E61A9, // Anisotropic filtering setting
            0x10D2BB16, // Anisotropic filtering mode
            0x1074C972  // Enable FXAA
        };

        public static bool IsCarriedSetting(uint id)
        {
            return Array.IndexOf(CarriedSettings, id) >= 0;
        }

        /// <summary>The NVDRS type a carried setting has (every one is a DWORD), or null for any other id.</summary>
        public static string CarriedType(uint id)
        {
            return IsCarriedSetting(id) ? "dword" : null;
        }

        /// <summary>
        ///     The value cs2.exe gets for a setting through <paramref name="profile" />, wherever it comes from: set
        ///     on the profile, inherited from the global profile, or the driver's own. Null when the driver has no
        ///     value for it. <paramref name="userSet" /> says whether a user put the value there, on this profile or
        ///     the global one, rather than the driver.
        /// </summary>
        public NvSetting EffectiveSetting(IntPtr profile, uint id, out bool userSet)
        {
            userSet = false;
            IntPtr item = Zeroed(SettingSize);
            try
            {
                Marshal.WriteInt32(item, 0, Version(SettingSize, 1));
                int status = Fn<GetSettingFn>(0x73BF8338)(_session, profile, id, item);
                if (status == SettingNotFound)
                {
                    return null;
                }

                Check(status, "read a setting");
                int location = Marshal.ReadInt32(item, SettingLocation);
                userSet = (location == LocationCurrent || location == LocationGlobal) && Marshal.ReadInt32(item, SettingIsCurrentPredefined) == 0;
                return Decode(item, SettingCurrent);
            }
            finally
            {
                Marshal.FreeHGlobal(item);
            }
        }

        /// <summary>
        ///     One setting as the user set it on <paramref name="profile" />, or null when the profile carries no
        ///     user value for it (the driver's default or predefined value applies).
        /// </summary>
        public NvSetting UserSetting(IntPtr profile, uint id)
        {
            IntPtr item = Zeroed(SettingSize);
            try
            {
                Marshal.WriteInt32(item, 0, Version(SettingSize, 1));
                int status = Fn<GetSettingFn>(0x73BF8338)(_session, profile, id, item);
                if (status == SettingNotFound)
                {
                    return null;
                }

                Check(status, "read a setting");
                return IsUserSet(item) ? Decode(item, SettingCurrent) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(item);
            }
        }

        /// <summary>
        ///     Whether cs2.exe gets a setting from a value a user put on the global profile, because
        ///     <paramref name="profile" /> has neither its own value nor a predefined one for it.
        /// </summary>
        public bool InheritsUserGlobalValue(IntPtr profile, uint id)
        {
            IntPtr item = Zeroed(SettingSize);
            try
            {
                Marshal.WriteInt32(item, 0, Version(SettingSize, 1));
                int status = Fn<GetSettingFn>(0x73BF8338)(_session, profile, id, item);
                if (status == SettingNotFound)
                {
                    return false;
                }

                Check(status, "read a setting");
                return Marshal.ReadInt32(item, SettingLocation) == LocationGlobal && Marshal.ReadInt32(item, SettingIsCurrentPredefined) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(item);
            }
        }

        /// <summary>
        ///     The driver's own value for a setting on <paramref name="profile" />, ignoring any value a user set
        ///     there (NVDRS_SETTING's predefined value), or null when the driver reports none.
        /// </summary>
        public NvSetting PredefinedSetting(IntPtr profile, uint id)
        {
            IntPtr item = Zeroed(SettingSize);
            try
            {
                Marshal.WriteInt32(item, 0, Version(SettingSize, 1));
                int status = Fn<GetSettingFn>(0x73BF8338)(_session, profile, id, item);
                if (status == SettingNotFound)
                {
                    return null;
                }

                Check(status, "read a setting");
                return Marshal.ReadInt32(item, SettingIsPredefinedValid) != 0 ? Decode(item, SettingPredefined) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(item);
            }
        }

        public void SetSetting(IntPtr profile, NvSetting setting)
        {
            IntPtr item = Zeroed(SettingSize);
            try
            {
                Marshal.WriteInt32(item, 0, Version(SettingSize, 1));
                Marshal.WriteInt32(item, SettingId, unchecked((int)setting.Id));
                switch (setting.Type)
                {
                    case "dword":
                        Marshal.WriteInt32(item, SettingType, (int)TypeDword);
                        Marshal.WriteInt32(item, SettingCurrent, unchecked((int)uint.Parse(setting.Value, System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    case "qword":
                        Marshal.WriteInt32(item, SettingType, (int)TypeQword);
                        Marshal.WriteInt64(item, SettingCurrent, unchecked((long)ulong.Parse(setting.Value, System.Globalization.CultureInfo.InvariantCulture)));
                        break;
                    case "wstring":
                        Marshal.WriteInt32(item, SettingType, (int)TypeWString);
                        WriteWide(item, SettingCurrent, setting.Value);
                        break;
                    case "binary":
                        byte[] data = Inv.Unhex(setting.Value);
                        if (data.Length > BinaryBytes)
                        {
                            throw new FormatException("An NVIDIA binary setting holds at most " + Inv.S(BinaryBytes) + " bytes.");
                        }

                        Marshal.WriteInt32(item, SettingType, (int)TypeBinary);
                        Marshal.WriteInt32(item, SettingCurrent, data.Length);
                        Marshal.Copy(data, 0, new IntPtr(item.ToInt64() + SettingCurrent + 4), data.Length);
                        break;
                    default:
                        throw new FormatException("Unknown NVIDIA setting type '" + setting.Type + "'.");
                }

                Check(Fn<SessionHandlePtr>(0x577DD202)(_session, profile, item), "set " + setting.IdText);
            }
            finally
            {
                Marshal.FreeHGlobal(item);
            }
        }

        /// <summary>Puts a setting back to the driver's own value for the profile.</summary>
        public void ResetSetting(IntPtr profile, uint id)
        {
            int status = Fn<ResetSettingFn>(0x53F0381E)(_session, profile, id);
            if (status != SettingNotFound)
            {
                Check(status, "reset a setting");
            }
        }

        /// <summary>Writes the session's changes to the driver. Throws <see cref="NvAdminRequiredException" /> without rights.</summary>
        public void Save()
        {
            int status = Fn<OnHandle>(0xFCBC7E14)(_session);
            if (status == InvalidUserPrivilege)
            {
                throw new NvAdminRequiredException();
            }

            Check(status, "save the driver settings");
        }

        private static bool IsUserSet(IntPtr item)
        {
            return Marshal.ReadInt32(item, SettingLocation) == 0 && Marshal.ReadInt32(item, SettingIsCurrentPredefined) == 0;
        }

        /// <summary>The setting's current value, or its predefined one, by the offset of that union in NVDRS_SETTING.</summary>
        private static NvSetting Decode(IntPtr item, int valueOffset)
        {
            uint id = unchecked((uint)Marshal.ReadInt32(item, SettingId));
            string name = Marshal.PtrToStringUni(new IntPtr(item.ToInt64() + SettingName));
            IntPtr current = new IntPtr(item.ToInt64() + valueOffset);
            switch ((uint)Marshal.ReadInt32(item, SettingType))
            {
                case TypeDword:
                    return new NvSetting(id, name, "dword", Inv.S(unchecked((uint)Marshal.ReadInt32(current))));
                case TypeQword:
                    return new NvSetting(id, name, "qword", unchecked((ulong)Marshal.ReadInt64(current)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                case TypeString:
                case TypeWString:
                    return new NvSetting(id, name, "wstring", Marshal.PtrToStringUni(current));
                case TypeBinary:
                    int length = Math.Min(Marshal.ReadInt32(current), BinaryBytes);
                    byte[] data = new byte[Math.Max(length, 0)];
                    Marshal.Copy(new IntPtr(current.ToInt64() + 4), data, 0, data.Length);
                    return new NvSetting(id, name, "binary", Inv.Hex(data));
                default:
                    return null;
            }
        }

        // -- Digital vibrance -------------------------------------------------------------------------

        /// <summary>
        ///     Each NVIDIA display's digital vibrance as current, min and max, in NVAPI's enumeration order. The two
        ///     DVC calls are not in NVIDIA's public header; their ids are the ones vibrance tools have used for
        ///     years. Empty when the driver lacks them.
        /// </summary>
        public static IList<int[]> ReadVibrance()
        {
            List<int[]> levels = new List<int[]>();
            if (Initialize() != null)
            {
                return levels;
            }

            GetDvcFn get;
            try
            {
                get = Fn<GetDvcFn>(0x4085DE45);
            }
            catch (NotSupportedException)
            {
                return levels;
            }

            IntPtr info = Zeroed(16);
            try
            {
                foreach (IntPtr display in Displays())
                {
                    Clear(info, 0, 16);
                    Marshal.WriteInt32(info, 0, Version(16, 1));
                    if (get(display, 0, info) == Ok)
                    {
                        levels.Add(new int[] { Marshal.ReadInt32(info, 4), Marshal.ReadInt32(info, 8), Marshal.ReadInt32(info, 12) });
                    }
                    else
                    {
                        levels.Add(null);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }

            return levels;
        }

        /// <summary>Sets the vibrance of the display at <paramref name="index" /> in NVAPI's order.</summary>
        public static void SetVibrance(int index, int level)
        {
            string why = Initialize();
            if (why != null)
            {
                throw new InvalidOperationException(why);
            }

            IList<IntPtr> displays = Displays();
            if (index >= displays.Count)
            {
                throw new InvalidOperationException("this PC has no NVIDIA display number " + Inv.S(index + 1));
            }

            Check(Fn<SetDvcFn>(0x172409B4)(displays[index], 0, level), "set digital vibrance");
        }

        private static IList<IntPtr> Displays()
        {
            List<IntPtr> displays = new List<IntPtr>();
            EnumDisplayFn enumerate = Fn<EnumDisplayFn>(0x9ABDD40D);
            for (uint i = 0; i < 16; i++)
            {
                IntPtr display;
                if (enumerate(i, out display) != Ok)
                {
                    break;
                }

                displays.Add(display);
            }

            return displays;
        }

        // -- Buffers ----------------------------------------------------------------------------------

        private static int Version(int size, int version)
        {
            return size | (version << 16);
        }

        private static IntPtr Zeroed(int size)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            Clear(buffer, 0, size);
            return buffer;
        }

        private static void Clear(IntPtr buffer, int offset, int size)
        {
            byte[] zeros = new byte[size];
            Marshal.Copy(zeros, 0, new IntPtr(buffer.ToInt64() + offset), size);
        }

        private static IntPtr WideBuffer(string text)
        {
            IntPtr buffer = Zeroed(UnicodeBytes);
            WriteWide(buffer, 0, text);
            return buffer;
        }

        private static void WriteWide(IntPtr buffer, int offset, string text)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text ?? "");
            int length = Math.Min(bytes.Length, UnicodeBytes - 2);
            Marshal.Copy(bytes, 0, new IntPtr(buffer.ToInt64() + offset), length);
            Marshal.WriteInt16(buffer, offset + length, 0);
        }

        private static void Check(int status, string what)
        {
            if (status == InvalidUserPrivilege)
            {
                throw new NvAdminRequiredException();
            }

            if (status != Ok)
            {
                throw new InvalidOperationException("The NVIDIA driver could not " + what + " (NVAPI " + Inv.S(status) + ").");
            }
        }
    }
}
