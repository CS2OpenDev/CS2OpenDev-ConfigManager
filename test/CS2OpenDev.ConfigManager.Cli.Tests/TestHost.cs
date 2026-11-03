using System.IO.Compression;
using System.Text;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>An elevated run that never happens: it records what it was asked and answers as told.</summary>
internal sealed class FakeElevator : IElevator
{
    public List<IReadOnlyList<string>> Calls { get; } = [];

    /// <summary>What each call would have sent the elevated run over its pipe.</summary>
    public List<IReadOnlyList<byte[]?>> Inputs { get; } = [];

    public ElevatedResult Answer { get; set; } = new(Declined: true, ExitCodes.PartsLeft, null, "the Windows prompt was cancelled or refused");

    public ElevatedResult Run(IReadOnlyList<string> arguments, IReadOnlyList<byte[]?> input)
    {
        Calls.Add(arguments);
        Inputs.Add(input);
        return Answer;
    }
}

/// <summary>The elevated run's pipe in a test: the input it is given, and the reports it sends back.</summary>
internal sealed class FakeChannel(IReadOnlyList<byte[]?>? input) : IElevatedChannel
{
    public IReadOnlyList<byte[]?>? Input { get; } = input;

    public List<CommandReport> Sent { get; } = [];

    public void Send(CommandReport report) => Sent.Add(report);

    public void Dispose()
    {
    }
}

/// <summary>
///     acb's surroundings for one test: temp undo stores, a scripted person at a terminal (or nobody), which
///     processes run, how the administrator question goes, and a UAC prompt that is only recorded.
/// </summary>
internal sealed class TestHost : IDisposable
{
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public TempDir Temp { get; } = new();

    public Queue<string> Answers { get; } = new();

    /// <summary>What the person types at hidden prompts (passphrases), in order; the prompts asked are kept in <see cref="SecretPrompts" />.</summary>
    public Queue<string> Secrets { get; } = new();

    public List<string> SecretPrompts { get; } = [];

    public bool Interactive { get; set; }

    public HashSet<string> Running { get; } = new(StringComparer.Ordinal);

    public ElevationState Elevation { get; set; } = OperatingSystem.IsWindows()
        ? new ElevationState(ElevationKind.Credentials, "Windows asks for an administrator's name and password")
        : new ElevationState(ElevationKind.NotNeeded, "");

    public FakeElevator Elevator { get; } = new();

    /// <summary>What an elevated run started with --pipe reads from the first run; null when nothing answers.</summary>
    public IReadOnlyList<byte[]?>? ElevatedInput { get; set; }

    /// <summary>The channels elevated runs opened, by pipe name.</summary>
    public List<(string Pipe, FakeChannel Channel)> Channels { get; } = [];

    /// <summary>Thrown by the machine store check, standing in for a folder a player made first.</summary>
    public Exception? MachineStoreProblem { get; set; }

    public List<string> MachineStoreChecks { get; } = [];

    public Action<string>? BeforeRollback { get; set; }

    /// <summary>
    ///     Stands in for starting NVIDIA's driver API, for a test of a driver that fails or hangs; null leaves it to
    ///     <see cref="Machine" />, the fake, so no test depends on this PC's GPU.
    /// </summary>
    public NvidiaStarter? StartNvidia { get; set; }

    /// <summary>How long the engine waits for an NVIDIA driver part.</summary>
    public int DriverTimeoutMs { get; set; } = ConfigBackup.DriverPartTimeoutMs;

    public string UserStore => Temp.Sub("user-store");

    public string MachineParent => Temp.Sub("ProgramData");

    public string MachineStore => Path.Combine(MachineParent, "CS2OpenDev", "ConfigBackup", "undo");

    public string Output => _out.ToString();

    public string Errors => _err.ToString();

    /// <summary>Everything acb wrote, for a failure message.</summary>
    public string All => _out + Environment.NewLine + _err;

    /// <summary>The program file acb takes for its own (what --self-contained carries); null for none.</summary>
    public string? Executable { get; set; }

    /// <summary>Stands in for the wizard when acb is run with no command at a terminal.</summary>
    public Func<AcbHost, int>? Wizard { get; set; }

    /// <summary>The registry, power plans and driver acb sees: in memory, never this PC's. Null leaves the engine's default, which refuses this PC in a test.</summary>
    public FakeMachine? Machine { get; set; } = new();

    public int Run(params string[] args) => Acb.Run(args, Host());

