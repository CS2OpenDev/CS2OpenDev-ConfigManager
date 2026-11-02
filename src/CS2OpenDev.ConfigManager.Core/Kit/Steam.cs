#nullable disable
// v1's C# 5 style; see the header of KeyValues.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace CS2OpenDev.ConfigManager.Kit
{
    /// <summary>One Steam account that has CS2 data on this machine.</summary>
    public sealed class SteamAccount
    {
        public SteamAccount(string accountId, string folder, DateTime lastWriteUtc, bool mostRecent)
        {
            AccountId = accountId;
            Folder = folder;
            LastWriteUtc = lastWriteUtc;
            MostRecent = mostRecent;
        }

        /// <summary>The 32-bit account id, the name of the userdata folder.</summary>
        public string AccountId { get; private set; }

        /// <summary><c>userdata/&lt;id&gt;/730</c>.</summary>
        public string Folder { get; private set; }

        /// <summary>The newest file under <see cref="Folder" />'s cfg folders.</summary>
        public DateTime LastWriteUtc { get; private set; }

        /// <summary>Steam's loginusers.vdf marks this account as the one that signed in last.</summary>
        public bool MostRecent { get; private set; }
    }

    /// <summary>Where Steam and CS2 live on this machine. Every member reads; none writes.</summary>
    public sealed class SteamInstall
    {
        public const string Cs2AppId = "730";
        private const long SteamId64Base = 76561197960265728L;

        private SteamInstall(string root)
        {
            Root = root;
        }

        /// <summary>The Steam folder (the one holding steamapps and userdata).</summary>
        public string Root { get; private set; }

        public string UserData
        {
            get { return Path.Combine(Root, "userdata"); }
        }

        /// <summary>Finds Steam, or null. <paramref name="overrideRoot" /> wins when given.</summary>
        public static SteamInstall Locate(string overrideRoot)
        {
            if (!string.IsNullOrEmpty(overrideRoot))
            {
                return Directory.Exists(overrideRoot) ? new SteamInstall(Path.GetFullPath(overrideRoot)) : null;
            }

            if (ConfigBackup.MachineWritesRefused)
            {
                throw new InvalidOperationException("A test run never looks for this PC's own Steam; name a Steam folder.");
            }

            List<string> candidates = new List<string>();
            if (Platform.IsWindows)
            {
                string fromRegistry = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
                if (!string.IsNullOrEmpty(fromRegistry))
                {
                    candidates.Add(fromRegistry.Replace('/', '\\'));
                }

                string machine = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
                if (!string.IsNullOrEmpty(machine))
                {
                    candidates.Add(machine);
                }

                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            }
            else
            {
                return LocateUnderHome(Environment.GetEnvironmentVariable("HOME") ?? "");
            }

            return FirstSteam(candidates);
        }

        /// <summary>
        ///     The Linux Steam roots under <paramref name="home" />, in order: ~/.steam/steam (usually a link to one of
        ///     the others), ~/.local/share/Steam, and Flatpak's. Tests pass a home of their own; nothing else is looked at.
        /// </summary>
        internal static SteamInstall LocateUnderHome(string home)
        {
            List<string> candidates = new List<string>();
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            return FirstSteam(candidates);
        }

        /// <summary>
        ///     The first candidate that holds a Steam install, by the folder a link points to, so one Steam is always
        ///     named by one path (records compare the Steam folder their restore wrote to by path).
        /// </summary>
        private static SteamInstall FirstSteam(List<string> candidates)
        {
            foreach (string candidate in candidates)
            {
                if (!Directory.Exists(Path.Combine(candidate, "steamapps")) && !Directory.Exists(Path.Combine(candidate, "userdata")))
                {
                    continue;
                }

                FileSystemInfo target = null;
                try
                {
                    target = new DirectoryInfo(candidate).ResolveLinkTarget(true);
                }
                catch (IOException)
                {
                }

                return new SteamInstall(target != null ? target.FullName : Path.GetFullPath(candidate));
            }

            return null;
        }

        /// <summary>Every Steam library folder, the main one first.</summary>
        public IList<string> Libraries()
        {
            List<string> libraries = new List<string>();
            libraries.Add(Root);
            string vdf = Path.Combine(Root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                return libraries;
            }

            KvNode root;
            try
            {
                root = KvNode.Load(vdf);
            }
            catch (FormatException)
            {
                return libraries;
            }

            foreach (KvNode entry in root.Children)
            {
                // Library entries are numbered; the old flat format also holds stats keys beside them.
                long index;
                if (!long.TryParse(entry.Key, out index))
                {
                    continue;
                }

                string path = entry.IsSection ? entry.Get("path") : entry.Value;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                string full = Path.GetFullPath(path);
                bool seen = false;
                foreach (string known in libraries)
                {
                    if (string.Equals(known.TrimEnd('\\', '/'), full.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        seen = true;
                    }
                }

                if (!seen)
                {
                    libraries.Add(full);
                }
            }

            return libraries;
        }

        /// <summary>
        ///     The CS2 install folder (<c>.../steamapps/common/Counter-Strike Global Offensive</c>), or null when
        ///     no library holds the app manifest.
        /// </summary>
        public string Cs2InstallDir()
        {
            foreach (string library in Libraries())
            {
                string manifest = Path.Combine(library, "steamapps", "appmanifest_" + Cs2AppId + ".acf");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                string installDir = "Counter-Strike Global Offensive";
                try
                {
                    string named = KvNode.Load(manifest).Get("installdir");
                    if (!string.IsNullOrEmpty(named))
                    {
                        installDir = named;
                    }
                }
                catch (FormatException)
                {
                }

                string dir = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(dir))
                {
                    return dir;
                }
            }

            return null;
        }

        /// <summary><c>game/csgo/cfg</c> under the install, or null.</summary>
        public string Cs2CfgDir()
        {
            string install = Cs2InstallDir();
            return install == null ? null : Path.Combine(install, "game", "csgo", "cfg");
        }

        /// <summary><c>game/bin/win64/cs2.exe</c> under the install, or null.</summary>
        public string Cs2Exe()
        {
            string install = Cs2InstallDir();
            return install == null ? null : Path.Combine(install, "game", "bin", "win64", "cs2.exe");
        }

        /// <summary>Accounts with a userdata/&lt;id&gt;/730 folder, the most likely one first.</summary>
        public IList<SteamAccount> Accounts()
        {
            List<SteamAccount> accounts = new List<SteamAccount>();
            if (!Directory.Exists(UserData))
            {
                return accounts;
            }

            string mostRecent = MostRecentAccountId();
            foreach (string dir in Directory.GetDirectories(UserData))
            {
                string id = Path.GetFileName(dir);
                long numeric;
                if (!long.TryParse(id, out numeric) || numeric <= 0)
                {
                    continue;
                }

                string app = Path.Combine(dir, Cs2AppId);
                if (!Directory.Exists(app))
                {
                    continue;
                }

                accounts.Add(new SteamAccount(id, app, NewestWrite(app), id == mostRecent));
            }

            accounts.Sort(delegate(SteamAccount a, SteamAccount b)
            {
                if (a.MostRecent != b.MostRecent)
                {
                    return a.MostRecent ? -1 : 1;
                }

                return b.LastWriteUtc.CompareTo(a.LastWriteUtc);
            });
            return accounts;
        }

        /// <summary>
        ///     An account id as Steam names its userdata folder (all digits, a nonzero 32-bit number), with a
        ///     SteamID64 turned into one; null for anything else, so no id can name a path outside userdata.
        /// </summary>
        public static string NormalizeAccountId(string id)
        {
            string text = (id ?? "").Trim();
            if (text.Length == 0 || text.Length > 20)
            {
                return null;
            }

            foreach (char c in text)
            {
                if (c < '0' || c > '9')
                {
                    return null;
                }
            }

            ulong value;
            if (!ulong.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                return null;
            }

            if (value > uint.MaxValue && value > (ulong)SteamId64Base)
            {
                value -= (ulong)SteamId64Base;
            }

            return value == 0 || value > uint.MaxValue ? null : Inv.S((long)value);
        }

        /// <summary>The userdata folder of an account, whether or not it exists yet.</summary>
        public string AccountFolder(string accountId)
        {
            return Path.Combine(UserData, accountId, Cs2AppId);
        }

        public string LocalConfigPath(string accountId)
        {
            return Path.Combine(UserData, accountId, "config", "localconfig.vdf");
        }

        /// <summary>The account Steam signed in last, from loginusers.vdf, as a 32-bit id; null when unknown.</summary>
        public string MostRecentAccountId()
        {
            string path = Path.Combine(Root, "config", "loginusers.vdf");
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                foreach (KvNode user in KvNode.Load(path).Children)
                {
                    long id64;
                    if (user.IsSection && user.Get("MostRecent") == "1" && long.TryParse(user.Key, out id64) && id64 > SteamId64Base)
                    {
                        return Inv.S(id64 - SteamId64Base);
                    }
                }
            }
            catch (FormatException)
            {
            }
            catch (IOException)
            {
            }

            return null;
        }

        /// <summary>The CS2 launch options in an account's localconfig.vdf, or null when none are set.</summary>
        public string ReadLaunchOptions(string accountId)
        {
            string path = LocalConfigPath(accountId);
            if (!File.Exists(path))
            {
                return null;
            }

            KvNode app = KvNode.Load(path).Path("Software", "Valve", "Steam", "apps", Cs2AppId);
            return app == null ? null : app.Get("LaunchOptions");
        }

        /// <summary>
        ///     Writes the CS2 launch options into an account's localconfig.vdf; null removes them. Steam rewrites
        ///     the file when it exits, so call this only while Steam is closed. The previous file is copied to
        ///     <paramref name="copyTo" /> first when given, so nothing is left beside Steam's own file.
        /// </summary>
        public void WriteLaunchOptions(string accountId, string launchOptions, string copyTo)
        {
            string path = LocalConfigPath(accountId);
            KvNode root;
            if (File.Exists(path))
            {
                root = KvNode.Load(path);
                if (copyTo != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(copyTo));
                    SafeFile.WriteFlushed(copyTo, File.ReadAllBytes(path));
                }
            }
            else
            {
                root = new KvNode("UserLocalConfigStore");
            }

            KvNode app = root.Section("Software").Section("Valve").Section("Steam").Section("apps").Section(Cs2AppId);
            if (launchOptions == null)
            {
                app.Remove("LaunchOptions");
            }
            else
            {
                app.Set("LaunchOptions", launchOptions);
            }

            root.Save(path);
        }

        public static bool IsRunning(string processName)
        {
            Process[] found = Process.GetProcessesByName(processName);
            foreach (Process p in found)
            {
                p.Dispose();
            }

            return found.Length > 0;
        }

        private static DateTime NewestWrite(string dir)
        {
            DateTime newest = DateTime.MinValue;
            foreach (string sub in new string[] { Path.Combine(dir, "local", "cfg"), Path.Combine(dir, "remote") })
            {
                if (!Directory.Exists(sub))
                {
                    continue;
                }

                foreach (string file in Directory.GetFiles(sub))
                {
                    DateTime t = File.GetLastWriteTimeUtc(file);
                    if (t > newest)
                    {
                        newest = t;
                    }
                }
            }

            return newest;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        internal static string ReadRegistryString(RegistryKey hive, string subKey, string name)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(subKey))
                {
                    return key == null ? null : key.GetValue(name) as string;
                }
            }
            catch (System.Security.SecurityException)
            {
                return null;
            }
        }
    }

    internal static class Platform
    {
        [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
        public static bool IsWindows
        {
            get { return Path.DirectorySeparatorChar == '\\'; }
        }
    }
}
