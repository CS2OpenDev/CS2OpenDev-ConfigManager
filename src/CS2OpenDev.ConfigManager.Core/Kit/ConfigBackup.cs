#nullable disable
#pragma warning disable
// v1's C# 5 style; see the header of KeyValues.cs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CS2OpenDev.ConfigManager.Kit
{
    /// <summary>The parts of a backup, by the names capture.vdf and acb --only use.</summary>
    public static class Sections
    {
        /// <summary>The account's CS2 config on this PC: binds, settings, crosshair and video, from local/cfg.</summary>
        public const string Cs2 = "cs2";

        /// <summary>The user's own .cfg files in the game's cfg folder (autoexec.cfg and the like).</summary>
        public const string Cs2Game = "cs2game";

        /// <summary>CS2's launch options in Steam.</summary>
        public const string Launch = "launch";

        /// <summary>The accessibility hotkeys (Shift five times, NumLock held, right Shift held), only ever turned off.</summary>
        public const string Input = "input";

        /// <summary>Game Mode and background recording.</summary>
        public const string GameBar = "gamebar";

        /// <summary>cs2.exe's high-performance GPU preference and "Disable fullscreen optimizations".</summary>
        public const string Cs2Exe = "cs2exe";

        /// <summary>The active power plan.</summary>
        public const string Power = "power";

        /// <summary>NVIDIA Control Panel's 3D settings as cs2.exe gets them, from a fixed list, written to the CS2 profile only.</summary>
        public const string Nvidia = "nvidia";

        /// <summary>NVIDIA digital vibrance, restored only to a PC with one NVIDIA display.</summary>
        public const string Vibrance = "vibrance";

        public static readonly string[] All = { Cs2, Cs2Game, Launch, Input, GameBar, Cs2Exe, Power, Nvidia, Vibrance };

        /// <summary>
        ///     Who a section's changes reach, the one place each section is classified: every rule below is read
        ///     from it, and a name that is not a section throws rather than passing as harmless.
        /// </summary>
        public static SectionScope Scope(string section)
        {
            switch (section)
            {
                case Cs2:
                case Launch:
                    return SectionScope.OwnAccount;
                case Cs2Game:
                    return SectionScope.SharedFiles;
                case Input:
                case GameBar:
                case Cs2Exe:
                    return SectionScope.PerUser;
                case Power:
                case Nvidia:
                case Vibrance:
                    return SectionScope.Machine;
                default:
                    throw new ArgumentException("Not a section: " + section);
            }
        }

        /// <summary>Sections read from or written to the Steam folder or the CS2 install.</summary>
        public static bool NeedsSteam(string section)
        {
            return section == Cs2 || section == Cs2Game || section == Launch || section == Cs2Exe;
        }

        /// <summary>Sections that are Windows or driver settings rather than files, so exist only on Windows.</summary>
        public static bool IsMachineWide(string section)
        {
            SectionScope scope = Scope(section);
            return scope == SectionScope.PerUser || scope == SectionScope.Machine;
        }

        /// <summary>Sections written to the profile of the Windows user the restore runs as.</summary>
        public static bool IsPerUser(string section)
        {
            return Scope(section) == SectionScope.PerUser;
        }

        /// <summary>
        ///     Sections whose changes reach other people on the PC: the machine-wide ones, and the game's cfg
        ///     folder, which every Steam account and Windows user who plays CS2 there shares.
        /// </summary>
        public static bool AffectsEveryone(string section)
        {
            return Scope(section) != SectionScope.OwnAccount;
        }

        /// <summary>Sections that change only the player's own Steam account.</summary>
        public static bool IsOwnAccount(string section)
        {
            return Scope(section) == SectionScope.OwnAccount;
        }

        public static string Label(string section)
        {
            switch (section)
            {
                case Cs2: return "CS2 settings, binds, crosshair and video";
                case Cs2Game: return "CS2 cfg files (autoexec and the like), shared by everyone on the PC";
                case Launch: return "CS2 launch options";
                case Input: return "Windows Sticky, Toggle and Filter Keys hotkeys (turned off)";
                case GameBar: return "Windows Game Mode and Game Bar recording";
                case Cs2Exe: return "cs2.exe high-performance GPU and fullscreen optimizations";
                case Power: return "Windows power plan";
                case Nvidia: return "NVIDIA 3D settings for CS2";
                case Vibrance: return "NVIDIA digital vibrance";
                default: return section;
            }
        }
    }

    /// <summary>Who a section's changes reach.</summary>
    public enum SectionScope
    {
        /// <summary>The player's own Steam account only.</summary>
        OwnAccount,

        /// <summary>Files in the CS2 install, which every account on the PC reads.</summary>
        SharedFiles,

        /// <summary>The Windows user profile the restore runs as.</summary>
        PerUser,

        /// <summary>The whole PC: power plan and NVIDIA driver.</summary>
        Machine
    }

    /// <summary>What to capture.</summary>
    public sealed class CaptureOptions
    {
        public CaptureOptions()
        {
            Include = new List<string>(Sections.All);
        }

        /// <summary>The sections to capture.</summary>
        public List<string> Include { get; private set; }

        /// <summary>Steam's folder; null finds it.</summary>
        public string SteamRoot { get; set; }

        /// <summary>The account to capture; null takes the one Steam signed in last.</summary>
        public string AccountId { get; set; }

        /// <summary>Also carry gamestate_integration_*.cfg, which point at programs on this PC.</summary>
        public bool IncludeGameStateIntegration { get; set; }

        /// <summary>Written into backup.vdf as what made it.</summary>
        public string Creator { get; set; }

        /// <summary>Checked between parts: a cancelled capture stops before the next part starts.</summary>
        public CancellationToken Cancellation { get; set; }
    }

    /// <summary>
    ///     Whether a run writes a backup onto the PC or puts back what an earlier restore replaced. It always comes
    ///     from the command the person ran, never from the record being applied: v1 read it from the manifest, so a
    ///     backup that called itself an undo skipped the restore's path rules.
    /// </summary>
    public enum RestoreMode
    {
        Restore,
        Undo
    }

    /// <summary>
    ///     What a restore applies: the capture record and the bytes of the files it lists. A verified archive
    ///     serves both from memory, so nothing a restore writes is read back from a folder someone could change.
    /// </summary>
    public interface IBackupSource
    {
        /// <summary>The capture record (a backup's capture.vdf, an undo record's backup.vdf).</summary>
        KvNode Manifest { get; }

        /// <summary>The bytes of a file the record lists under <paramref name="section" />, or null when there are none.</summary>
        byte[] ReadFile(string section, string relative);

        /// <summary>Writes <see cref="Manifest" /> back, so an undo forgets each part once it is put back.</summary>
        void SaveManifest();
    }

    /// <summary>A capture or undo record kept as a folder: backup.vdf beside a files tree.</summary>
    public sealed class FolderSource : IBackupSource
    {
        private readonly string _dir;

        public FolderSource(string dir)
            : this(dir, ConfigBackup.ReadManifest(dir))
        {
        }

        public FolderSource(string dir, KvNode manifest)
        {
            _dir = dir;
            Manifest = manifest;
        }

        public KvNode Manifest { get; private set; }

        public byte[] ReadFile(string section, string relative)
        {
            string path = Path.Combine(Path.Combine(_dir, "files"), Path.Combine(section, ConfigBackup.SafeRelative(relative).Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        public void SaveManifest()
        {
            SafeFile.WriteRecord(Path.Combine(_dir, ConfigBackup.ManifestName), new UTF8Encoding(false).GetBytes(Manifest.ToText()));
        }
    }

    /// <summary>A target the restore may not write (another user's, denied by an ACL, held open); nothing of its part was written.</summary>
    public sealed class TargetBlockedException : IOException
    {
        public TargetBlockedException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    ///     The undo record could not be claimed (a full disk, a store that refuses the write). Nothing was changed,
    ///     and the half-made claim is gone, so the restore stops before its first change.
    /// </summary>
    public sealed class UndoClaimException : IOException
    {
        public UndoClaimException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>What to restore.</summary>
    public sealed class RestoreOptions
    {
        public RestoreOptions()
        {
            WriteUndo = true;
        }

        /// <summary>The sections to restore; null restores every section the backup holds.</summary>
        public ICollection<string> Only { get; set; }

        public string SteamRoot { get; set; }

        /// <summary>The account to write CS2 settings to; null picks one (see <see cref="ConfigBackup.ResolveAccount" />).</summary>
        public string AccountId { get; set; }

        /// <summary>Record what is replaced so Undo can put it back.</summary>
        public bool WriteUndo { get; set; }

        /// <summary>Say what would change and change nothing.</summary>
        public bool DryRun { get; set; }

        /// <summary>
        ///     The restorer's own undo store, where the record goes. Required for a restore that writes undo: a
        ///     record is never kept beside the backup.
        /// </summary>
        public string UndoRoot { get; set; }

        /// <summary>Extra values the record carries for people reading it (the archive's digest and trust level).</summary>
        public IDictionary<string, string> RecordNotes { get; set; }

        /// <summary>
        ///     Apply even while CS2 or Steam runs. For tests against a Steam folder no running client reads; on a
        ///     real one, a running CS2 or Steam writes its own copy back over the restore when it exits.
        /// </summary>
        public bool SkipProcessChecks { get; set; }

        /// <summary>NVIDIA setting ids a restore leaves alone, from an organiser's policy. An undo puts back every id it recorded.</summary>
        public ICollection<uint> NvidiaDeny { get; set; }

        /// <summary>Whether a process of that name runs; null asks Windows or Linux. Tests stand in for a running CS2.</summary>
        public Func<string, bool> ProcessCheck { get; set; }

        /// <summary>Called with the record's folder just before a failed part is rolled back. Tests only.</summary>
        internal Action<string> BeforeRollback { get; set; }

        internal bool IsRunning(string processName)
        {
            if (SkipProcessChecks)
            {
                return false;
            }

            return ProcessCheck != null ? ProcessCheck(processName) : SteamInstall.IsRunning(processName);
        }
    }

    /// <summary>How one section went.</summary>
    public sealed class SectionResult
    {
        public SectionResult(string section, string outcome, string detail)
        {
            Section = section;
            Outcome = outcome;
            Detail = detail;
        }

        public string Section { get; private set; }

        /// <summary>
        ///     done; skipped (nothing to do); deferred (not now, for a reason that passes, such as CS2 running);
        ///     blocked (a target cannot be written, so nothing of the part was); failed; or admin (needs an
        ///     administrator).
        /// </summary>
        public string Outcome { get; private set; }

        public string Detail { get; private set; }

        public override string ToString()
        {
            return "[" + Outcome + "] " + Sections.Label(Section) + ": " + Detail;
        }
    }

    /// <summary>The outcome of a capture or a restore.</summary>
    public sealed class RunReport
    {
        public RunReport()
        {
            Results = new List<SectionResult>();
            PendingParts = new List<string>();
        }

        public List<SectionResult> Results { get; private set; }

        /// <summary>The undo snapshot a restore wrote, or null.</summary>
        public string UndoDir { get; set; }

        /// <summary>Parts that failed partway and were put back from the record, so the PC has them as before.</summary>
        public List<string> RolledBack { get; private set; } = new List<string>();

        /// <summary>
        ///     Why a failed part could not be rolled back, or null. The restore stopped there, and the record keeps
        ///     what is still to be put back.
        /// </summary>
        public string RollbackProblem { get; set; }

        /// <summary>Why no undo record could be claimed, or null. The restore stopped before its first change.</summary>
        public string ClaimProblem { get; set; }

        /// <summary>
        ///     Each target a run looked at, as "section path sha256" (or "absent"), so a reviewed plan can be refused
        ///     when the PC changed after it was made.
        /// </summary>
        public List<string> Observed { get; private set; } = new List<string>();

        /// <summary>For an undo, the snapshot's parts still to be put back.</summary>
        public List<string> PendingParts { get; private set; }

        /// <summary>For an undo, how many of the snapshot's parts are still to be put back.</summary>
        public int Pending
        {
            get { return PendingParts.Count; }
        }

        public bool NeedsAdmin
        {
            get
            {
                foreach (SectionResult r in Results)
                {
                    if (r.Outcome == "admin")
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public int Count(string outcome)
        {
            int n = 0;
            foreach (SectionResult r in Results)
            {
                if (r.Outcome == outcome)
                {
                    n++;
                }
            }

            return n;
        }

        internal void Add(TextWriter log, string section, string outcome, string detail)
        {
            SectionResult result = new SectionResult(section, outcome, detail);
            Results.Add(result);
            if (log != null)
            {
                log.WriteLine(result.ToString());
            }
        }
    }

    /// <summary>
    ///     Captures a player's CS2, Windows and NVIDIA settings into a folder (backup.vdf plus a files tree),
    ///     and restores such a folder on another PC, keeping an undo snapshot in the same format so the PC can
    ///     be put back the way it was.
    /// </summary>
    public static class ConfigBackup
    {
        public const string ManifestName = "backup.vdf";
        public const string FormatVersion = "1";
        public const string RootKey = "ConfigBackup";
        private const long MaxFileBytes = 4L * 1024 * 1024;

        /// <summary>CS2's video settings; restored by merging so the target keeps what describes its own hardware.</summary>
        public const string VideoFile = "cs2_video.txt";

        /// <summary>The keys in cs2_video.txt that describe the GPU and monitor rather than the player's choices.</summary>
        public static readonly string[] VideoHardwareKeys = { "VendorID", "DeviceID", "setting.monitor_index", "setting.knowndevice", "Autoconfig" };

        /// <summary>CS2's per-machine convars; restored by merging so the target keeps its own devices.</summary>
        public const string MachineConvarsFile = "cs2_machine_convars.vcfg";

        /// <summary>The convars in cs2_machine_convars.vcfg that name this PC's devices or its trust state.</summary>
        public static readonly string[] MachineHardwareKeys = { "voice_device_override", "speaker_config", "joy_name", "trusted_launch" };

        // -- What counts as the player's -------------------------------------------------------------

        /// <summary>
        ///     Whether a file under userdata/&lt;id&gt;/730 is a setting worth carrying, by its path relative to
        ///     that folder with forward slashes: the .vcfg files in local/cfg with their _lastclouded twins, and
        ///     video. remote/ is Steam Cloud's folder and stays Steam Cloud's, so a restore never becomes an
        ///     upload over the player's cloud copy. Caches, ban lists, the trusted-launch list and screenshots
        ///     are left behind.
        /// </summary>
        public static bool IsAccountSetting(string relative)
        {
            string path = relative.Replace('\\', '/');
            string name = path.Substring(path.LastIndexOf('/') + 1);
            if (!path.StartsWith("local/cfg/", StringComparison.OrdinalIgnoreCase) || path.IndexOf('/', 10) >= 0)
            {
                return false;
            }

            return name.EndsWith(".vcfg", StringComparison.OrdinalIgnoreCase)
                   || name.EndsWith(".vcfg_lastclouded", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, VideoFile, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Whether a file in game/csgo/cfg is the player's own rather than one Valve ships, by its path
        ///     relative to that folder. Valve's are the game modes, server.cfg, the *_default.cfg files, the perf
        ///     test, boot.vcfg and the devonly folder; every other .cfg is the player's, including the
        ///     gamemode_*_server.cfg overrides and server_*.cfg practice files Valve leaves for players to write.
        ///     server.cfg is Valve's stub unless the player edited it, which capture tells with
        ///     <see cref="IsValveServerStub" />.
        /// </summary>
        public static bool IsPlayerCfg(string relative, bool includeGameStateIntegration)
        {
            string path = relative.Replace('\\', '/');
            string name = path.Substring(path.LastIndexOf('/') + 1).ToLowerInvariant();
            if (path.StartsWith("devonly/", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".cfg", StringComparison.Ordinal))
            {
                return false;
            }

            bool valveGameMode = name.StartsWith("gamemode_", StringComparison.Ordinal) && !name.EndsWith("_server.cfg", StringComparison.Ordinal);
            if (valveGameMode || name == "server.cfg"
                || name == "perftest.cfg" || name.EndsWith("_default.cfg", StringComparison.Ordinal))
            {
                return false;
            }

            return includeGameStateIntegration || !name.StartsWith("gamestate_integration_", StringComparison.Ordinal);
        }

        /// <summary>Whether a path in game/csgo/cfg is server.cfg itself, which a backup carries only once the player edited it.</summary>
        public static bool IsServerCfg(string relative)
        {
            return string.Equals((relative ?? "").Replace('\\', '/'), "server.cfg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Whether a backup may write this path of a file part: the same rules capture uses, so neither an older
        ///     backup nor a hand-edited one reaches Steam Cloud's remote/ folder or Valve's own cfg files.
        /// </summary>
        public static bool IsRestorablePath(string section, string relative)
        {
            if (section == Sections.Cs2)
            {
                return IsAccountSetting(relative);
            }

            return section == Sections.Cs2Game && (IsPlayerCfg(relative, true) || IsServerCfg(relative));
        }

        /// <summary>Whether a server.cfg holds nothing but comments and blank lines, as Valve ships it.</summary>
        public static bool IsValveServerStub(string file)
        {
            foreach (string line in File.ReadAllLines(file))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        // -- Capture ---------------------------------------------------------------------------------

        /// <summary>Captures into <paramref name="outDir" />, which must be empty or absent.</summary>
        public static RunReport Capture(CaptureOptions options, string outDir, TextWriter log)
        {
            RunReport report = new RunReport();
            if (Directory.Exists(outDir) && Directory.GetFileSystemEntries(outDir).Length > 0)
            {
                throw new IOException("The capture folder is not empty: " + outDir);
            }

            Directory.CreateDirectory(outDir);
            KvNode manifest = NewManifest("backup", options.Creator);
            SteamInstall steam = SteamInstall.Locate(options.SteamRoot);
            string account = null;
            if (steam != null)
            {
                account = SteamInstall.NormalizeAccountId(options.AccountId);
                if (options.AccountId == null)
                {
                    IList<SteamAccount> accounts = steam.Accounts();
                    account = accounts.Count > 0 ? accounts[0].AccountId : null;
                }
            }

            foreach (string section in Sections.All)
            {
                if (!options.Include.Contains(section))
                {
                    continue;
                }

                options.Cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (section == Sections.Nvidia || section == Sections.Vibrance)
                    {
                        CaptureDriverSection(section, options, steam, account, outDir, manifest, report, log);
                    }
                    else
                    {
                        CaptureSection(section, options, steam, account, outDir, manifest, report, log);
                    }
                }
                catch (Exception ex)
                {
                    // A part that failed partway is left out whole, so the backup never holds half of one.
                    manifest.Remove(section);
                    string staged = Path.Combine(Path.Combine(outDir, "files"), section);
                    if (Directory.Exists(staged))
                    {
                        Directory.Delete(staged, true);
                    }

                    report.Add(log, section, "failed", ex.Message + "; nothing from this part is in the backup");
                }
            }

            manifest.Save(Path.Combine(outDir, ManifestName));
            return report;
        }

        /// <summary>How long a part that calls into the NVIDIA driver may take; longer than NVAPI's own start timeout.</summary>
        public const int DriverPartTimeoutMs = 60000;

        private static readonly object DriverWorkerLock = new object();

        private static Thread _driverWorker;

        /// <summary>
        ///     Captures a part that calls into the NVIDIA driver on a worker of its own, writing into a manifest
        ///     and report of its own that are merged only once it finishes, so a driver call that never returns
        ///     fails that part, and leaves the backup untouched, instead of holding the save.
        /// </summary>
        private static void CaptureDriverSection(string section, CaptureOptions options, SteamInstall steam, string account,
            string outDir, KvNode manifest, RunReport report, TextWriter log)
        {
            KvNode partManifest = new KvNode("backup");
            RunReport partReport = new RunReport();
            Exception error;
            string why = RunOnDriverWorker(delegate
            {
                CaptureSection(section, options, steam, account, outDir, partManifest, partReport, null);
            }, options.Cancellation, DriverPartTimeoutMs, out error);
            if (why != null)
            {
                report.Add(log, section, "failed", why);
                return;
            }

            if (error != null)
            {
                throw new InvalidOperationException(error.Message, error);
            }

            KvNode part = partManifest.Child(section);
            if (part != null)
            {
                manifest.Add(part);
            }

            foreach (SectionResult result in partReport.Results)
            {
                report.Add(log, result.Section, result.Outcome, result.Detail);
            }
        }

        /// <summary>
        ///     Runs <paramref name="body" /> on a background thread and waits for it while watching for a cancel.
        ///     Null once it finished, with what it threw in <paramref name="error" />; otherwise why it was given
        ///     up on, and the thread is left to finish alone. While a thread given up on is still running no
        ///     second one starts, since a driver that hung once is likely to hang again.
        /// </summary>
        public static string RunOnDriverWorker(ThreadStart body, CancellationToken cancellation, int timeoutMs, out Exception error)
        {
            error = null;
            Exception thrown = null;
            Thread worker;
            lock (DriverWorkerLock)
            {
                if (_driverWorker != null && _driverWorker.IsAlive)
                {
                    return "an earlier call into the NVIDIA driver has not returned; restart DemoViewer to try again";
                }

                worker = new Thread(new ThreadStart(delegate
                {
                    try
                    {
                        body();
                    }
                    catch (Exception ex)
                    {
                        thrown = ex;
                    }
                }));
                worker.IsBackground = true;
                worker.Name = "Advanced Config Backup driver call";
                _driverWorker = worker;
                worker.Start();
            }

            int waited = 0;
            while (!worker.Join(100))
            {
                waited += 100;
                if (cancellation.IsCancellationRequested)
                {
                    return "cancelled while waiting for the NVIDIA driver";
                }

                if (waited >= timeoutMs)
                {
                    return "the NVIDIA driver did not answer within " + Inv.S(timeoutMs / 1000) + " seconds";
                }
            }

            error = thrown;
            return null;
        }

        private static void CaptureSection(string section, CaptureOptions options, SteamInstall steam, string account,
            string outDir, KvNode manifest, RunReport report, TextWriter log)
        {
            bool needsSteam = Sections.NeedsSteam(section);
            if (needsSteam && steam == null)
            {
                report.Add(log, section, "skipped", "Steam was not found");
                return;
            }

            if (Sections.IsMachineWide(section) && !Platform.IsWindows)
            {
                report.Add(log, section, "skipped", "only on Windows");
                return;
            }

            switch (section)
            {
                case Sections.Cs2:
                {
                    if (account == null)
                    {
                        report.Add(log, section, "skipped", "no Steam account on this PC has CS2 settings");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    node.Set("account", account);
                    List<string> redacted = new List<string>();
                    int n = CaptureTree(steam.AccountFolder(account), outDir, section, node.Section("files"),
                        delegate(string rel) { return IsAccountSetting(rel); }, redacted);
                    report.Add(log, section, n > 0 ? "done" : "skipped", Inv.S(n) + " files from account " + account
                        + (redacted.Count > 0 ? "; passwords and server tokens left out of " + string.Join(", ", redacted.ToArray()) : ""));
                    return;
                }
                case Sections.Cs2Game:
                {
                    string cfg = steam.Cs2CfgDir();
                    if (cfg == null || !Directory.Exists(cfg))
                    {
                        report.Add(log, section, "skipped", "CS2 is not installed");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    bool gsi = options.IncludeGameStateIntegration;
                    List<string> redacted = new List<string>();
                    int n = CaptureTree(cfg, outDir, section, node.Section("files"),
                        delegate(string rel)
                        {
                            return IsPlayerCfg(rel, gsi) || (IsServerCfg(rel) && !IsValveServerStub(Path.Combine(cfg, rel)));
                        }, redacted);
                    report.Add(log, section, n > 0 ? "done" : "skipped", n > 0
                        ? Inv.S(n) + " cfg files" + (redacted.Count > 0 ? "; passwords and server tokens left out of " + string.Join(", ", redacted.ToArray()) : "")
                        : "no cfg files of your own");
                    return;
                }
                case Sections.Launch:
                {
                    string value = account == null ? null : steam.ReadLaunchOptions(account);
                    if (string.IsNullOrEmpty(value))
                    {
                        report.Add(log, section, "skipped", "none are set");
                        return;
                    }

                    List<string> secrets;
                    value = RemoveSecrets(value, out secrets);
                    KvNode launch = manifest.Section(section);
                    launch.Set("account", account);
                    launch.Set("value", value);
                    report.Add(log, section, "done", value + (secrets.Count > 0 ? " (the value of " + string.Join(", ", secrets.ToArray()) + " left out)" : ""));
                    return;
                }
                case Sections.Input:
                {
                    // Only a hotkey that is off travels: a restore turns the target's off and never turns one on.
                    KvNode node = manifest.Section(section);
                    foreach (KeyValuePair<string, string> pair in WindowsSettings.ReadHotkeys())
                    {
                        if (pair.Value == "0")
                        {
                            node.Set(pair.Key, "0");
                        }
                    }

                    if (node.Children.Count == 0)
                    {
                        manifest.Remove(section);
                        report.Add(log, section, "skipped", "every hotkey is on, as Windows ships it");
                        return;
                    }

                    report.Add(log, section, "done", Inv.S(node.Children.Count) + " hotkeys off");
                    return;
                }
                case Sections.GameBar:
                case Sections.Cs2Exe:
                {
                    string exe = section == Sections.Cs2Exe ? steam.Cs2Exe() : null;
                    if (section == Sections.Cs2Exe && exe == null)
                    {
                        report.Add(log, section, "skipped", "CS2 is not installed");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    int n = CaptureRegistry(section, node, exe);
                    if (node.Child("registry") == null)
                    {
                        manifest.Remove(section);
                        report.Add(log, section, "skipped", "nothing is set");
                        return;
                    }

                    int unset = node.Child("registry").Children.Count - n;
                    report.Add(log, section, "done", Inv.S(n) + " registry values" + (unset > 0 ? ", " + Inv.S(unset) + " at Windows' default" : ""));
                    return;
                }
                case Sections.Power:
                {
                    string[] plan = WindowsSettings.ActivePowerPlan();
                    if (plan == null)
                    {
                        report.Add(log, section, "skipped", "the active power plan could not be read");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    node.Set("guid", plan[0]);
                    node.Set("name", plan[1]);
                    // Only Windows' own templates travel: a plan of the user's own carries this PC's hardware
                    // settings. A copy named like a template (Ultimate Performance made with powercfg
                    // -duplicatescheme) is recorded with that template, which another PC can copy.
                    string template = WindowsSettings.PowerPlanTemplate(plan[0], plan[1], WindowsSettings.PowerPlans());
                    if (template != null && !string.Equals(template, plan[0], StringComparison.OrdinalIgnoreCase))
                    {
                        node.Set("template", template);
                    }

                    report.Add(log, section, "done", template != null
                        ? plan[1]
                        : plan[1] + ", a plan of your own: another PC switches to it only if it has one of that name");
                    return;
                }
                case Sections.Nvidia:
                {
                    string why = NvidiaDrs.Initialize();
                    if (why != null)
                    {
                        report.Add(log, section, "skipped", why);
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    string driver = NvidiaDrs.DriverVersion();
                    if (driver != null)
                    {
                        node.Set("driver", driver);
                    }

                    // What cs2.exe gets for each carried setting, whether set on its profile or the global one;
                    // a setting the driver decides is recorded absent, so a restore resets the target's.
                    int changed = 0;
                    KvNode list = node.Section("cs2").Section("settings");
                    using (NvidiaDrs drs = NvidiaDrs.Open())
                    {
                        IntPtr profile = drs.FindCs2Profile();
                        if (profile == IntPtr.Zero)
                        {
                            profile = drs.GlobalProfile();
                        }

                        foreach (uint id in NvidiaDrs.CarriedSettings)
                        {
                            bool userSet;
                            NvSetting setting = drs.EffectiveSetting(profile, id, out userSet);
                            KvNode item = list.Section("0x" + id.ToString("X8", CultureInfo.InvariantCulture));
                            if (setting == null || !userSet)
                            {
                                item.Set("absent", "1");
                                continue;
                            }

                            item.Set("name", setting.Name);
                            item.Set("type", setting.Type);
                            item.Set("value", setting.Value);
                            changed++;
                        }
                    }

                    report.Add(log, section, "done", Inv.S(changed) + " of " + Inv.S(NvidiaDrs.CarriedSettings.Length) + " carried settings changed from the driver's" + (driver == null ? "" : " (driver " + driver + ")"));
                    return;
                }
                case Sections.Vibrance:
                {
                    IList<int[]> levels = NvidiaDrs.ReadVibrance();
                    if (levels.Count == 0)
                    {
                        report.Add(log, section, "skipped", "no NVIDIA display reports digital vibrance");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    List<string> shown = new List<string>();
                    for (int i = 0; i < levels.Count; i++)
                    {
                        if (levels[i] != null)
                        {
                            node.Set(Inv.S(i), Inv.S(levels[i][0]));
                            shown.Add(Inv.S(levels[i][0]));
                        }
                    }

                    if (shown.Count == 0)
                    {
                        manifest.Remove(section);
                        report.Add(log, section, "skipped", "no NVIDIA display reported its vibrance level");
                        return;
                    }

                    report.Add(log, section, "done", "level " + string.Join(", ", shown.ToArray()));
                    return;
                }
            }
        }

        /// <summary>Records a group's registry values, as <see cref="RegistryEntryForBackup" /> decides. Returns how many values hold something.</summary>
        private static int CaptureRegistry(string group, KvNode node, string cs2Exe)
        {
            int n = 0;
            KvNode list = node.Section("registry");
            foreach (RegistryItem item in WindowsSettings.RegistryItems)
            {
                if (item.Group != group)
                {
                    continue;
                }

                string valueName = item.ValueName == RegistryItem.Cs2ExeToken ? cs2Exe : item.ValueName;
                string kind = null;
                string data = null;
                bool present = valueName != null && WindowsSettings.ReadRegistry(item.SubKey, valueName, out kind, out data);
                // A plain value of another kind than Windows reads (a tweak script's string "0") does not take
                // effect, so it counts as unset; a merged value is recorded as the string a restore merges.
                if (present && !WindowsSettings.IsMergedValue(item.SubKey) && kind != item.Kind)
                {
                    present = false;
                }

                kind = item.Kind;
                if (!RegistryEntryForBackup(item.SubKey, present, ref data))
                {
                    continue;
                }

                present = data != null;
                KvNode entry = list.Section(Inv.S(list.Children.Count));
                entry.Set("key", item.SubKey);
                entry.Set("name", item.ValueName);
                if (present)
                {
                    entry.Set("kind", kind);
                    entry.Set("data", data);
                    n++;
                }
                else
                {
                    entry.Set("absent", "1");
                }
            }

            if (list.Children.Count == 0)
            {
                node.Remove("registry");
            }

            return n;
        }

        /// <summary>
        ///     Whether a backup records a registry value read from this PC, and as what (<paramref name="data" />,
        ///     null for absent). A plain value missing here is recorded absent, so the restore leaves the target at
        ///     Windows' default as this PC is. The Layers value records only the fullscreen-optimizations choice,
        ///     and absent whenever this PC lacks it, value or not, so the restore takes it off the target too. The
        ///     GPU preference is recorded only when it is high performance, since a restore only ever sets that.
        /// </summary>
        public static bool RegistryEntryForBackup(string subKey, bool present, ref string data)
        {
            if (!WindowsSettings.IsMergedValue(subKey))
            {
                if (!present)
                {
                    data = null;
                }

                return true;
            }

            data = present ? WindowsSettings.ValueForBackup(subKey, data) : null;
            return data != null || WindowsSettings.AbsenceRemovesChoice(subKey);
        }

        private delegate bool PathFilter(string relative);

        /// <summary>The commands whose value is a password or a server login token, left out of every backup.</summary>
        public static readonly string[] SecretCommands = { "rcon_password", "sv_password", "sv_setsteamaccount", "tv_password", "tv_relaypassword", "password" };

        private static readonly Regex SecretPattern = new Regex(
            "(^|[\\s;\"])(\\+?)(" + string.Join("|", SecretCommands) + ")([ \\t]+)(\"[^\"\\r\\n]*\"|[^\\s;\"]+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // A saved convar in a .vcfg: "password"		"x" on its own line. CS2 archives the client's password convar,
        // so connecting with one writes it into the convars files.
        private static readonly Regex SecretPairPattern = new Regex(
            "^[ \t]*\"(" + string.Join("|", SecretCommands) + ")\"[ \t]+\"([^\"\r\n]*)\"[^\r\n]*(\r?\n)?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        ///     Text with each of <see cref="SecretCommands" /> removed with its value: as a command in a cfg file,
        ///     inside a bind or alias ("connect ip; password x"), or as a +command in launch options. Removed
        ///     rather than blanked, so no quotes are nested in a bind, and a server falls back to its default (no
        ///     rcon). One already set to "" stays. <paramref name="found" /> names the commands removed.
        /// </summary>
        public static string RemoveSecrets(string text, out List<string> found)
        {
            List<string> names = new List<string>();
            string result = SecretPairPattern.Replace(text ?? "", delegate(Match m)
            {
                if (m.Groups[2].Value.Length == 0)
                {
                    return m.Value;
                }

                string name = m.Groups[1].Value.ToLowerInvariant();
                if (!names.Contains(name))
                {
                    names.Add(name);
                }

                return "";
            });
            result = SecretPattern.Replace(result, delegate(Match m)
            {
                if (m.Groups[5].Value == "\"\"")
                {
                    return m.Value;
                }

                string name = m.Groups[3].Value.ToLowerInvariant();
                if (!names.Contains(name))
                {
                    names.Add(name);
                }

                return m.Groups[1].Value;
            });
            found = names;
            return result;
        }

        /// <summary>
        ///     Whether a captured file can hold a secret: every cs2game cfg, and every .vcfg in the cs2 part with
        ///     its twin, where CS2 saves binds (cs2_user_keys*) and archived convars such as password.
        /// </summary>
        public static bool MayHoldSecrets(string section, string relative)
        {
            if (section == Sections.Cs2Game)
            {
                return true;
            }

            string path = (relative ?? "").Replace('\\', '/');
            string name = path.Substring(path.LastIndexOf('/') + 1);
            // Every .vcfg and its twin: the key files hold binds, the convars files hold archived convars.
            return section == Sections.Cs2
                   && (name.EndsWith(".vcfg", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".vcfg_lastclouded", StringComparison.OrdinalIgnoreCase));
        }

        private static int CaptureTree(string sourceRoot, string outDir, string section, KvNode files, PathFilter keep, List<string> redacted)
        {
            if (!Directory.Exists(sourceRoot))
            {
                return 0;
            }

            int n = 0;
            List<string> all = new List<string>(Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories));
            all.Sort(StringComparer.Ordinal);
            foreach (string file in all)
            {
                string relative = file.Substring(sourceRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                FileInfo info = new FileInfo(file);
                if (!keep(relative) || info.Length > MaxFileBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                string target = Path.Combine(outDir, "files", section, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                // Read, filtered and written once as a new file: an unfiltered copy never reaches the disk, and the
                // source's attributes (a read-only cfg) never reach the staged copy.
                byte[] data = File.ReadAllBytes(file);
                if (redacted != null && MayHoldSecrets(section, relative))
                {
                    // Latin-1 maps every byte to one char and back, so the rest of the file stays byte for byte.
                    Encoding bytes = Encoding.GetEncoding(28591);
                    List<string> secrets;
                    string text = RemoveSecrets(bytes.GetString(data), out secrets);
                    if (secrets.Count > 0)
                    {
                        data = bytes.GetBytes(text);
                        redacted.Add(relative);
                    }
                }

                using (FileStream stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.Write(data, 0, data.Length);
                }

                files.Set(relative, Sha256(target));
                n++;
            }

            return n;
        }

        // -- Restore ---------------------------------------------------------------------------------

        /// <summary>Reads backup.vdf from a backup or undo folder, checking its format.</summary>
        public static KvNode ReadManifest(string backupDir)
        {
            string path = Path.Combine(backupDir, ManifestName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("This folder is not an Advanced Config Backup: it has no " + ManifestName + ".", path);
            }

            return ParseManifest(File.ReadAllText(path, Encoding.UTF8), ManifestName);
        }

        /// <summary>Parses a capture or undo record, checking its format; <paramref name="name" /> is what errors call it.</summary>
        public static KvNode ParseManifest(string text, string name)
        {
            KvNode manifest = KvNode.Parse(text);
            if (manifest.Key != RootKey)
            {
                throw new FormatException(name + " is not an Advanced Config Backup record.");
            }

            string format = manifest.Get("format");
            if (format != FormatVersion)
            {
                throw new FormatException("This record is format " + (format ?? "?") + "; this version reads format " + FormatVersion + ".");
            }

            return manifest;
        }

        /// <summary>One line per section the backup holds.</summary>
        public static IList<string> Describe(string backupDir)
        {
            KvNode manifest = ReadManifest(backupDir);
            List<string> lines = new List<string>();
            foreach (string section in Sections.All)
            {
                KvNode node = manifest.Child(section);
                if (node == null)
                {
                    continue;
                }

                string detail;
                switch (section)
                {
                    case Sections.Cs2:
                    case Sections.Cs2Game:
                        KvNode files = node.Child("files");
                        detail = Inv.S(files == null ? 0 : files.Children.Count) + " files";
                        break;
                    case Sections.Launch:
                        detail = node.Get("absent") == "1" ? "none" : node.Get("value");
                        break;
                    case Sections.Power:
                        detail = node.Get("name") ?? node.Get("guid");
                        break;
                    case Sections.Nvidia:
                        detail = DescribeNvidia(node.Path("cs2", "settings"));
                        break;
                    default:
                        detail = Inv.S(node.Children.Count) + " values";
                        break;
                }

                lines.Add(Sections.Label(section) + ": " + detail);
            }

            return lines;
        }

        /// <summary>How many NVIDIA settings a backup sets, apart from the ones it records at the driver's default.</summary>
        public static string DescribeNvidia(KvNode settings)
        {
            int set = 0;
            int unset = 0;
            foreach (KvNode item in settings == null ? new KvNode[0] : (IEnumerable<KvNode>)settings.Children)
            {
                if (item.Get("absent") == "1")
                {
                    unset++;
                }
                else
                {
                    set++;
                }
            }

            return Inv.S(set) + " settings" + (unset > 0 ? ", " + Inv.S(unset) + " at the driver's default" : "");
        }

        /// <summary>
        ///     The account a restore writes CS2 settings and launch options to: the one asked for, once it has
        ///     signed in to Steam on this PC, else the backup's own account once it has (Steam makes its userdata
        ///     folder then). Never another account by guess, since on a shared PC the one that signed in last is
        ///     usually someone else. Null when neither has a folder here; the caller asks or skips.
        /// </summary>
        public static string ResolveAccount(SteamInstall steam, string requested, string backupAccount)
        {
            string why;
            return ResolveAccount(steam, requested, backupAccount, out why);
        }

        /// <summary>As <see cref="ResolveAccount(SteamInstall, string, string)" />, saying why when it finds none.</summary>
        public static string ResolveAccount(SteamInstall steam, string requested, string backupAccount, out string why)
        {
            why = null;
            if (!string.IsNullOrEmpty(requested))
            {
                string id = SteamInstall.NormalizeAccountId(requested);
                if (id == null)
                {
                    why = "'" + requested + "' is not a Steam account id; use the number of your folder under Steam\\userdata";
                    return null;
                }

                if (!Directory.Exists(Path.Combine(steam.UserData, id)))
                {
                    why = "account " + id + " has not signed in to Steam on this PC (there is no Steam\\userdata\\" + id + ")";
                    return null;
                }

                return id;
            }

            if (IsAccountId(backupAccount) && Directory.Exists(Path.Combine(steam.UserData, backupAccount)))
            {
                return backupAccount;
            }

            why = NoAccountReason(backupAccount);
            return null;
        }

        /// <summary>Whether a manifest's account is exactly a userdata folder name: digits, a nonzero 32-bit number.</summary>
        private static bool IsAccountId(string account)
        {
            return !string.IsNullOrEmpty(account) && SteamInstall.NormalizeAccountId(account) == account;
        }

        /// <summary>The account a backup or undo snapshot was made for: the cs2 part's, else the launch part's.</summary>
        public static string BackupAccount(KvNode manifest)
        {
            foreach (string section in new string[] { Sections.Cs2, Sections.Launch })
            {
                KvNode node = manifest.Child(section);
                string account = node == null ? null : node.Get("account");
                if (IsAccountId(account))
                {
                    return account;
                }
            }

            return null;
        }

        private static string NoAccountReason(string backupAccount)
        {
            return !IsAccountId(backupAccount)
                ? "the backup names no Steam account; name yours with --account"
                : "your Steam account (" + backupAccount + ") has not signed in to Steam on this PC. Sign in, start CS2 once, close it, and restore again; or name an account with --account";
        }

        private static string Where(string account, string backupAccount)
        {
            return "account " + account + (string.IsNullOrEmpty(backupAccount) || backupAccount == account
                ? ""
                : " (NOT the backup's account " + backupAccount + ")");
        }

        /// <summary>
        ///     Why a per-user part must not run now, or null. It writes the profile of the user the process runs
        ///     as, so not while that is another user than the one signed in (an administrator's password typed at
        ///     a UAC prompt), and an undo only as the user its snapshot was recorded for.
        /// </summary>
        public static string OtherUserReason(string runningAs, string signedIn, string recordedSid, string currentSid)
        {
            if (!string.IsNullOrEmpty(runningAs) && !string.IsNullOrEmpty(signedIn)
                && !string.Equals(runningAs, signedIn, StringComparison.OrdinalIgnoreCase))
            {
                return "this runs as " + runningAs + " but " + signedIn + " is signed in, and these settings belong to the signed-in user. Run acb as yourself, not with Run as administrator";
            }

            if (!string.IsNullOrEmpty(recordedSid) && !string.IsNullOrEmpty(currentSid)
                && !string.Equals(recordedSid, currentSid, StringComparison.OrdinalIgnoreCase))
            {
                return "the restore changed another Windows user's settings (" + recordedSid + "); sign in as that user and run acb undo";
            }

            return null;
        }

        /// <summary>
        ///     The undo snapshot a restore writes as it goes: saved before each change, so a restore that dies
        ///     partway leaves a record of everything it touched. Its folder is claimed on first use, so a restore
        ///     that changes nothing leaves none.
        /// </summary>
        private sealed class UndoLog
        {
            private readonly string _root;
            private string _dir;

            public UndoLog(string root, KvNode manifest)
            {
                _root = root;
                Manifest = manifest;
            }

            public string Dir
            {
                get
                {
                    if (_dir == null)
                    {
                        string sequence;
                        _dir = ClaimUndoSnapshot(_root, Manifest, out sequence);
                        Manifest.Set("sequence", sequence);
                    }

                    return _dir;
                }
            }

            public KvNode Manifest { get; private set; }

            public bool Saved { get; private set; }

            public static void Save(UndoLog log)
            {
                if (log != null)
                {
                    // A record whose flush failed throws here, before the change it guards is made.
                    SafeFile.WriteRecord(Path.Combine(log.Dir, ManifestName), new UTF8Encoding(false).GetBytes(log.Manifest.ToText()));
                    log.Saved = true;
                }
            }
        }

        private static readonly Regex UndoSnapshotPattern = new Regex("^([0-9]{6})-[0-9]{8}-[0-9]{6}Z$");

        /// <summary>
        ///     Whether a folder name is an undo snapshot's: a six-digit sequence number, then the UTC time it was
        ///     made, which is for people to read and orders nothing.
        /// </summary>
        public static bool IsUndoSnapshotName(string name)
        {
            return name != null && UndoSnapshotPattern.IsMatch(name);
        }

        /// <summary>
        ///     The snapshot names among <paramref name="names" />, newest first by sequence number. The sequence
        ///     is one more than every snapshot before it, so the order holds when the clock is wrong or goes back.
        /// </summary>
        public static string[] NewestFirst(string[] names)
        {
            List<string> snapshots = new List<string>();
            foreach (string name in names ?? new string[0])
            {
                if (IsUndoSnapshotName(name) && !snapshots.Contains(name))
                {
                    snapshots.Add(name);
                }
            }

            snapshots.Sort(delegate(string x, string y) { return string.CompareOrdinal(y, x); });
            return snapshots.ToArray();
        }

        /// <summary>
        ///     Creates the next undo snapshot under <paramref name="undoRoot" />: numbered one past every snapshot
        ///     there, in applied\ and in set-aside\, and claimed by creating its backup.vdf exclusively. A claim that then finds
        ///     another folder with its number gives the number up, so two restores never share one.
        /// </summary>
        private static string ClaimUndoSnapshot(string undoRoot, KvNode manifest, out string sequence)
        {
            int next = 1;
            foreach (string dir in new string[] { undoRoot, Path.Combine(undoRoot, "applied"), Path.Combine(undoRoot, "set-aside") })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string sub in Directory.GetDirectories(dir))
                {
                    Match m = UndoSnapshotPattern.Match(Path.GetFileName(sub));
                    if (m.Success)
                    {
                        next = Math.Max(next, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1);
                    }
                }
            }

            for (int attempt = 0; attempt < 100; attempt++, next++)
            {
                if (next > 999999)
                {
                    break;
                }

                sequence = next.ToString("D6", CultureInfo.InvariantCulture);
                string path = Path.Combine(undoRoot, sequence + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "Z");
                if (OtherSnapshotWithSequence(undoRoot, sequence, null) != null)
                {
                    continue;
                }

                // Only a claim this attempt made is ever removed: a folder or record another restore holds stays.
                bool madeFolder = false;
                bool madeRecord = false;
                try
                {
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                        madeFolder = true;
                    }

                    byte[] text = new UTF8Encoding(false).GetBytes(manifest.ToText());
                    // Flushed like every other record: a claim whose backup.vdf came back zero-filled after a power
                    // loss could not be read, and would hold back every older snapshot.
                    using (FileStream stream = new FileStream(Path.Combine(path, ManifestName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    {
                        madeRecord = true;
                        stream.Write(text, 0, text.Length);
                        stream.Flush(true);
                    }

                    // Each claim creates its folder before it looks, so of two that raced, at least the later one
                    // sees the other here and moves on.
                    if (OtherSnapshotWithSequence(undoRoot, sequence, Path.GetFileName(path)) != null)
                    {
                        Directory.Delete(path, true);
                        continue;
                    }

                    return path;
                }
                catch (IOException ex)
                {
                    // Another restore created this record first: its claim, not a failure, so take the next number.
                    if (!madeRecord && File.Exists(Path.Combine(path, ManifestName)))
                    {
                        continue;
                    }

                    // A full disk or a store that refuses the write fails the same way on every number, so v1's
                    // retry left a hundred half-made folders. The claim is removed and the restore stops here.
                    RemoveClaim(path, madeFolder, madeRecord);
                    throw new UndoClaimException("the undo record could not be written under " + undoRoot + " (" + ex.Message + "), so nothing was changed", ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    RemoveClaim(path, madeFolder, madeRecord);
                    throw new UndoClaimException("the undo record could not be written under " + undoRoot + " (" + ex.Message + "), so nothing was changed", ex);
                }
            }

            throw new UndoClaimException("no free undo record number under " + undoRoot + ", so nothing was changed", null);
        }

        /// <summary>Best effort: a half-made claim this attempt created is deleted, and a cleanup failure never hides why it stopped.</summary>
        private static void RemoveClaim(string path, bool madeFolder, bool madeRecord)
        {
            try
            {
                if (madeRecord)
                {
                    SafeFile.Delete(Path.Combine(path, ManifestName));
                }

                if (madeFolder && Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        ///     The name of a snapshot folder under <paramref name="undoRoot" /> or its applied\ that carries
        ///     <paramref name="sequence" />, other than <paramref name="except" />; null when there is none.
        /// </summary>
        public static string OtherSnapshotWithSequence(string undoRoot, string sequence, string except)
        {
            foreach (string dir in new string[] { undoRoot, Path.Combine(undoRoot, "applied"), Path.Combine(undoRoot, "set-aside") })
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (string sub in Directory.GetDirectories(dir, sequence + "-*"))
                {
                    string name = Path.GetFileName(sub);
                    if (name != except && IsUndoSnapshotName(name))
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        /// <summary>
        ///     Applies <paramref name="source" /> section by section. <paramref name="mode" /> comes from the command
        ///     that was run; whatever the record says about itself decides nothing. A restore records what it
        ///     replaces under <see cref="RestoreOptions.UndoRoot" /> before each change, and a part that fails
        ///     partway is put back from that record at once. If that rollback fails too, the run stops there.
        /// </summary>
        public static RunReport Restore(IBackupSource source, RestoreMode mode, RestoreOptions options, TextWriter log)
        {
            KvNode manifest = source.Manifest;
            RunReport report = new RunReport();
            SteamInstall steam = SteamInstall.Locate(options.SteamRoot);
            bool isUndo = mode == RestoreMode.Undo;
            string userSid = Platform.IsWindows ? WindowsSettings.CurrentUserSid() : null;
            UndoLog undo = null;
            if (options.WriteUndo && !options.DryRun && !isUndo)
            {
                if (string.IsNullOrEmpty(options.UndoRoot))
                {
                    throw new ArgumentException("A restore that records undo needs the restorer's undo store; a record is never kept beside the backup.");
                }

                undo = new UndoLog(options.UndoRoot, NewManifest("undo", "Advanced Config Backup restore of " + (manifest.Get("created") ?? "a backup")));
                if (userSid != null)
                {
                    undo.Manifest.Set("user", userSid);
                }

                if (options.RecordNotes != null)
                {
                    foreach (KeyValuePair<string, string> note in options.RecordNotes)
                    {
                        undo.Manifest.Set(note.Key, note.Value);
                    }
                }
            }

            List<string> wouldSettle = new List<string>();
            foreach (string section in Sections.All)
            {
                KvNode node = manifest.Child(section);
                if (node == null || (options.Only != null && !options.Only.Contains(section)))
                {
                    continue;
                }

                int first = report.Results.Count;
                try
                {
                    RestoreSection(section, node, manifest, source, isUndo, options, steam, undo, userSid, report, log);
                }
                catch (NvAdminRequiredException)
                {
                    report.Add(log, section, "admin", "needs an administrator");
                }
                catch (UndoClaimException ex)
                {
                    report.Add(log, section, "failed", ex.Message);
                    report.ClaimProblem = ex.Message;
                    break;
                }
                catch (TargetBlockedException ex)
                {
                    report.Add(log, section, "blocked", ex.Message);
                }
                catch (Exception ex)
                {
                    report.Add(log, section, "failed", ex.Message);
                }

                if (!isUndo && undo != null && Failed(report, first) && undo.Manifest.Child(section) != null)
                {
                    string problem = RollBack(section, undo, options, steam, userSid, log);
                    if (problem != null)
                    {
                        report.RollbackProblem = Sections.Label(section) + ": " + problem;
                        break;
                    }

                    report.RolledBack.Add(section);
                }

                if (isUndo && options.DryRun && Settled(report, first))
                {
                    wouldSettle.Add(section);
                }

                // An undo forgets each part once it is put back, so a rerun applies only what is left.
                if (isUndo && !options.DryRun && Settled(report, first))
                {
                    manifest.Remove(section);
                    try
                    {
                        source.SaveManifest();
                    }
                    catch (Exception ex)
                    {
                        manifest.Add(node);
                        report.Add(log, section, "failed", "put back, but the record could not note it: " + ex.Message);
                    }
                }
            }

            if (isUndo)
            {
                // A dry run forgets nothing, so it reports what a real run would leave.
                foreach (string part in PartsLeft(manifest))
                {
                    if (!wouldSettle.Contains(part))
                    {
                        report.PendingParts.Add(part);
                    }
                }
            }

            if (undo != null && undo.Saved)
            {
                report.UndoDir = undo.Dir;
            }

            return report;
        }

        private static bool Failed(RunReport report, int first)
        {
            for (int i = first; i < report.Results.Count; i++)
            {
                if (report.Results[i].Outcome == "failed")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     Puts back what a failed part had changed, from the record written before each of those changes, and
        ///     takes the part off the record once it is back. Null when the part is as it was; otherwise why not,
        ///     and the record keeps the part for <c>acb undo</c>.
        /// </summary>
        private static string RollBack(string section, UndoLog undo, RestoreOptions options, SteamInstall steam, string userSid, TextWriter log)
        {
            KvNode recorded = undo.Manifest.Child(section);
            try
            {
                if (options.BeforeRollback != null)
                {
                    options.BeforeRollback(undo.Dir);
                }

                // The part was written a moment ago, so the processes it waits for are not checked again.
                RestoreOptions back = new RestoreOptions
                {
                    SteamRoot = options.SteamRoot,
                    AccountId = options.AccountId,
                    WriteUndo = false,
                    SkipProcessChecks = true
                };
                RunReport result = new RunReport();
                RestoreSection(section, recorded, undo.Manifest, new FolderSource(undo.Dir, undo.Manifest), true, back, steam, null, userSid, result, log);
                if (!Settled(result, 0))
                {
                    return "it could not be put back (" + string.Join("; ", result.Results.ConvertAll(delegate(SectionResult r) { return r.ToString(); }).ToArray()) + ")";
                }

                undo.Manifest.Remove(section);
                UndoLog.Save(undo);
                return null;
            }
            catch (Exception ex)
            {
                if (undo.Manifest.Child(section) == null)
                {
                    undo.Manifest.Add(recorded);
                }

                return "it could not be put back (" + ex.Message + ")";
            }
        }

        /// <summary>The parts a manifest still holds; for an undo snapshot, the parts not put back yet.</summary>
        public static string[] PartsLeft(KvNode manifest)
        {
            List<string> parts = new List<string>();
            foreach (string section in Sections.All)
            {
                if (manifest.Child(section) != null)
                {
                    parts.Add(section);
                }
            }

            return parts.ToArray();
        }

        /// <summary>
        ///     Whether the results since <paramref name="first" /> say the part is finished: at least one, and each
        ///     done or skipped. A part that reported nothing is not finished, so its record is kept.
        /// </summary>
        private static bool Settled(RunReport report, int first)
        {
            if (report.Results.Count <= first)
            {
                return false;
            }

            for (int i = first; i < report.Results.Count; i++)
            {
                string outcome = report.Results[i].Outcome;
                if (outcome != "done" && outcome != "skipped")
                {
                    return false;
                }
            }

            return true;
        }

        private static void RestoreSection(string section, KvNode node, KvNode manifest, IBackupSource backup, bool isUndo, RestoreOptions options,
            SteamInstall steam, UndoLog undoLog, string userSid, RunReport report, TextWriter log)
        {
            bool dry = options.DryRun;
            KvNode undo = undoLog == null ? null : undoLog.Manifest;
            bool needsSteam = Sections.NeedsSteam(section);
            if (needsSteam && steam == null)
            {
                report.Add(log, section, "deferred", "Steam was not found on this PC");
                return;
            }

            if (Sections.IsMachineWide(section) && !Platform.IsWindows)
            {
                report.Add(log, section, "skipped", "only on Windows");
                return;
            }

            string backupAccount = BackupAccount(manifest);
            if (Sections.IsPerUser(section))
            {
                string other = OtherUserReason(WindowsSettings.CurrentUserName(), WindowsSettings.SessionUserName(),
                    isUndo ? manifest.Get("user") : null, userSid);
                if (other != null)
                {
                    report.Add(log, section, "deferred", other);
                    return;
                }
            }

            switch (section)
            {
                case Sections.Cs2:
                case Sections.Cs2Game:
                {
                    if (options.IsRunning("cs2"))
                    {
                        report.Add(log, section, "deferred", "CS2 is running; it rewrites its settings when it closes. Close it and run this again");
                        return;
                    }

                    string targetRoot;
                    string where;
                    string account = null;
                    if (section == Sections.Cs2)
                    {
                        string why;
                        account = ResolveAccount(steam, options.AccountId, backupAccount, out why);
                        if (account == null)
                        {
                            // Left for later on a restore (the account signs in, then restore again); on an undo the
                            // account's folder is gone, so nothing of it is left to put back.
                            report.Add(log, section, isUndo ? "skipped" : "deferred", why);
                            return;
                        }

                        targetRoot = steam.AccountFolder(account);
                        where = Where(account, backupAccount);
                    }
                    else
                    {
                        targetRoot = steam.Cs2CfgDir();
                        if (targetRoot == null)
                        {
                            report.Add(log, section, "deferred", "CS2 is not installed on this PC");
                            return;
                        }

                        where = "the CS2 cfg folder";
                    }

                    // Every copy is checked, and every merged file built, before the first write, so a damaged
                    // copy leaves the part as the target had it, and a .vcfg never lands without its twin.
                    KvNode files = node.Child("files");
                    List<string> relatives = new List<string>();
                    List<byte[]> sources = new List<byte[]>();
                    List<KvNode> merged = new List<KvNode>();
                    List<string> outside = new List<string>();
                    foreach (KvNode file in files == null ? new KvNode[0] : (IEnumerable<KvNode>)files.Children)
                    {
                        string relative = SafeRelative(file.Key);
                        // An undo snapshot puts back whatever its restore changed; a backup writes only what capture would carry.
                        if (!isUndo && !IsRestorablePath(section, relative))
                        {
                            outside.Add(relative);
                            continue;
                        }

                        byte[] source = null;
                        KvNode mergedFile = null;
                        if (file.Value != "absent")
                        {
                            // Read once: the bytes checked here are the bytes written, so nothing can be swapped in between.
                            source = backup.ReadFile(section, relative);
                            if (source == null || Sha256(source) != file.Value)
                            {
                                throw new IOException("the backup's copy of " + relative + " is missing or damaged, so nothing in this part was written");
                            }

                            string existing = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                            // An undo leaves a file that already matches its record alone, so nothing unchanged holds it up.
                            if (isUndo && File.Exists(existing) && Sha256(existing) == file.Value)
                            {
                                continue;
                            }
                        }

                        // A target another user owns, an ACL denies or a program holds open stops the part here, before any write.
                        string writable = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                        try
                        {
                            SafeFile.CheckWritable(writable, dry);
                            report.Observed.Add(section + " " + writable + " " + (File.Exists(writable) ? Sha256(writable) : "absent"));
                        }
                        catch (IOException ex)
                        {
                            throw new TargetBlockedException(relative + " cannot be written (" + ex.Message + "), so nothing in this part was written", ex);
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            throw new TargetBlockedException(relative + " cannot be written (" + ex.Message + "), so nothing in this part was written", ex);
                        }

                        // Merged only once the target is known to be writable, so one that cannot even be read is
                        // reported as blocked like any other, not as a failure.
                        string[] hardwareKeys = source == null || isUndo ? null : HardwareKeys(relative);
                        if (hardwareKeys != null)
                        {
                            mergedFile = MergeKeepingHardware(source, writable, hardwareKeys);
                        }

                        relatives.Add(relative);
                        sources.Add(source);
                        merged.Add(mergedFile);
                    }

                    KvNode undoFiles = null;
                    if (undo != null)
                    {
                        undoFiles = undo.Section(section).Section("files");
                        if (section == Sections.Cs2)
                        {
                            undo.Section(section).Set("account", account);
                        }
                    }

                    int written = 0;
                    int removed = 0;
                    for (int i = 0; i < relatives.Count; i++)
                    {
                        string target = Path.Combine(targetRoot, relatives[i].Replace('/', Path.DirectorySeparatorChar));
                        if (undoFiles != null)
                        {
                            RecordFile(target, undoLog.Dir, section, relatives[i], undoFiles);
                            UndoLog.Save(undoLog);
                        }

                        if (sources[i] == null)
                        {
                            if (!dry)
                            {
                                SafeFile.Delete(target);
                            }

                            removed++;
                            continue;
                        }

                        if (!dry)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            if (merged[i] != null)
                            {
                                merged[i].Save(target);
                            }
                            else
                            {
                                SafeFile.Write(target, sources[i]);
                            }
                        }

                        written++;
                    }

                    report.Add(log, section, "done", (dry ? "would write " : "wrote ") + Inv.S(written) + " files to " + where
                                                     + (removed > 0 ? ", removed " + Inv.S(removed) : "")
                                                     + (outside.Count > 0 ? "; left alone, as files this tool does not restore: " + string.Join(", ", outside.ToArray()) : ""));
                    return;
                }
                case Sections.Launch:
                {
                    if (options.IsRunning("steam"))
                    {
                        report.Add(log, section, "deferred", "Steam is running; it rewrites launch options when it closes. Close Steam and run this again");
                        return;
                    }

                    string why;
                    string account = ResolveAccount(steam, options.AccountId, backupAccount, out why);
                    if (account == null)
                    {
                        report.Add(log, section, isUndo ? "skipped" : "deferred", why);
                        return;
                    }

                    string value = node.Get("absent") == "1" ? null : node.Get("value");
                    string localConfig = steam.LocalConfigPath(account);
                    report.Observed.Add(section + " " + localConfig + " " + (File.Exists(localConfig) ? Sha256(localConfig) : "absent"));
                    if (undo != null)
                    {
                        string previous = steam.ReadLaunchOptions(account);
                        KvNode u = undo.Section(section);
                        u.Set("account", account);
                        if (previous == null)
                        {
                            u.Set("absent", "1");
                        }
                        else
                        {
                            u.Set("value", previous);
                        }

                        UndoLog.Save(undoLog);
                    }

                    if (!dry)
                    {
                        // The safety copy of Steam's file goes into the undo snapshot, not beside Steam's file.
                        steam.WriteLaunchOptions(account, value, undoLog == null ? null : Path.Combine(undoLog.Dir, "files", section, "localconfig.vdf"));
                    }

                    report.Add(log, section, "done", (value ?? "cleared") + " for " + Where(account, backupAccount));
                    return;
                }
                case Sections.Input:
                {
                    // A backup only turns hotkeys off; its undo puts each back as the target had it.
                    IDictionary<string, string> current = WindowsSettings.ReadHotkeys();
                    KvNode u = undo == null ? null : undo.Section(section);
                    int n = 0;
                    foreach (KvNode value in node.Children)
                    {
                        if (value.IsSection || Array.IndexOf(WindowsSettings.HotkeyFeatures, value.Key) < 0)
                        {
                            continue;
                        }

                        bool active = value.Value == "1";
                        string previous;
                        if ((active && !isUndo) || !current.TryGetValue(value.Key, out previous) || previous == value.Value)
                        {
                            continue;
                        }

                        if (u != null)
                        {
                            u.Set(value.Key, previous);
                            UndoLog.Save(undoLog);
                        }

                        if (!dry)
                        {
                            WindowsSettings.SetHotkey(value.Key, active);
                        }

                        n++;
                    }

                    report.Add(log, section, n > 0 ? "done" : "skipped", n > 0 ? Inv.S(n) + " hotkeys " + (isUndo ? "put back" : "turned off") : "already as wanted");
                    return;
                }
                case Sections.GameBar:
                case Sections.Cs2Exe:
                {
                    string exe = null;
                    if (section == Sections.Cs2Exe)
                    {
                        exe = steam.Cs2Exe();
                        if (exe == null)
                        {
                            report.Add(log, section, "deferred", "CS2 is not installed on this PC");
                            return;
                        }
                    }

                    int n = RestoreRegistry(node, exe, undoLog, section, dry, isUndo);
                    report.Add(log, section, "done", Inv.S(n) + " registry values");
                    return;
                }
                case Sections.Power:
                {
                    string guid = node.Get("guid");
                    string name = node.Get("name");
                    string template = isUndo ? null : node.Get("template");
                    string shown = name ?? guid;
                    string[] current = WindowsSettings.ActivePowerPlan();
                    string useGuid;
                    string action = WindowsSettings.PowerPlanAction(guid, name, template, WindowsSettings.PowerPlans(), current == null ? null : current[0], out useGuid);
                    string problem = null;
                    string created = null;
                    if (action == "missing")
                    {
                        report.Add(log, section, "skipped", "this PC has no plan named " + shown);
                    }
                    else if (action == "already")
                    {
                        report.Add(log, section, "skipped", "already " + shown);
                    }
                    else if (dry)
                    {
                        report.Add(log, section, "done", (action == "copy" ? "would add Windows' " : "would switch to ") + shown);
                    }
                    else
                    {
                        // The plan to switch back to is on record before anything changes, so a plan that cannot be
                        // read is not switched away from.
                        if (undo != null && current == null)
                        {
                            report.Add(log, section, "failed", "could not read which power plan is active, so it could not be put back afterwards; left as it is");
                            return;
                        }

                        KvNode u = null;
                        if (undo != null)
                        {
                            u = undo.Section(section);
                            u.Set("guid", current[0]);
                            u.Set("name", current[1]);
                            UndoLog.Save(undoLog);
                        }

                        if (action == "copy")
                        {
                            // A Windows template this PC hides: a copy of it is listed, recorded, and removed by Undo.
                            useGuid = WindowsSettings.DuplicatePowerPlan(useGuid, out problem);
                            created = useGuid;
                            if (u != null && created != null)
                            {
                                u.Set("created", created);
                                UndoLog.Save(undoLog);
                            }
                        }

                        if (problem == null)
                        {
                            problem = WindowsSettings.ActivatePowerPlan(useGuid);
                        }

                        // The record stays when the switch fails: powercfg may have switched before it failed or
                        // was stopped, and switching back to the plan already active is harmless.
                        report.Add(log, section, problem == null ? "done" : "failed", problem ?? (action == "copy" ? "added Windows' " + shown + " and switched to it" : shown));
                    }

                    // Undoing a restore that added a plan: switched back above, so the copy can go.
                    string added = isUndo ? node.Get("created") : null;
                    if (added != null && !dry && (action == "already" || problem == null))
                    {
                        IDictionary<string, string> listed = WindowsSettings.PowerPlans();
                        string[] active = WindowsSettings.ActivePowerPlan();
                        string createdProblem = CreatedPlanProblem(added, active == null ? null : active[0]);
                        if (createdProblem != null)
                        {
                            report.Add(log, section, "skipped", "did not remove " + added + ": " + createdProblem);
                        }
                        else if (listed.ContainsKey(added))
                        {
                            string deleteProblem = WindowsSettings.DeletePowerPlan(added);
                            if (deleteProblem != null)
                            {
                                report.Add(log, section, "failed", "could not remove the plan the restore added: " + deleteProblem);
                            }
                        }
                    }

                    return;
                }
                case Sections.Nvidia:
                {
                    // Every value is checked before the driver is opened, so a damaged backup entry writes nothing.
                    // An undo puts back every entry it can and reports the rest, so one bad record cannot hold the
                    // others on the PC.
                    KvNode toCheck = node.Path("cs2", "settings");
                    Dictionary<string, string> unusable = new Dictionary<string, string>();
                    foreach (KvNode item in toCheck == null ? new KvNode[0] : (IEnumerable<KvNode>)toCheck.Children)
                    {
                        string entryProblem = NvidiaEntryProblem(item);
                        if (entryProblem != null)
                        {
                            if (!isUndo)
                            {
                                throw new FormatException(entryProblem);
                            }

                            unusable[item.Key] = entryProblem;
                        }
                    }

                    string why = NvidiaDrs.Initialize();
                    if (why != null)
                    {
                        report.Add(log, section, "deferred", why);
                        return;
                    }

                    // Every write goes to the CS2 profile, so nothing else on the PC changes, and only for the
                    // settings on NvidiaDrs.CarriedSettings. One the target's driver refuses is skipped, not fatal.
                    // A setting the backup leaves to the driver that this PC's global profile overrides gets the
                    // driver's own value on the CS2 profile, so cs2.exe stops inheriting the override.
                    KvNode settings = node.Path("cs2", "settings");
                    KvNode pending = new KvNode(section);
                    KvNode undoSettings = pending.Section("cs2").Section("settings");
                    int n = 0;
                    List<string> refused = new List<string>();
                    List<string> leftToGlobal = new List<string>();
                    using (NvidiaDrs drs = NvidiaDrs.Open())
                    {
                        IntPtr profile = drs.FindCs2Profile();
                        if (profile == IntPtr.Zero)
                        {
                            report.Add(log, section, "skipped", "this NVIDIA driver has no CS2 profile; update the driver and restore again");
                            return;
                        }

                        foreach (KvNode item in settings == null ? new KvNode[0] : (IEnumerable<KvNode>)settings.Children)
                        {
                            uint id;
                            if (!item.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                || !uint.TryParse(item.Key.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
                                || !NvidiaDrs.IsCarriedSetting(id))
                            {
                                refused.Add(item.Key + " (not a setting this tool carries)");
                                continue;
                            }

                            if (!isUndo && options.NvidiaDeny != null && options.NvidiaDeny.Contains(id))
                            {
                                refused.Add(item.Key + " (the organiser's policy leaves it)");
                                continue;
                            }

                            if (unusable.ContainsKey(item.Key))
                            {
                                refused.Add(item.Key + " (" + unusable[item.Key] + ")");
                                continue;
                            }

                            bool absent = item.Get("absent") == "1";
                            NvSetting previous = drs.UserSetting(profile, id);
                            // A value the undo could not write back is never replaced, so every undo entry is one it accepts.
                            if (!isUndo && previous != null && !CanRecordNvidia(item.Key, previous))
                            {
                                refused.Add(item.Key + " (this PC's driver holds it as " + (previous.Type ?? "no type") + ", which could not be put back)");
                                continue;
                            }

                            NvSetting driverValue = null;
                            if (absent && !isUndo && previous == null && drs.InheritsUserGlobalValue(profile, id))
                            {
                                driverValue = drs.PredefinedSetting(drs.GlobalProfile(), id);
                                if (driverValue == null)
                                {
                                    leftToGlobal.Add(item.Key);
                                    continue;
                                }
                            }

                            if (absent && previous == null && driverValue == null)
                            {
                                continue;
                            }

                            bool reset = false;
                            if (!dry)
                            {
                                try
                                {
                                    if (absent && previous != null)
                                    {
                                        drs.ResetSetting(profile, id);
                                        reset = true;
                                        // With its own value gone, the CS2 profile may fall through to the global override.
                                        if (!isUndo && drs.InheritsUserGlobalValue(profile, id))
                                        {
                                            driverValue = drs.PredefinedSetting(drs.GlobalProfile(), id);
                                            if (driverValue == null)
                                            {
                                                leftToGlobal.Add(item.Key);
                                            }
                                        }
                                    }

                                    if (driverValue != null)
                                    {
                                        drs.SetSetting(profile, driverValue);
                                    }
                                    else if (!absent)
                                    {
                                        drs.SetSetting(profile, new NvSetting(id, item.Get("name") ?? "", item.Get("type"), item.Get("value")));
                                    }
                                }
                                catch (InvalidOperationException ex)
                                {
                                    // A reset the driver took is saved with the rest, so it still goes into the undo snapshot.
                                    refused.Add(item.Key + " (" + ex.Message + ")");
                                    if (!reset)
                                    {
                                        continue;
                                    }
                                }
                                catch (FormatException ex)
                                {
                                    refused.Add(item.Key + " (" + ex.Message + ")");
                                    if (!reset)
                                    {
                                        continue;
                                    }
                                }
                            }

                            KvNode u = undoSettings.Section(item.Key);
                            if (previous == null)
                            {
                                u.Set("absent", "1");
                            }
                            else
                            {
                                u.Set("name", previous.Name);
                                u.Set("type", previous.Type);
                                u.Set("value", previous.Value);
                            }

                            n++;
                        }

                        if (!dry && n > 0)
                        {
                            // On record before the driver saves; taken off again only when the driver refuses the save
                            // for want of rights, which changes nothing. After any other failure the driver may have
                            // kept some of the session, and undoing a value that did not change is harmless.
                            if (undo != null)
                            {
                                undo.Add(pending);
                                UndoLog.Save(undoLog);
                            }

                            try
                            {
                                drs.Save();
                            }
                            catch (NvAdminRequiredException)
                            {
                                if (undo != null)
                                {
                                    undo.Remove(section);
                                    UndoLog.Save(undoLog);
                                }

                                throw;
                            }
                        }
                    }

                    report.Add(log, section, "done", (dry ? "would change " : "changed ") + Inv.S(n) + " settings on the CS2 profile"
                                                     + (refused.Count > 0 ? "; skipped " + string.Join(", ", refused.ToArray()) : "")
                                                     + (leftToGlobal.Count > 0 ? "; left as this PC's global profile sets them, since the driver reports no default: " + string.Join(", ", leftToGlobal.ToArray()) : ""));
                    return;
                }
                case Sections.Vibrance:
                {
                    // Displays are matched by NVAPI's order, which says nothing about which monitor is which; so a
                    // backup is applied only where that cannot go wrong: one display here, one level in the backup.
                    IList<int[]> current = NvidiaDrs.ReadVibrance();
                    KvNode u = undo == null ? null : undo.Section(section);
                    List<int> wanted = new List<int>();
                    List<int> indexes = new List<int>();
                    foreach (KvNode item in node.Children)
                    {
                        int index = Inv.I(item.Key, -1);
                        int level = Inv.I(item.Value, int.MinValue);
                        if (index >= 0 && level != int.MinValue)
                        {
                            indexes.Add(index);
                            wanted.Add(level);
                        }
                    }

                    if (!isUndo)
                    {
                        int shown = 0;
                        foreach (int[] display in current)
                        {
                            shown += display == null ? 0 : 1;
                        }

                        bool oneLevel = wanted.Count > 0 && wanted.TrueForAll(delegate(int level) { return level == wanted[0]; });
                        if (shown != 1 || current[0] == null || !oneLevel)
                        {
                            report.Add(log, section, "skipped", shown != 1 || current[0] == null
                                ? "this PC has " + Inv.S(shown) + " NVIDIA displays; set vibrance in NVIDIA Control Panel"
                                : "the backup holds different levels for several displays; set vibrance in NVIDIA Control Panel");
                            return;
                        }

                        indexes = new List<int> { 0 };
                        wanted = new List<int> { wanted[0] };
                    }

                    // A display missing during an undo may only be off or asleep, so its record is kept for a rerun.
                    int n = 0;
                    int missing = 0;
                    for (int i = 0; i < indexes.Count; i++)
                    {
                        int index = indexes[i];
                        if (index >= current.Count || current[index] == null)
                        {
                            report.Add(log, section, isUndo ? "deferred" : "skipped", "this PC has no NVIDIA display number " + Inv.S(index + 1)
                                + (isUndo ? " now; turn that monitor on and run this again" : ""));
                            missing++;
                            continue;
                        }

                        if (u != null)
                        {
                            u.Set(Inv.S(index), Inv.S(current[index][0]));
                            UndoLog.Save(undoLog);
                        }

                        if (!dry)
                        {
                            NvidiaDrs.SetVibrance(index, Math.Max(current[index][1], Math.Min(current[index][2], wanted[i])));
                        }

                        n++;
                    }

                    if (n > 0 || missing == 0)
                    {
                        report.Add(log, section, n > 0 ? "done" : "skipped", Inv.S(n) + " displays");
                    }

                    return;
                }
                default:
                    report.Add(log, section, "failed", "this restore has no handler for the part");
                    return;
            }
        }

        /// <summary>
        ///     Applies a part's registry values. A backup's cs2exe values are merged into the target's own (its
        ///     choice added or removed, the rest kept); an undo snapshot holds the target's values as they were
        ///     and writes them back as they are. Every entry is checked before the first is written.
        /// </summary>
        private static int RestoreRegistry(KvNode node, string cs2Exe, UndoLog undoLog, string section, bool dry, bool isUndo)
        {
            KvNode list = node.Child("registry");
            if (list == null)
            {
                return 0;
            }

            foreach (KvNode entry in list.Children)
            {
                string problem = RegistryEntryProblem(entry, isUndo);
                if (problem != null)
                {
                    throw new FormatException(problem);
                }
            }

            int n = 0;
            KvNode undoList = undoLog == null ? null : undoLog.Manifest.Section(section).Section("registry");
            foreach (KvNode entry in list.Children)
            {
                string subKey = entry.Get("key");
                string name = entry.Get("name");
                string valueName = name == RegistryItem.Cs2ExeToken ? cs2Exe : name;
                if (valueName == null)
                {
                    continue;
                }

                string oldKind;
                string oldData;
                bool had = WindowsSettings.ReadRegistry(subKey, valueName, out oldKind, out oldData);
                bool absent = entry.Get("absent") == "1";
                string kind = absent ? null : entry.Get("kind");
                string data = absent ? null : entry.Get("data");
                if (!isUndo && WindowsSettings.IsMergedValue(subKey))
                {
                    data = WindowsSettings.MergeValue(subKey, had ? oldData : null, data);
                    kind = data == null ? null : had && oldKind == "expandstring" ? oldKind : "string";
                }

                if (had ? (kind == oldKind && data == oldData) : kind == null)
                {
                    continue;
                }

                if (undoList != null)
                {
                    KvNode u = undoList.Section(Inv.S(undoList.Children.Count));
                    u.Set("key", subKey);
                    u.Set("name", name);
                    if (had)
                    {
                        u.Set("kind", oldKind);
                        u.Set("data", oldData);
                    }
                    else
                    {
                        u.Set("absent", "1");
                    }

                    UndoLog.Save(undoLog);
                }

                if (!dry)
                {
                    WindowsSettings.WriteRegistry(subKey, valueName, kind, data);
                }

                n++;
            }

            return n;
        }

        /// <summary>
        ///     Why a registry entry of a backup or undo snapshot cannot be written, or null. A backup names only
        ///     the values in <see cref="WindowsSettings.RegistryItems" />, each as the kind Windows reads it; an
        ///     undo snapshot puts back whatever kind the target had.
        /// </summary>
        public static string RegistryEntryProblem(KvNode entry, bool isUndo)
        {
            string subKey = entry.Get("key");
            string name = entry.Get("name");
            RegistryItem item = WindowsSettings.CarriedItem(subKey, name);
            if (item == null)
            {
                return "the backup names a registry value this tool does not carry: " + subKey + "\\" + name;
            }

            if (entry.Get("absent") == "1")
            {
                return null;
            }

            string kind = entry.Get("kind");
            string data = entry.Get("data");
            if (!isUndo && kind != item.Kind)
            {
                return "the backup gives " + subKey + "\\" + name + " as " + (kind ?? "no kind") + "; Windows reads it as " + item.Kind;
            }

            return WindowsSettings.IsValidRegistryData(kind, data) ? null : "the backup's " + (kind ?? "value") + " for " + subKey + "\\" + name + " is not valid";
        }

        /// <summary>
        ///     Why an NVIDIA entry of a backup or undo snapshot cannot be written, or null. Only its type and value
        ///     are judged here: a carried setting must be given as the type NVIDIA declares for it, with a value of
        ///     that type. An id the tool does not carry is reported, and passed over, by the restore itself.
        /// </summary>
        public static string NvidiaEntryProblem(KvNode item)
        {
            uint id;
            if (item.Get("absent") == "1"
                || !item.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || !uint.TryParse(item.Key.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
                || !NvidiaDrs.IsCarriedSetting(id))
            {
                return null;
            }

            string type = item.Get("type");
            string expected = NvidiaDrs.CarriedType(id);
            if (type != expected)
            {
                return "the backup gives NVIDIA setting " + item.Key + " as " + (type ?? "no type") + "; the driver declares it " + expected;
            }

            uint value;
            return uint.TryParse(item.Get("value"), NumberStyles.None, CultureInfo.InvariantCulture, out value)
                ? null
                : "the backup's value for NVIDIA setting " + item.Key + " is not a number";
        }

        /// <summary>Whether an undo snapshot could hold <paramref name="previous" /> as an entry its undo accepts.</summary>
        public static bool CanRecordNvidia(string key, NvSetting previous)
        {
            KvNode record = new KvNode(key);
            record.Set("type", previous.Type);
            record.Set("value", previous.Value);
            return NvidiaEntryProblem(record) == null;
        }

        private static readonly Regex BareGuid = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");

        /// <summary>
        ///     Why an undo snapshot's record of a power plan the restore added must not be deleted, or null: it must
        ///     be a bare lower-case GUID, as the restore records it, not one of the Windows templates, and not the
        ///     plan now active.
        /// </summary>
        public static string CreatedPlanProblem(string guid, string activeGuid)
        {
            if (guid == null || !BareGuid.IsMatch(guid))
            {
                return "not a power plan GUID";
            }

            if (WindowsSettings.IsBuiltInPowerPlan(guid))
            {
                return "it is a Windows template";
            }

            return string.Equals(guid, activeGuid, StringComparison.OrdinalIgnoreCase) ? "it is the active plan" : null;
        }

        /// <summary>Only the values in <see cref="WindowsSettings.RegistryItems" />: a backup cannot name another.</summary>
        public static bool IsCarriedRegistryValue(string subKey, string name)
        {
            return WindowsSettings.CarriedItem(subKey, name) != null;
        }

        private static void RecordFile(string target, string undoDir, string section, string relative, KvNode undoFiles)
        {
            if (!File.Exists(target))
            {
                undoFiles.Set(relative, "absent");
                return;
            }

            string copy = Path.Combine(undoDir, "files", section, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            SafeFile.ClearReadOnly(copy);
            // Flushed, and written without the target's attributes: the copy is the tool's own record, and it must
            // be on the disk before the change it records. A read-only target keeps its attribute on the target.
            SafeFile.WriteFlushed(copy, File.ReadAllBytes(target));
            undoFiles.Set(relative, Sha256(copy));
        }

        /// <summary>The keys a restore keeps from the target's copy of a file, or null when the file is copied whole.</summary>
        public static string[] HardwareKeys(string relative)
        {
            string name = relative.Substring(relative.LastIndexOf('/') + 1);
            if (string.Equals(name, VideoFile, StringComparison.OrdinalIgnoreCase))
            {
                return VideoHardwareKeys;
            }

            return string.Equals(name, MachineConvarsFile, StringComparison.OrdinalIgnoreCase) ? MachineHardwareKeys : null;
        }

        /// <summary>
        ///     A backup's cs2_video.txt or cs2_machine_convars.vcfg as it should be written over the target's,
        ///     keeping each of <paramref name="keys" /> as the target has it, and dropping it when the target has
        ///     none or no usable file: these name the source PC's GPU, monitor and devices, which the target does
        ///     not have, and CS2 detects its own when they are missing. A .vcfg holds its convars one level down,
        ///     under "convars". Throws on a backup copy that does not parse.
        /// </summary>
        public static KvNode MergeKeepingHardware(string backupFile, string targetFile, string[] keys)
        {
            return MergeKeepingHardware(File.ReadAllBytes(backupFile), targetFile, keys);
        }

        /// <summary>As <see cref="MergeKeepingHardware(string, string, string[])" />, from the backup copy's bytes.</summary>
        public static KvNode MergeKeepingHardware(byte[] backupFile, string targetFile, string[] keys)
        {
            KvNode incoming = KvNode.Parse(Text(backupFile));
            KvNode existing = null;
            if (File.Exists(targetFile))
            {
                try
                {
                    existing = KvNode.Load(targetFile);
                }
                catch (FormatException)
                {
                }
            }

            KvNode into = incoming.Child("convars") ?? incoming;
            KvNode from = existing == null ? null : existing.Child("convars") ?? existing;
            foreach (string key in keys)
            {
                string value = from == null ? null : from.Get(key);
                if (value != null)
                {
                    into.Set(key, value);
                }
                else
                {
                    into.Remove(key);
                }
            }

            return incoming;
        }

        /// <summary>A manifest path as a relative path that cannot leave the folder it is joined to.</summary>
        public static string SafeRelative(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0)
            {
                throw new FormatException("the backup names an unsafe path: " + path);
            }

            foreach (string part in normalized.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    throw new FormatException("the backup names an unsafe path: " + path);
                }
            }

            return normalized;
        }

        private static KvNode NewManifest(string kind, string creator)
        {
            KvNode manifest = new KvNode(RootKey);
            manifest.Set("format", FormatVersion);
            manifest.Set("kind", kind);
            manifest.Set("created", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            manifest.Set("creator", creator ?? "Advanced Config Backup");
            return manifest;
        }

        /// <summary>Bytes as File.ReadAllText reads a UTF-8 file: a byte order mark is dropped.</summary>
        public static string Text(byte[] bytes)
        {
            using (StreamReader reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        public static string Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Inv.Hex(sha.ComputeHash(data));
            }
        }

        public static string Sha256(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(file))
            {
                return Inv.Hex(sha.ComputeHash(stream));
            }
        }
    }
}