    /// <summary>The host acb runs with: this test's console, stores, stand-ins and fake Steam.</summary>
    public AcbHost Host()
    {
        return new AcbHost
        {
            Out = _out,
            Err = _err,
            ReadLine = () => Answers.Count > 0 ? Answers.Dequeue() : null,
            ReadSecret = prompt =>
            {
                SecretPrompts.Add(prompt);
                return Secrets.Count > 0 ? Secrets.Dequeue() : null;
            },
            Interactive = Interactive,
            Platform = ArchiveFormat.CurrentPlatform,
            UserStoreRoot = UserStore,
            MachineStoreRoot = MachineStore,
            MachineStoreParent = MachineParent,
            IsRunning = Running.Contains,
            // A test that names no Steam folder fails here rather than reaching this PC's own Steam.
            LocateSteam = root => root is null
                ? throw new InvalidOperationException("acb looked for this PC's own Steam; a test names its fake Steam folder.")
                : SteamInstall.Locate(root),
            Elevation = () => Elevation,
            Elevator = Elevator,
            ConnectElevated = pipe =>
            {
                FakeChannel channel = new(ElevatedInput);
                Channels.Add((pipe, channel));
                return channel;
            },
            Now = () => new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero),
            EnsureMachineStore = (root, parent) =>
            {
                MachineStoreChecks.Add(root);
                if (MachineStoreProblem is not null)
                {
                    throw MachineStoreProblem;
                }

                Directory.CreateDirectory(root);
            },
            BeforeRollback = BeforeRollback,
            StartNvidia = StartNvidia,
            DriverTimeoutMs = DriverTimeoutMs,
            Wizard = Wizard,
            Executable = Executable,
            Machine = Machine
        };
    }

    /// <summary>The undo records waiting in the player's store.</summary>
    public IReadOnlyList<UndoRecord> Records() => new UndoStore(UserStore, isMachine: false).Records();

    public void Dispose() => Temp.Dispose();
}

/// <summary>Archives for the tests: captured from a fake Steam, or written by hand to say what a test needs.</summary>
internal static class TestArchives
{
    public static readonly string[] FileSections = [Sections.Cs2, Sections.Cs2Game, Sections.Launch];

    private static int _count;

    /// <summary>A v2 archive of <paramref name="steam" />'s file sections, as acb backup makes it.</summary>
    public static string FromSteam(FakeSteam steam, TempDir temp, params string[] sections)
    {
        string zip = temp.Sub($"backup-{Interlocked.Increment(ref _count)}.zip");
        CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test" };
        options.Include.Clear();
        options.Include.AddRange(sections.Length == 0 ? FileSections : sections);
        RunReport report = BackupBuilder.Create(options, zip, new ArchiveTool("Advanced Config Backup", "test"), DateTimeOffset.UtcNow, CancellationToken.None);
        return BackupBuilder.HoldsAnything(report) ? zip : throw new InvalidOperationException(string.Join("; ", report.Results));
    }

    /// <summary>
    ///     A v2 archive written by hand: <paramref name="capture" /> as capture.vdf, and each file under files/ with
    ///     its SHA-256 checked by the engine against capture.vdf. Every entry matches the manifest, so the reader
    ///     accepts it; what it asks for is up to the restorer's rules.
    /// </summary>
    public static string Craft(TempDir temp, string capture, IReadOnlyDictionary<string, string>? files = null, string? platform = null, SealKey? seal = null)
    {
        string staging = temp.Sub($"staging-{Interlocked.Increment(ref _count)}");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, ArchiveFormat.CaptureEntry), capture, new UTF8Encoding(false));
        foreach ((string path, string text) in files ?? new Dictionary<string, string>())
        {
            string full = ArchivePaths.Under(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }

        string zip = temp.Sub($"crafted-{_count}.zip");
        ArchiveWriter.Write(staging, zip, new ArchiveContent(platform ?? ArchiveFormat.CurrentPlatform!, new ArchiveTool("hand-made", "test"),
            FakeSteam.AccountId, [], "test\n"), DateTimeOffset.UtcNow, CancellationToken.None, seal);
        return zip;
    }

    /// <summary>The SHA-256 the capture record gives a file of this text.</summary>
    public static string Sha(string text) => ConfigBackup.Sha256(new UTF8Encoding(false).GetBytes(text));

    /// <summary>A copy of <paramref name="zip" /> with one entry's bytes changed by <paramref name="change" />.</summary>
    public static string Rewrite(string zip, TempDir temp, string name, Func<string, string> change)
    {
        string copy = temp.Sub($"rewritten-{Interlocked.Increment(ref _count)}.zip");
        using ZipArchive source = ZipFile.OpenRead(zip);
        using ZipArchive target = ZipFile.Open(copy, ZipArchiveMode.Create);
        foreach (ZipArchiveEntry entry in source.Entries)
        {
            using StreamReader reader = new(entry.Open());
            string text = reader.ReadToEnd();
            using StreamWriter writer = new(target.CreateEntry(entry.FullName).Open(), new UTF8Encoding(false));
            writer.Write(entry.FullName == name ? change(text) : text);
        }

        return copy;
    }
}
