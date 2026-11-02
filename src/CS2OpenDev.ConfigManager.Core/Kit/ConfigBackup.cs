#nullable disable
#pragma warning disable CA1068, CA1720, CA1825, CA1846, CA1850, CA1854, CA1865, CA2249
// v1's C# 5 style; see the header of KeyValues.cs.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        /// <summary>Sections read from or written to the Steam folder or the CS2 install. A name that is not a section throws.</summary>
        public static bool NeedsSteam(string section)
        {
            Scope(section);
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

        /// <summary>The registry, power plans and driver read; null is this PC (refused while a test assembly runs).</summary>
        internal IMachine Machine { get; set; }
    }

    /// <summary>
    ///     Whether a run writes a backup onto the PC or puts back what an earlier restore replaced. It always comes
    ///     from the command the person ran, never from the record being applied: v1 read it from the manifest, so a
    ///     backup that called itself an undo skipped the restore's path rules.
    /// </summary>
    internal enum RestoreMode
    {
        Restore,
        Undo
    }

    /// <summary>
    ///     What a restore applies: the capture record and the bytes of the files it lists. A verified archive
    ///     serves both from memory, so nothing a restore writes is read back from a folder someone could change.
    /// </summary>
    internal interface IBackupSource
    {
        /// <summary>The capture record (a backup's capture.vdf, an undo record's backup.vdf).</summary>
        KvNode Manifest { get; }

        /// <summary>The bytes of a file the record lists under <paramref name="section" />, or null when there are none.</summary>
        byte[] ReadFile(string section, string relative);

        /// <summary>Writes <see cref="Manifest" /> back, so an undo forgets each part once it is put back.</summary>
        void SaveManifest();
    }

    /// <summary>A capture or undo record kept as a folder: backup.vdf beside a files tree.</summary>
    internal sealed class FolderSource : IBackupSource
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

    /// <summary>Starts the NVIDIA driver's API: null once it is up, otherwise why not, and whether that may pass.</summary>
    internal delegate string NvidiaStarter(out bool mayPass);

    /// <summary>What to restore.</summary>
    internal sealed class RestoreOptions
    {
        public RestoreOptions()
        {
            WriteUndo = true;
            DriverTimeoutMs = ConfigBackup.DriverPartTimeoutMs;
        }

        /// <summary>The sections to restore; null restores every section the backup holds.</summary>
        public ICollection<string> Only { get; set; }

        public string SteamRoot { get; set; }

        /// <summary>The account to write CS2 settings to; null picks one (see <see cref="ConfigBackup.ResolveAccount(SteamInstall, string, string)" />).</summary>
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

        /// <summary>Also write the gamestate_integration_*.cfg files the backup carries; they are left alone unless asked for.</summary>
        public bool IncludeGameStateIntegration { get; set; }

        /// <summary>NVIDIA setting ids a restore leaves alone, from an organiser's policy. An undo puts back every id it recorded.</summary>
        public ICollection<uint> NvidiaDeny { get; set; }

        /// <summary>
        ///     Files a restore leaves as the target has them, each as "section/relative" (cs2/local/cfg/x.vcfg). A
        ///     .vcfg and its _lastclouded twin are one choice, so a pair is never split. An undo puts back every file
        ///     it recorded.
        /// </summary>
        public ICollection<string> LeaveFiles { get; set; }

        /// <summary>Whether a process of that name runs; null asks Windows or Linux. Tests stand in for a running CS2.</summary>
        public Func<string, bool> ProcessCheck { get; set; }

        /// <summary>
        ///     Finds Steam from <see cref="SteamRoot" />, or on the PC when that is null; null uses
        ///     <see cref="SteamInstall.Locate" />. acb's tests pass one that refuses to look on the PC, so a test that
        ///     names no Steam folder fails instead of reaching the real one.
        /// </summary>
        public Func<string, SteamInstall> LocateSteam { get; set; }

        /// <summary>Called with the record's folder just before a failed part is rolled back. Tests only.</summary>
        internal Action<string> BeforeRollback { get; set; }

        /// <summary>Starts NVAPI; null uses <see cref="NvidiaDrs.Initialize(out bool)" />. Tests stand in for a PC without the driver.</summary>
        internal NvidiaStarter StartNvidia { get; set; }

        /// <summary>The registry, power plans and driver changed; null is this PC (refused while a test assembly runs).</summary>
        internal IMachine Machine { get; set; }

        /// <summary>How long a part that calls into the NVIDIA driver may take. Tests shorten it.</summary>
        internal int DriverTimeoutMs { get; set; }

        internal bool IsRunning(string processName)
        {
            if (SkipProcessChecks)
            {
                return false;
            }

            return ProcessCheck != null ? ProcessCheck(processName) : SteamInstall.IsRunning(processName);
        }
    }

    /// <summary>
    ///     The outcomes a part can have, the only ones a result may carry: a misspelt or new one throws where it is
    ///     made, rather than passing as done.
    /// </summary>
    public static class Outcomes
    {
        public const string Done = "done";

        /// <summary>Nothing to do.</summary>
        public const string Skipped = "skipped";

        /// <summary>Not now, for a reason that passes, such as CS2 running.</summary>
        public const string Deferred = "deferred";

        /// <summary>A target cannot be written, so nothing of the part was.</summary>
        public const string Blocked = "blocked";

        public const string Failed = "failed";

        /// <summary>Needs an administrator.</summary>
        public const string Admin = "admin";

        /// <summary>An undo's part waiting for a newer record of the same part.</summary>
        public const string Left = "left";

        private static readonly string[] all = { Done, Skipped, Deferred, Blocked, Failed, Admin, Left };

        /// <summary>Every outcome, read-only so no code in the process can change which outcomes are valid.</summary>
        public static readonly ReadOnlyCollection<string> All = new ReadOnlyCollection<string>(all);

        /// <summary>Whether <paramref name="outcome" /> is one of <see cref="All" />, compared ordinally.</summary>
        public static bool IsKnown(string outcome)
        {
            return outcome != null && Array.IndexOf(all, outcome) >= 0;
        }

        /// <summary>Whether the part is finished: done, or nothing to do.</summary>
        public static bool IsSettled(string outcome)
        {
            return outcome == Done || outcome == Skipped;
        }
    }

    /// <summary>
    ///     Stands between a driver part's worker and its first change. Once the run gives up on the worker, one that
    ///     has not changed anything yet never does, so the run knows whether the part may have changed the PC.
    /// </summary>
    internal sealed class DriverGate
    {
        private readonly object _lock = new object();
        private bool _givenUp;
        private bool _started;

        /// <summary>Called by the worker before each change; throws when the run gave up on it before its first.</summary>
        public void Enter()
        {
            lock (_lock)
            {
                if (_givenUp && !_started)
                {
                    throw new OperationCanceledException("the run gave up on this part before it changed anything");
                }

                _started = true;
            }
        }

        /// <summary>Called by the run when it gives up on the worker: whether the worker may have changed something.</summary>
        public bool GiveUp()
        {
            lock (_lock)
            {
                _givenUp = true;
                return _started;
            }
        }
    }

    /// <summary>How one section went.</summary>
    public sealed class SectionResult
    {
        public SectionResult(string section, string outcome, string detail)
        {
            if (!Outcomes.IsKnown(outcome))
            {
                throw new ArgumentException("Not an outcome: " + outcome);
            }

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

        /// <summary>Each setting a run would change or changed, with the value the PC had and the one it gets, for a person to review.</summary>
        public List<SettingChange> Settings { get; private set; } = new List<SettingChange>();

        /// <summary>
        ///     Driver parts given up on after their worker may have started changing the PC, so what they changed is
        ///     not known. The worker may be cut off at any point, by process exit too; every driver write is recorded
        ///     before it is made, so their record covers whatever they changed, for acb undo.
        /// </summary>
        public List<string> Unknown { get; private set; } = new List<string>();

        /// <summary>On a driver part's own report: what its worker passes before each change.</summary>
        internal DriverGate Gate { get; set; }

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
                    if (r.Outcome == Outcomes.Admin)
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

        /// <summary>
        ///     In an undo record's file part, the SHA-256 of each file as the restore left it, for showing whether it
        ///     changed since. An undo reads only the part's "files".
        /// </summary>
        public const string WroteKey = "wrote";

        /// <summary>CS2's video settings; restored by merging so the target keeps what describes its own hardware.</summary>
        public const string VideoFile = "cs2_video.txt";

        /// <summary>
        ///     Set by the test assemblies when they load: the engine's default machine refuses every read and write of
        ///     this PC's registry, power plan and driver (tests pass a fake), and looking for this PC's own Steam
        ///     throws, so a test can never read or change the PC it runs on.
        /// </summary>
        internal static bool MachineWritesRefused { get; set; }

        /// <summary>The machine a run reads and changes: the one it was given, else this PC, which a test assembly refuses.</summary>
        internal static IMachine MachineOf(IMachine given)
        {
            return given ?? (MachineWritesRefused ? (IMachine)RefusingMachine.Instance : RealMachine.Instance);
        }

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
            if (!IsPlainPath(path) || !path.StartsWith("local/cfg/", StringComparison.OrdinalIgnoreCase) || path.IndexOf('/', 10) >= 0)
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
            if (!IsPlainPath(path) || path.StartsWith("devonly/", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".cfg", StringComparison.Ordinal))
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

        /// <summary>
        ///     Whether no name in a path is empty or ends in a dot or space, which Windows drops, so such a name would
        ///     be judged by these rules as one file and written as another.
        /// </summary>
        private static bool IsPlainPath(string path)
        {
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Whether a name in a path could be a Windows 8.3 alias, which opens another file (GAMEST~1.CFG for
        ///     gamestate_integration_x.cfg): at most eight characters, a dot and three, with a tilde and a digit, as
        ///     Windows makes them. Setting one by hand needs an administrator's restore privilege. Only names from a
        ///     backup are judged this way; capture reads the folder's own long names.
        /// </summary>
        private static bool MayBeAlias(string path)
        {
            foreach (string part in path.Split('/'))
            {
                int dot = part.IndexOf('.');
                bool shortShape = dot < 0
                    ? part.Length <= 8
                    : dot <= 8 && part.Length - dot - 1 <= 3 && part.IndexOf('.', dot + 1) < 0;
                if (!shortShape)
                {
                    continue;
                }

                for (int i = 0; i + 1 < part.Length; i++)
                {
                    if (part[i] == '~' && part[i + 1] >= '0' && part[i + 1] <= '9')
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Whether a path in game/csgo/cfg is server.cfg itself, which a backup carries only once the player edited it.</summary>
        public static bool IsServerCfg(string relative)
        {
            return string.Equals((relative ?? "").Replace('\\', '/'), "server.cfg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Whether a backup may write this path of a file part: the same rules capture uses, so neither an older
        ///     backup nor a hand-edited one reaches Steam Cloud's remote/ folder or Valve's own cfg files. A
        ///     gamestate_integration_*.cfg sends live game data to the address it names for every account on the
        ///     PC, so it is written only when the restore was asked to. A name that could be an 8.3 alias is never written.
        /// </summary>
        public static bool IsRestorablePath(string section, string relative, bool includeGameStateIntegration)
        {
            if (MayBeAlias((relative ?? "").Replace('\\', '/')))
            {
                return false;
            }

            if (section == Sections.Cs2)
            {
                return IsAccountSetting(relative);
            }

            return section == Sections.Cs2Game && (IsPlayerCfg(relative, includeGameStateIntegration) || IsServerCfg(relative));
        }

        /// <summary>Whether a path in game/csgo/cfg is a game state integration file.</summary>
        public static bool IsGameStateIntegration(string relative)
        {
            string path = (relative ?? "").Replace('\\', '/');
            return path.Substring(path.LastIndexOf('/') + 1).StartsWith("gamestate_integration_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether a capture record's value for a file is a SHA-256 (64 lowercase hex digits), the only value capture writes.</summary>
        private static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
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

                    report.Add(log, section, Outcomes.Failed, ex.Message + "; nothing from this part is in the backup");
                }
            }

            manifest.Save(Path.Combine(outDir, ManifestName));
            return report;
        }

        /// <summary>How long a part that calls into the NVIDIA driver may take; longer than NVAPI's own start timeout.</summary>
        public const int DriverPartTimeoutMs = 60000;

        private static readonly object DriverWorkerLock = new object();

        /// <summary>The last worker given up on (timed out or cancelled), or null.</summary>
        private static Thread _abandonedWorker;

        /// <summary>The last worker started, or null; a new one waits for it, so one thread at a time is in the driver.</summary>
        private static Thread _currentWorker;

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
                report.Add(log, section, Outcomes.Failed, why);
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

        private static void GiveUp(Thread worker)
        {
            lock (DriverWorkerLock)
            {
                _abandonedWorker = worker;
            }
        }

        /// <summary>
        ///     Runs <paramref name="body" /> on a background thread and waits for it while watching for a cancel.
        ///     Null once it finished, with what it threw in <paramref name="error" />; otherwise why it was given
        ///     up on, and the thread is left to finish alone. One thread at a time is in the driver: a call waits,
        ///     within its own limit, for one still running. While a thread given up on is still running no
        ///     second one starts, since a driver that hung once is likely to hang again.
        /// </summary>
        public static string RunOnDriverWorker(ThreadStart body, CancellationToken cancellation, int timeoutMs, out Exception error)
        {
            error = null;
            Exception thrown = null;
            Thread worker;
            int waited = 0;
            while (true)
            {
                Thread busy;
                lock (DriverWorkerLock)
                {
                    if (_abandonedWorker != null && _abandonedWorker.IsAlive)
                    {
                        return "an earlier call into the NVIDIA driver has not returned; close the program and start it again to try again";
                    }

                    busy = _currentWorker != null && _currentWorker.IsAlive ? _currentWorker : null;
                    if (busy == null)
                    {
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
                        worker.Start();
                        _currentWorker = worker;
                        break;
                    }
                }

                // Nothing was started for this call, so there is nothing to give up on.
                if (!busy.Join(100))
                {
                    waited += 100;
                    if (cancellation.IsCancellationRequested)
                    {
                        return "cancelled while waiting for the NVIDIA driver";
                    }

                    if (waited >= timeoutMs)
                    {
                        return "the NVIDIA driver was busy with another call for " + Inv.S(timeoutMs / 1000) + " seconds";
                    }
                }
            }

            while (!worker.Join(100))
            {
                waited += 100;
                if (cancellation.IsCancellationRequested)
                {
                    GiveUp(worker);
                    return "cancelled while waiting for the NVIDIA driver";
                }

                if (waited >= timeoutMs)
                {
                    GiveUp(worker);
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
                report.Add(log, section, Outcomes.Skipped, "Steam was not found");
                return;
            }

            // On Linux the NVIDIA part is the driver's application profiles, files in the user's home.
            if (section == Sections.Nvidia && !Platform.IsWindows)
            {
                CaptureLinuxNvidia(MachineOf(options.Machine), manifest, report, log);
                return;
            }

            if (Sections.IsMachineWide(section) && !Platform.IsWindows)
            {
                report.Add(log, section, Outcomes.Skipped, "only on Windows");
                return;
            }

            switch (section)
            {
                case Sections.Cs2:
                {
                    if (account == null)
                    {
                        report.Add(log, section, Outcomes.Skipped, "no Steam account on this PC has CS2 settings");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    node.Set("account", account);
                    List<string> redacted = new List<string>();
                    int n = CaptureTree(steam.AccountFolder(account), outDir, section, node.Section("files"),
                        delegate(string rel) { return IsAccountSetting(rel); }, redacted);
                    report.Add(log, section, n > 0 ? Outcomes.Done : Outcomes.Skipped, Inv.S(n) + " files from account " + account
                        + (redacted.Count > 0 ? "; passwords and server tokens left out of " + string.Join(", ", redacted.ToArray()) : ""));
                    return;
                }
                case Sections.Cs2Game:
                {
                    string cfg = steam.Cs2CfgDir();
                    if (cfg == null || !Directory.Exists(cfg))
                    {
                        report.Add(log, section, Outcomes.Skipped, "CS2 is not installed");
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
                    report.Add(log, section, n > 0 ? Outcomes.Done : Outcomes.Skipped, n > 0
                        ? Inv.S(n) + " cfg files" + (redacted.Count > 0 ? "; passwords and server tokens left out of " + string.Join(", ", redacted.ToArray()) : "")
                        : "no cfg files of your own");
                    return;
                }
                case Sections.Launch:
                {
                    string value = account == null ? null : steam.ReadLaunchOptions(account);
                    if (string.IsNullOrEmpty(value))
                    {
                        report.Add(log, section, Outcomes.Skipped, "none are set");
                        return;
                    }

                    List<string> secrets;
                    value = RemoveSecrets(value, out secrets);
                    KvNode launch = manifest.Section(section);
                    launch.Set("account", account);
                    launch.Set("value", value);
                    report.Add(log, section, Outcomes.Done, value + (secrets.Count > 0 ? " (the value of " + string.Join(", ", secrets.ToArray()) + " left out)" : "")
                        + (HasControlCharacter(value) ? "; they hold a control character, so a restore will refuse them: set them again in Steam" : ""));
                    return;
                }
                case Sections.Input:
                {
                    // Only a hotkey that is off travels: a restore turns the target's off and never turns one on.
                    KvNode node = manifest.Section(section);
                    foreach (KeyValuePair<string, string> pair in MachineOf(options.Machine).ReadHotkeys())
                    {
                        if (pair.Value == "0")
                        {
                            node.Set(pair.Key, "0");
                        }
                    }

                    if (node.Children.Count == 0)
                    {
                        manifest.Remove(section);
                        report.Add(log, section, Outcomes.Skipped, "every hotkey is on, as Windows ships it");
                        return;
                    }

                    report.Add(log, section, Outcomes.Done, Inv.S(node.Children.Count) + " hotkeys off");
                    return;
                }
                case Sections.GameBar:
                case Sections.Cs2Exe:
                {
                    string exe = section == Sections.Cs2Exe ? steam.Cs2Exe() : null;
                    if (section == Sections.Cs2Exe && exe == null)
                    {
                        report.Add(log, section, Outcomes.Skipped, "CS2 is not installed");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    int n = CaptureRegistry(MachineOf(options.Machine), section, node, exe);
                    if (node.Child("registry") == null)
                    {
                        manifest.Remove(section);
                        report.Add(log, section, Outcomes.Skipped, "nothing is set");
                        return;
                    }

                    int unset = node.Child("registry").Children.Count - n;
                    report.Add(log, section, Outcomes.Done, Inv.S(n) + " registry values" + (unset > 0 ? ", " + Inv.S(unset) + " at Windows' default" : ""));
                    return;
                }
                case Sections.Power:
                {
                    string[] plan = MachineOf(options.Machine).ActivePowerPlan();
                    if (plan == null)
                    {
                        report.Add(log, section, Outcomes.Skipped, "the active power plan could not be read");
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    node.Set("guid", plan[0]);
                    node.Set("name", plan[1]);
                    // Only Windows' own templates travel: a plan of the user's own carries this PC's hardware
                    // settings. A copy named like a template (Ultimate Performance made with powercfg
                    // -duplicatescheme) is recorded with that template, which another PC can copy.
                    string template = WindowsSettings.PowerPlanTemplate(plan[0], plan[1], MachineOf(options.Machine).PowerPlans());
                    if (template != null && !string.Equals(template, plan[0], StringComparison.OrdinalIgnoreCase))
                    {
                        node.Set("template", template);
                    }

                    report.Add(log, section, Outcomes.Done, template != null
                        ? plan[1]
                        : plan[1] + ", a plan of your own: another PC switches to it only if it has one of that name");
                    return;
                }
                case Sections.Nvidia:
                {
                    bool mayPassIgnored;
                    string why = MachineOf(options.Machine).StartNvidia(out mayPassIgnored);
                    if (why != null)
                    {
                        report.Add(log, section, Outcomes.Skipped, why);
                        return;
                    }

                    KvNode node = manifest.Section(section);
                    string driver = MachineOf(options.Machine).NvidiaDriverVersion();
                    if (driver != null)
                    {
                        node.Set("driver", driver);
                    }

                    // What cs2.exe gets for each carried setting, whether set on its profile or the global one;
                    // a setting the driver decides is recorded absent, so a restore resets the target's.
                    int changed = 0;
                    KvNode list = node.Section("cs2").Section("settings");
                    using (INvidiaSession drs = MachineOf(options.Machine).OpenNvidia())
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

                    report.Add(log, section, Outcomes.Done, Inv.S(changed) + " of " + Inv.S(NvidiaDrs.CarriedSettings.Length) + " carried settings changed from the driver's" + (driver == null ? "" : " (driver " + driver + ")"));
                    return;
                }
                case Sections.Vibrance:
                {
                    IList<int[]> levels = MachineOf(options.Machine).ReadVibrance();
                    if (levels.Count == 0)
                    {
                        report.Add(log, section, Outcomes.Skipped, "no NVIDIA display reports digital vibrance");
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
                        report.Add(log, section, Outcomes.Skipped, "no NVIDIA display reported its vibrance level");
                        return;
                    }

                    report.Add(log, section, Outcomes.Done, "level " + string.Join(", ", shown.ToArray()));
                    return;
                }
            }
        }

        /// <summary>Records a group's registry values, as <see cref="RegistryEntryForBackup" /> decides. Returns how many values hold something.</summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static int CaptureRegistry(IMachine machine, string group, KvNode node, string cs2Exe)
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
                bool present = valueName != null && machine.ReadRegistry(item.SubKey, valueName, out kind, out data);
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
                        detail = node.Child("linux") != null
                            ? Inv.S(node.Child("linux").Children.Count) + " settings for the cs2 process"
                            : DescribeNvidia(node.Path("cs2", "settings"));
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

        /// <summary>
        ///     On an undo, whether an account folder that is not found is gone (the PC was wiped, so nothing of it is
        ///     left to put back) rather than looked for in the wrong place: only when this is the Steam folder the
        ///     record's restore wrote to and its userdata folder is still there.
        /// </summary>
        private static bool AccountGone(SteamInstall steam, KvNode manifest)
        {
            string recorded = manifest.Get("steam");
            return recorded != null && SamePath(recorded, steam.Root) && Directory.Exists(steam.UserData);
        }

        private static string UndoAccountReason(string why, SteamInstall steam, KvNode manifest)
        {
            if (AccountGone(steam, manifest))
            {
                return why + "; nothing of it is left to put back";
            }

            string recorded = manifest.Get("steam");
            if (recorded != null && SamePath(recorded, steam.Root))
            {
                return "the account's folder is not under " + steam.Root + ", which has no userdata folder at all (wiped, or nobody has signed in since). "
                       + "Left in the record; if it is gone for good, acb undo --set-aside with this record's name forgets what it holds";
            }

            return "the account's folder is not under " + steam.UserData + ", and its restore wrote to "
                   + (recorded ?? "a Steam folder it did not record") + ". Left in the record; run acb undo again with --steam-path naming that Steam folder, "
                   + "or, if that Steam is gone for good, acb undo --set-aside with this record's name forgets what it holds";
        }

        /// <summary>
        ///     Why an undo record's part written through a Steam folder can no longer be put back, or null when it
        ///     still can: the Steam folder its restore wrote to is gone, or under it the account's folder (cs2,
        ///     launch) or the CS2 install (cs2game, cs2exe). A record that names no Steam folder is not known to be
        ///     stranded. Looks only at the recorded folder, never for this PC's own Steam.
        /// </summary>
        internal static string StrandedReason(KvNode manifest, string section)
        {
            string recorded = manifest.Get("steam");
            if (string.IsNullOrEmpty(recorded))
            {
                return null;
            }

            SteamInstall steam = SteamInstall.Locate(recorded);
            if (steam == null)
            {
                return "Steam is gone from " + recorded;
            }

            if (section == Sections.Cs2 || section == Sections.Launch)
            {
                KvNode node = manifest.Child(section);
                string account = node == null ? null : node.Get("account");
                return IsAccountId(account) && !Directory.Exists(Path.Combine(steam.UserData, account))
                    ? "account " + account + " is gone from " + steam.UserData
                    : null;
            }

            if (section == Sections.Cs2Game || section == Sections.Cs2Exe)
            {
                return steam.Cs2InstallDir() == null ? "CS2 is not installed under " + steam.Root : null;
            }

            return null;
        }

        /// <summary>The choice key for a file: its section and path, a _lastclouded twin named by its .vcfg.</summary>
        public static string FileChoiceKey(string section, string relative)
        {
            const string twin = "_lastclouded";
            string named = relative.EndsWith(".vcfg" + twin, StringComparison.OrdinalIgnoreCase) ? relative.Substring(0, relative.Length - twin.Length) : relative;
            return section + "/" + named;
        }

        private static bool LeftByChoice(ICollection<string> leave, string section, string relative)
        {
            if (leave == null || leave.Count == 0)
            {
                return false;
            }

            string key = FileChoiceKey(section, relative);
            foreach (string left in leave)
            {
                if (string.Equals(left, key, Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                Platform.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
        internal static RunReport Restore(IBackupSource source, RestoreMode mode, RestoreOptions options, TextWriter log)
        {
            KvNode manifest = source.Manifest;
            RunReport report = new RunReport();
            // Steam is looked for only when a chosen part reads or writes it, so a run of the driver's settings opens
            // no Steam folder at all.
            bool isUndo = mode == RestoreMode.Undo;
            if (!isUndo && !options.DryRun && (!options.WriteUndo || string.IsNullOrEmpty(options.UndoRoot)))
            {
                // Nothing restores without its record, whoever builds the options.
                throw new ArgumentException("A restore that changes anything needs the restorer's undo store; a record is never kept beside the backup or skipped.");
            }

            SteamInstall steam = UsesSteam(manifest, options) ? (options.LocateSteam ?? SteamInstall.Locate)(options.SteamRoot) : null;
            string userSid = Platform.IsWindows ? WindowsSettings.CurrentUserSid() : null;
            UndoLog undo = null;
            if (!options.DryRun && !isUndo)
            {
                undo = new UndoLog(options.UndoRoot, NewManifest("undo", "Advanced Config Backup restore of " + (manifest.Get("created") ?? "a backup")));
                if (userSid != null)
                {
                    undo.Manifest.Set("user", userSid);
                }

                // The undo writes back into this Steam, whichever one a later run would find first.
                if (steam != null)
                {
                    undo.Manifest.Set("steam", steam.Root);
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
                    if (section == Sections.Nvidia || section == Sections.Vibrance)
                    {
                        RestoreDriverSection(section, node, manifest, source, isUndo, options, steam, undo, userSid, report, log);
                    }
                    else
                    {
                        RestoreSection(section, node, manifest, source, isUndo, options, steam, undo, userSid, report, log);
                    }
                }
                catch (NvAdminRequiredException)
                {
                    report.Add(log, section, Outcomes.Admin, "needs an administrator");
                }
                catch (UndoClaimException ex)
                {
                    report.Add(log, section, Outcomes.Failed, ex.Message);
                    report.ClaimProblem = ex.Message;
                    break;
                }
                catch (TargetBlockedException ex)
                {
                    report.Add(log, section, Outcomes.Blocked, ex.Message);
                }
                catch (Exception ex)
                {
                    report.Add(log, section, Outcomes.Failed, ex.Message);
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
                        report.Add(log, section, Outcomes.Failed, "put back, but the record could not note it: " + ex.Message);
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

        /// <summary>
        ///     Restores a part that calls into the NVIDIA driver on the driver worker, as capture does, so a driver
        ///     that never answers leaves the part for later instead of holding the run. A worker given up on before
        ///     its first change never makes one; one given up on after it may have changed the PC, so a restore
        ///     reports that part as not known. Such a worker is not waited for and dies with the process; its record
        ///     still covers what it changed, because each driver write is recorded first. What the part threw is
        ///     thrown here, as any other part's would be.
        /// </summary>
        private static void RestoreDriverSection(string section, KvNode node, KvNode manifest, IBackupSource source, bool isUndo, RestoreOptions options,
            SteamInstall steam, UndoLog undo, string userSid, RunReport report, TextWriter log)
        {
            RunReport partReport = new RunReport();
            DriverGate gate = new DriverGate();
            partReport.Gate = gate;
            Exception error;
            string why = RunOnDriverWorker(delegate
            {
                RestoreSection(section, node, manifest, source, isUndo, options, steam, undo, userSid, partReport, null);
            }, CancellationToken.None, options.DriverTimeoutMs, out error);
            if (why != null)
            {
                bool mayHaveChanged = gate.GiveUp();
                report.Add(log, section, Outcomes.Deferred, why + DriverTimeoutDetail(mayHaveChanged, isUndo));
                if (mayHaveChanged && !isUndo)
                {
                    report.Unknown.Add(section);
                }

                return;
            }

            foreach (SectionResult result in partReport.Results)
            {
                report.Add(log, result.Section, result.Outcome, result.Detail);
            }

            report.Observed.AddRange(partReport.Observed);
            report.Settings.AddRange(partReport.Settings);
            if (error != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }

        /// <summary>Before a driver part's change: stops a worker the run already gave up on, so it changes nothing late.</summary>
        private static void PassGate(RunReport report)
        {
            if (report.Gate != null)
            {
                report.Gate.Enter();
            }
        }

        /// <summary>What follows the reason a driver part was given up on, by whether its worker may have changed the PC.</summary>
        internal static string DriverTimeoutDetail(bool mayHaveChanged, bool isUndo)
        {
            if (!mayHaveChanged)
            {
                return "; nothing of this part was changed. Run this again later";
            }

            return isUndo
                ? "; some of it may already be put back. Run acb undo again later, which puts back what the record still holds"
                : "; it may already have changed this part, so what it changed is not known. Its record keeps what it recorded, and acb undo puts that back";
        }

        private static bool UsesSteam(KvNode manifest, RestoreOptions options)
        {
            foreach (string section in Sections.All)
            {
                if (Sections.NeedsSteam(section) && manifest.Child(section) != null && (options.Only == null || options.Only.Contains(section)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A control character other than a tab, or a line or paragraph separator; a tab is printed as \t.</summary>
        private static bool HasControlCharacter(string text)
        {
            foreach (char c in text)
            {
                if ((char.IsControl(c) && c != '\t') || c == '\u2028' || c == '\u2029')
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Failed(RunReport report, int first)
        {
            for (int i = first; i < report.Results.Count; i++)
            {
                if (report.Results[i].Outcome == Outcomes.Failed)
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
                    SkipProcessChecks = true,
                    StartNvidia = options.StartNvidia,
                    Machine = options.Machine,
                    DriverTimeoutMs = options.DriverTimeoutMs
                };
                RunReport result = new RunReport();
                IBackupSource recordSource = new FolderSource(undo.Dir, undo.Manifest);
                if (section == Sections.Nvidia || section == Sections.Vibrance)
                {
                    // Bounded as the restore was, so a driver that stops answering stops the rollback, not the run.
                    RestoreDriverSection(section, recorded, undo.Manifest, recordSource, true, back, steam, null, userSid, result, log);
                }
                else
                {
                    RestoreSection(section, recorded, undo.Manifest, recordSource, true, back, steam, null, userSid, result, log);
                }
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
                if (!Outcomes.IsSettled(report.Results[i].Outcome))
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
            IMachine machine = MachineOf(options.Machine);
            KvNode undo = undoLog == null ? null : undoLog.Manifest;
            bool needsSteam = Sections.NeedsSteam(section);
            if (needsSteam && steam == null)
            {
                report.Add(log, section, Outcomes.Deferred, (string.IsNullOrEmpty(options.SteamRoot)
                    ? "Steam was not found on this PC"
                    : "Steam was not found at " + options.SteamRoot)
                    + (isUndo ? "; run acb undo again once it is back, or, if it is gone for good, acb undo --set-aside with this record's name forgets what it holds" : ""));
                return;
            }

            // On Linux the NVIDIA part is the driver's application profiles, files in the user's home.
            if (section == Sections.Nvidia && !Platform.IsWindows)
            {
                RestoreLinuxNvidia(machine, node, backup, isUndo, dry, undoLog, report, log);
                return;
            }

            if (Sections.IsMachineWide(section) && !Platform.IsWindows)
            {
                report.Add(log, section, Outcomes.Skipped, "only on Windows");
                return;
            }

            string backupAccount = BackupAccount(manifest);
            if (Sections.IsPerUser(section))
            {
                string other = OtherUserReason(WindowsSettings.CurrentUserName(), WindowsSettings.SessionUserName(),
                    isUndo ? manifest.Get("user") : null, userSid);
                if (other != null)
                {
                    report.Add(log, section, Outcomes.Deferred, other);
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
                        report.Add(log, section, Outcomes.Deferred, "CS2 is running; it rewrites its settings when it closes. Close it and run this again");
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
                            // Left for later on a restore (the account signs in, then restore again). On an undo the
                            // part is forgotten only when the account's folder is gone from the Steam its restore wrote to.
                            report.Add(log, section, isUndo && AccountGone(steam, manifest) ? Outcomes.Skipped : Outcomes.Deferred, isUndo ? UndoAccountReason(why, steam, manifest) : why);
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
                            report.Add(log, section, Outcomes.Deferred, "CS2 is not installed on this PC");
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
                    List<string> gsiLeft = new List<string>();
                    List<string> gsiWritten = new List<string>();
                    List<string> chosenLeft = new List<string>();
                    HashSet<string> named = new HashSet<string>(Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    foreach (KvNode file in files == null ? new KvNode[0] : (IEnumerable<KvNode>)files.Children)
                    {
                        string relative = SafeRelative(file.Key);
                        // A second entry for one file would be recorded over the first, so the undo record would keep
                        // the restore's own bytes instead of what the PC had.
                        if (!named.Add(relative))
                        {
                            throw new FormatException("the backup names " + relative + " twice, so nothing in this part was written");
                        }
                        // An undo snapshot puts back whatever its restore changed; a backup writes only what capture would carry.
                        if (!isUndo && !IsRestorablePath(section, relative, options.IncludeGameStateIntegration))
                        {
                            if (IsRestorablePath(section, relative, true))
                            {
                                gsiLeft.Add(relative);
                            }
                            else
                            {
                                outside.Add(relative);
                            }

                            continue;
                        }

                        if (!isUndo && LeftByChoice(options.LeaveFiles, section, relative))
                        {
                            chosenLeft.Add(relative);
                            continue;
                        }

                        // Capture records every file by its SHA-256; "absent" (delete it) is only ever an undo record's.
                        if (!isUndo && !IsSha256Hex(file.Value))
                        {
                            throw new IOException("the backup's entry for " + relative + " is not a file's SHA-256, so nothing in this part was written");
                        }

                        if (!isUndo && section == Sections.Cs2Game && IsGameStateIntegration(relative))
                        {
                            gsiWritten.Add(relative);
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
                            // An undo entry that removes a file the restore added needs only the right to remove it.
                            if (file.Value == "absent")
                            {
                                SafeFile.CheckDeletable(writable);
                            }
                            else
                            {
                                SafeFile.CheckWritable(writable, dry);
                            }

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

                    // What each file holds now, after the restore, so an undo's review can tell a file changed since
                    // (by CS2, or by the next person) from one as the restore left it. Kept after the writes, as it
                    // records nothing that undo needs: a record without it is put back all the same.
                    if (undoFiles != null && !dry && !isUndo && written > 0)
                    {
                        KvNode wrote = undo.Section(section).Section(WroteKey);
                        for (int i = 0; i < relatives.Count; i++)
                        {
                            string target = Path.Combine(targetRoot, relatives[i].Replace('/', Path.DirectorySeparatorChar));
                            if (sources[i] != null && wrote.Child(relatives[i]) == null && File.Exists(target))
                            {
                                wrote.Set(relatives[i], Sha256(target));
                            }
                        }

                        UndoLog.Save(undoLog);
                    }

                    report.Add(log, section, Outcomes.Done, (dry ? "would write " : "wrote ") + Inv.S(written) + " files to " + where
                                                     + (removed > 0 ? ", removed " + Inv.S(removed) : "")
                                                     + (gsiWritten.Count > 0 ? "; among them game state integration files, which send live game data to the address they name: " + string.Join(", ", gsiWritten.ToArray()) : "")
                                                     + (gsiLeft.Count > 0 ? "; left alone, as game state integration files are restored only with --gsi: " + string.Join(", ", gsiLeft.ToArray()) : "")
                                                     + (outside.Count > 0 ? "; left alone, as files this tool does not restore: " + string.Join(", ", outside.ToArray()) : "")
                                                     + (chosenLeft.Count > 0 ? "; left alone, as chosen: " + string.Join(", ", chosenLeft.ToArray()) : ""));
                    return;
                }
                case Sections.Launch:
                {
                    if (options.IsRunning("steam"))
                    {
                        report.Add(log, section, Outcomes.Deferred, "Steam is running; it rewrites launch options when it closes. Close Steam and run this again");
                        return;
                    }

                    string why;
                    string account = ResolveAccount(steam, options.AccountId, backupAccount, out why);
                    if (account == null)
                    {
                        report.Add(log, section, isUndo && AccountGone(steam, manifest) ? Outcomes.Skipped : Outcomes.Deferred, isUndo ? UndoAccountReason(why, steam, manifest) : why);
                        return;
                    }

                    string value = node.Get("absent") == "1" ? null : node.Get("value");
                    if (!isUndo && value != null && HasControlCharacter(value))
                    {
                        // Steam's own window cannot enter one, and Steam would show it to the player as written.
                        // Capture copies it but says a restore will refuse it, so the player learns on their own PC.
                        throw new InvalidDataException("the backup's launch options hold a control character, which Steam's own window cannot enter, so they were not written");
                    }

                    string localConfig = steam.LocalConfigPath(account);
                    report.Observed.Add(section + " " + localConfig + " " + (File.Exists(localConfig) ? Sha256(localConfig) : "absent"));
                    string launchNow = steam.ReadLaunchOptions(account);
                    if (launchNow != value)
                    {
                        report.Settings.Add(new SettingChange(section, "launch options", launchNow, value));
                    }
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

                    report.Add(log, section, Outcomes.Done, (value ?? "cleared") + " for " + Where(account, backupAccount));
                    return;
                }
                case Sections.Input:
                {
                    // A backup only turns hotkeys off; its undo puts each back as the target had it.
                    IDictionary<string, string> current = machine.ReadHotkeys();
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

                        report.Settings.Add(new SettingChange(section, value.Key + " hotkey", previous == "1" ? "on" : "off", active ? "on" : "off"));

                        if (u != null)
                        {
                            u.Set(value.Key, previous);
                            UndoLog.Save(undoLog);
                        }

                        if (!dry)
                        {
                            machine.SetHotkey(value.Key, active);
                        }

                        n++;
                    }

                    report.Add(log, section, n > 0 ? Outcomes.Done : Outcomes.Skipped, n > 0 ? Inv.S(n) + " hotkeys " + (isUndo ? "put back" : "turned off") : "already as wanted");
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
                            report.Add(log, section, Outcomes.Deferred, "CS2 is not installed on this PC");
                            return;
                        }
                    }

                    int n = RestoreRegistry(machine, node, exe, undoLog, section, dry, isUndo, report);
                    report.Add(log, section, Outcomes.Done, Inv.S(n) + " registry values");
                    return;
                }
                case Sections.Power:
                {
                    string guid = node.Get("guid");
                    string name = node.Get("name");
                    string template = isUndo ? null : node.Get("template");
                    string shown = name ?? guid;
                    string[] current = machine.ActivePowerPlan();
                    string useGuid;
                    string action = WindowsSettings.PowerPlanAction(guid, name, template, machine.PowerPlans(), current == null ? null : current[0], out useGuid);
                    string problem = null;
                    string created = null;
                    if (action == "missing")
                    {
                        report.Add(log, section, Outcomes.Skipped, "this PC has no plan named " + shown);
                    }
                    else if (action == "already")
                    {
                        report.Add(log, section, Outcomes.Skipped, "already " + shown);
                    }
                    else if (dry)
                    {
                        report.Settings.Add(new SettingChange(section, "active power plan", current == null ? null : current[1], shown));
                        report.Add(log, section, Outcomes.Done, (action == "copy" ? "would add Windows' " : "would switch to ") + shown);
                    }
                    else
                    {
                        // The plan to switch back to is on record before anything changes, so a plan that cannot be
                        // read is not switched away from.
                        if (undo != null && current == null)
                        {
                            report.Add(log, section, Outcomes.Failed, "could not read which power plan is active, so it could not be put back afterwards; left as it is");
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
                            // The record says a copy is being made before powercfg runs, so an undo can say so if
                            // acb stops before the copy's GUID is on record.
                            ICollection<string> before = machine.PowerPlans().Keys;
                            if (u != null)
                            {
                                u.Set("creating", useGuid);
                                UndoLog.Save(undoLog);
                            }

                            string copyOf = useGuid;
                            useGuid = machine.DuplicatePowerPlan(copyOf, out problem);
                            if (useGuid == null)
                            {
                                // powercfg stopped after 30 seconds, or failed after making the copy: a plan that
                                // appeared meanwhile, the only one, is that copy, recorded so the rollback removes it.
                                List<string> appeared = new List<string>();
                                foreach (string listed in machine.PowerPlans().Keys)
                                {
                                    if (!before.Contains(listed) && !WindowsSettings.IsBuiltInPowerPlan(listed))
                                    {
                                        appeared.Add(listed);
                                    }
                                }

                                created = before.Count > 0 && appeared.Count == 1 ? appeared[0] : null;
                            }
                            else
                            {
                                created = useGuid;
                            }

                            if (u != null && created != null)
                            {
                                u.Set("created", created);
                                UndoLog.Save(undoLog);
                            }
                        }

                        if (problem == null)
                        {
                            problem = machine.ActivatePowerPlan(useGuid);
                        }

                        // The record stays when the switch fails: powercfg may have switched before it failed or
                        // was stopped, and switching back to the plan already active is harmless.
                        report.Add(log, section, problem == null ? Outcomes.Done : Outcomes.Failed, problem ?? (action == "copy" ? "added Windows' " + shown + " and switched to it" : shown));
                    }

                    if (isUndo && node.Get("creating") != null && node.Get("created") == null)
                    {
                        report.Add(log, section, Outcomes.Skipped, "the restore was copying Windows' plan " + node.Get("creating")
                                                                    + " when it stopped, and may have left a copy; powercfg /list shows it, and powercfg /delete removes it");
                    }

                    // Undoing a restore that added a plan: switched back above, so the copy can go.
                    string added = isUndo ? node.Get("created") : null;
                    if (added != null && !dry && (action == "already" || problem == null))
                    {
                        IDictionary<string, string> listed = machine.PowerPlans();
                        string[] active = machine.ActivePowerPlan();
                        string createdProblem = CreatedPlanProblem(added, active == null ? null : active[0]);
                        if (createdProblem != null)
                        {
                            report.Add(log, section, Outcomes.Skipped, "did not remove " + added + ": " + createdProblem);
                        }
                        else if (listed.ContainsKey(added))
                        {
                            string deleteProblem = machine.DeletePowerPlan(added);
                            if (deleteProblem != null)
                            {
                                report.Add(log, section, Outcomes.Failed, "could not remove the plan the restore added: " + deleteProblem);
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
                    HashSet<uint> ids = new HashSet<uint>();
                    foreach (KvNode item in toCheck == null ? new KvNode[0] : (IEnumerable<KvNode>)toCheck.Children)
                    {
                        // One setting under two spellings would be recorded twice, the second time with the backup's value.
                        uint parsed;
                        if (!isUndo && item.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                                    && uint.TryParse(item.Key.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed)
                                    && !ids.Add(parsed))
                        {
                            throw new FormatException("the backup names NVIDIA setting " + item.Key + " twice, so nothing in this part was written");
                        }

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

                    bool mayPass;
                    string why = options.StartNvidia != null ? options.StartNvidia(out mayPass) : machine.StartNvidia(out mayPass);
                    if (why != null)
                    {
                        // No driver or no NVIDIA GPU stays so: a restore has nothing to do here, while an undo keeps
                        // its record for a PC that gets the driver back.
                        report.Add(log, section, !isUndo && !mayPass ? Outcomes.Skipped : Outcomes.Deferred, why);
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
                    // An undo's entries the driver would not take stay in the record for the next run.
                    List<string> kept = new List<string>();
                    using (INvidiaSession drs = machine.OpenNvidia())
                    {
                        IntPtr profile = drs.FindCs2Profile();
                        if (profile == IntPtr.Zero)
                        {
                            report.Add(log, section, Outcomes.Skipped, "this NVIDIA driver has no CS2 profile; update the driver and restore again");
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
                            report.Observed.Add(section + " " + item.Key + " " + (previous == null ? "absent" : previous.Type + " " + previous.Value));
                            string wantedValue = absent ? null : item.Get("value");
                            if ((previous == null ? null : previous.Value) != wantedValue)
                            {
                                report.Settings.Add(new SettingChange(section, item.Get("name") ?? item.Key, previous == null ? null : previous.Value, wantedValue));
                            }
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
                                PassGate(report);
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
                                    if (isUndo)
                                    {
                                        kept.Add(item.Key);
                                    }

                                    if (!reset)
                                    {
                                        continue;
                                    }
                                }
                                catch (FormatException ex)
                                {
                                    refused.Add(item.Key + " (" + ex.Message + ")");
                                    if (isUndo)
                                    {
                                        kept.Add(item.Key);
                                    }

                                    if (!reset)
                                    {
                                        continue;
                                    }
                                }
                            }

                            // The first value recorded for a setting is what the driver had; it is never replaced.
                            if (undoSettings.Child(item.Key) == null)
                            {
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
                            }

                            n++;
                        }

                        if (!dry && n > 0)
                        {
                            // On record before the driver saves: the worker may be cut off at any point, so this order is
                            // what keeps a change from going unrecorded. Taken off again only when the driver refuses the save
                            // for want of rights, which changes nothing. After any other failure the driver may have
                            // kept some of the session, and undoing a value that did not change is harmless.
                            PassGate(report);
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

                    if (isUndo && !dry && kept.Count > 0)
                    {
                        // The record keeps only what is still to be put back, so a rerun writes nothing twice. An
                        // entry that can never be written (not carried, damaged) is let go, and said so.
                        foreach (KvNode item in new List<KvNode>(settings.Children))
                        {
                            if (!kept.Contains(item.Key))
                            {
                                settings.Remove(item.Key);
                            }
                        }

                        try
                        {
                            backup.SaveManifest();
                        }
                        catch (Exception)
                        {
                            // The whole record stays, and putting back a value that is already back is harmless.
                        }

                        report.Add(log, section, Outcomes.Deferred, "put back " + Inv.S(n) + " settings on the CS2 profile; skipped "
                                                                + string.Join(", ", refused.ToArray()) + "; the record keeps " + string.Join(", ", kept.ToArray())
                                                                + " for the next acb undo. If this driver never takes them, an administrator's acb undo --set-aside lets older records go ahead without this one");
                        return;
                    }

                    report.Add(log, section, Outcomes.Done, (dry ? "would change " : "changed ") + Inv.S(n) + " settings on the CS2 profile"
                                                     + (refused.Count > 0 ? "; skipped " + string.Join(", ", refused.ToArray()) : "")
                                                     + (leftToGlobal.Count > 0 ? "; left as this PC's global profile sets them, since the driver reports no default: " + string.Join(", ", leftToGlobal.ToArray()) : ""));
                    return;
                }
                case Sections.Vibrance:
                {
                    // Displays are matched by NVAPI's order, which says nothing about which monitor is which; so a
                    // backup is applied only where that cannot go wrong: one display here, one level in the backup.
                    // The driver is started first, as for the NVIDIA part, so a driver that is updating or recovering
                    // leaves the part for later instead of reading as a PC without NVIDIA displays.
                    bool vibranceMayPass;
                    string notStarted = options.StartNvidia != null ? options.StartNvidia(out vibranceMayPass) : machine.StartNvidia(out vibranceMayPass);
                    if (notStarted != null)
                    {
                        report.Add(log, section, !isUndo && !vibranceMayPass ? Outcomes.Skipped : Outcomes.Deferred, notStarted);
                        return;
                    }

                    IList<int[]> current = machine.ReadVibrance();
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
                            report.Add(log, section, Outcomes.Skipped, shown != 1 || current[0] == null
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
                            report.Add(log, section, isUndo ? Outcomes.Deferred : Outcomes.Skipped, "this PC has no NVIDIA display number " + Inv.S(index + 1)
                                + (isUndo ? " now; turn that monitor on and run this again" : ""));
                            missing++;
                            continue;
                        }

                        if (!dry)
                        {
                            PassGate(report);
                        }

                        report.Settings.Add(new SettingChange(section, "display " + Inv.S(index + 1) + " vibrance", Inv.S(current[index][0]), Inv.S(wanted[i])));

                        // On record before the level changes: the worker may be cut off at any point, so this order is
                        // what keeps a change from going unrecorded.
                        if (u != null)
                        {
                            u.Set(Inv.S(index), Inv.S(current[index][0]));
                            UndoLog.Save(undoLog);
                        }

                        if (!dry)
                        {
                            machine.SetVibrance(index, Math.Max(current[index][1], Math.Min(current[index][2], wanted[i])));
                        }

                        n++;
                    }

                    if (n > 0 || missing == 0)
                    {
                        report.Add(log, section, n > 0 ? Outcomes.Done : Outcomes.Skipped, Inv.S(n) + " displays");
                    }

                    return;
                }
                default:
                    report.Add(log, section, Outcomes.Failed, "this restore has no handler for the part");
                    return;
            }
        }

        /// <summary>
        ///     Applies a part's registry values. A backup's cs2exe values are merged into the target's own (its
        ///     choice added or removed, the rest kept); an undo snapshot holds the target's values as they were
        ///     and writes them back as they are. Every entry is checked before the first is written.
        /// </summary>
        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static int RestoreRegistry(IMachine machine, KvNode node, string cs2Exe, UndoLog undoLog, string section, bool dry, bool isUndo, RunReport report)
        {
            KvNode list = node.Child("registry");
            if (list == null)
            {
                return 0;
            }

            HashSet<string> named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KvNode entry in list.Children)
            {
                string problem = RegistryEntryProblem(entry, isUndo);
                if (problem != null)
                {
                    throw new FormatException(problem);
                }

                // A value named twice would be recorded twice, the second time with the backup's data.
                if (!isUndo && !named.Add(entry.Get("key") + "\\" + entry.Get("name")))
                {
                    throw new FormatException("the backup names " + entry.Get("key") + "\\" + entry.Get("name") + " twice");
                }
            }

            // A key this user may not write stops the part here, before any value is written, as a file does.
            foreach (KvNode entry in list.Children)
            {
                string name = entry.Get("name");
                string valueName = name == RegistryItem.Cs2ExeToken ? cs2Exe : name;
                string blocked = valueName == null ? null : machine.RegistryWriteProblem(entry.Get("key"), valueName);
                if (blocked != null)
                {
                    throw new TargetBlockedException(entry.Get("key") + "\\" + name + " cannot be written (" + blocked + "), so nothing in this part was written", null);
                }
            }

            int n = 0;
            KvNode undoList = undoLog == null ? null : undoLog.Manifest.Section(section).Section("registry");
            // An undo writes its entries newest first, so whatever was recorded first, the value the PC had, is written last.
            List<KvNode> entries = new List<KvNode>(list.Children);
            if (isUndo)
            {
                entries.Reverse();
            }

            foreach (KvNode entry in entries)
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
                bool had = machine.ReadRegistry(subKey, valueName, out oldKind, out oldData);
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

                report.Settings.Add(new SettingChange(section, subKey + "\\" + (name == RegistryItem.Cs2ExeToken ? "cs2.exe" : name),
                    had ? oldData : null, kind == null ? null : data));

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
                    machine.WriteRegistry(subKey, valueName, kind, data);
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

        /// <summary>
        ///     Linux: the carried settings the player's NVIDIA application profiles give the cs2 process, as the driver
        ///     resolves them. Read only; nothing in the player's files is changed.
        /// </summary>
        private static void CaptureLinuxNvidia(IMachine machine, KvNode manifest, RunReport report, TextWriter log)
        {
            string section = Sections.Nvidia;
            string home = machine.HomeFolder();
            if (string.IsNullOrEmpty(home))
            {
                report.Add(log, section, Outcomes.Skipped, "HOME is not set, so the NVIDIA application profiles were not looked for");
                return;
            }

            List<string> notes = new List<string>();
            List<NvProfileSetting> settings = NvidiaProfiles.Cs2Settings(home, notes);
            string noted = notes.Count > 0 ? "; " + string.Join("; ", notes.ToArray()) : "";
            if (settings.Count == 0)
            {
                report.Add(log, section, Outcomes.Skipped, "no NVIDIA application profile gives the cs2 process a carried setting" + noted);
                return;
            }

            KvNode list = manifest.Section(section).Section("linux");
            foreach (NvProfileSetting setting in settings)
            {
                KvNode item = list.Section(setting.Key);
                item.Set("kind", setting.Kind);
                item.Set("value", setting.Value);
            }

            report.Add(log, section, Outcomes.Done, Inv.S(settings.Count) + " settings for the cs2 process from the NVIDIA application profiles" + noted);
        }

        /// <summary>
        ///     Linux: writes the backup's settings as one file of acb's own in ~/.nv/nvidia-application-profiles-rc.d,
        ///     recorded first, so the player's own profile files are never edited and acb undo removes or restores just
        ///     that file. The driver reads the user's rc file before the .d folder, so a rule for cs2 there wins, which
        ///     the result says.
        /// </summary>
        private static void RestoreLinuxNvidia(IMachine machine, KvNode node, IBackupSource backup, bool isUndo, bool dry, UndoLog undoLog,
            RunReport report, TextWriter log)
        {
            string section = Sections.Nvidia;
            string home = machine.HomeFolder();
            if (string.IsNullOrEmpty(home))
            {
                report.Add(log, section, Outcomes.Deferred, "HOME is not set, so the NVIDIA application profiles cannot be found; run this again with HOME set");
                return;
            }

            string target = NvidiaProfiles.OwnFile(home);
            byte[] bytes = null;
            int count = 0;
            if (isUndo)
            {
                KvNode files = node.Child("files");
                string recorded = files == null ? null : files.Get(NvidiaProfiles.OwnFileName);
                if (recorded == null)
                {
                    report.Add(log, section, Outcomes.Skipped, "the record holds nothing for the NVIDIA application profiles");
                    return;
                }

                if (recorded != "absent")
                {
                    bytes = backup.ReadFile(section, NvidiaProfiles.OwnFileName);
                    if (bytes == null || Sha256(bytes) != recorded)
                    {
                        throw new IOException("the record's copy of acb's NVIDIA profile file is missing or damaged, so it was not put back");
                    }

                    if (File.Exists(target) && Sha256(target) == recorded)
                    {
                        report.Add(log, section, Outcomes.Done, "acb's NVIDIA profile file is already as recorded");
                        return;
                    }
                }
                else if (!File.Exists(target))
                {
                    report.Add(log, section, Outcomes.Done, "acb's NVIDIA profile file is already gone");
                    return;
                }
            }
            else
            {
                KvNode list = node.Child("linux");
                if (list == null || list.Children.Count == 0)
                {
                    report.Add(log, section, Outcomes.Skipped, "the backup holds no NVIDIA application profile settings");
                    return;
                }

                List<NvProfileSetting> settings = new List<NvProfileSetting>();
                HashSet<string> named = new HashSet<string>(StringComparer.Ordinal);
                foreach (KvNode item in list.Children)
                {
                    string kind = item.Get("kind") ?? "";
                    string value = item.Get("value") ?? "";
                    string problem = NvidiaProfiles.SettingProblem(item.Key, kind, value);
                    if (problem != null)
                    {
                        throw new FormatException(problem + ", so nothing in this part was written");
                    }

                    if (!named.Add(item.Key))
                    {
                        throw new FormatException("the backup names NVIDIA profile setting " + item.Key + " twice, so nothing in this part was written");
                    }

                    settings.Add(new NvProfileSetting(item.Key, kind, value));
                }

                bytes = NvidiaProfiles.Write(settings);
                count = settings.Count;
                Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (NvProfileSetting setting in NvidiaProfiles.Cs2Settings(home, new List<string>()))
                {
                    now[setting.Key] = setting.Value;
                }

                foreach (NvProfileSetting setting in settings)
                {
                    string had;
                    now.TryGetValue(setting.Key, out had);
                    if (had != setting.Value)
                    {
                        report.Settings.Add(new SettingChange(section, setting.Key, had, setting.Value));
                    }
                }
            }

            string folder = NvidiaProfiles.UserRcD(home);
            if (File.Exists(folder))
            {
                throw new TargetBlockedException(folder + " is a file, so acb cannot add its own profile file beside it; nothing in this part was written", null);
            }

            try
            {
                if (bytes == null)
                {
                    SafeFile.CheckDeletable(target);
                }
                else
                {
                    SafeFile.CheckWritable(target, dry);
                }

                report.Observed.Add(section + " " + target + " " + (File.Exists(target) ? Sha256(target) : "absent"));
            }
            catch (IOException ex)
            {
                throw new TargetBlockedException(NvidiaProfiles.OwnFileName + " cannot be written (" + ex.Message + "), so nothing in this part was written", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new TargetBlockedException(NvidiaProfiles.OwnFileName + " cannot be written (" + ex.Message + "), so nothing in this part was written", ex);
            }

            if (undoLog != null)
            {
                RecordFile(target, undoLog.Dir, section, NvidiaProfiles.OwnFileName, undoLog.Manifest.Section(section).Section("files"));
                UndoLog.Save(undoLog);
            }

            if (!dry)
            {
                if (bytes == null)
                {
                    SafeFile.Delete(target);
                }
                else
                {
                    Directory.CreateDirectory(folder);
                    SafeFile.Write(target, bytes);
                }
            }

            if (isUndo)
            {
                report.Add(log, section, Outcomes.Done, bytes == null ? "removed acb's NVIDIA profile file" : "put back acb's NVIDIA profile file as it was");
                return;
            }

            report.Add(log, section, Outcomes.Done, (dry ? "would write " : "wrote ") + Inv.S(count) + " settings for the cs2 process to " + target
                + (NvidiaProfiles.RcHasCs2Rule(home)
                    ? "; this PC's own nvidia-application-profiles-rc also gives cs2 settings, and the driver reads it first, so its values win where both set one"
                    : ""));
        }

        private static void RecordFile(string target, string undoDir, string section, string relative, KvNode undoFiles)
        {
            // The first record of a file is what the PC had; nothing later in the same restore replaces it.
            if (undoFiles.Child(relative) != null)
            {
                return;
            }

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
