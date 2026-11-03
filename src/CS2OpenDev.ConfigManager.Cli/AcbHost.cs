using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>
///     Everything acb reaches outside its own process: the console, the undo stores, running processes, where
///     Steam is, the administrator question and the clock. <see cref="Real" /> is the machine; tests build one
///     around temp folders and stand-ins, so no test needs the real Steam folder or UAC. The registry, power plans
///     and driver are reached through <see cref="Machine" />, which tests set to a fake.
/// </summary>
internal sealed class AcbHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Err { get; init; }

    /// <summary>A line from the person, or null when there is nobody to ask (input ended or redirected).</summary>
    public required Func<string?> ReadLine { get; init; }

    /// <summary>
    ///     Asks for a passphrase without showing it, the question going to standard error; null when there is nobody
    ///     to ask or the input ended.
    /// </summary>
    public Func<string, string?> ReadSecret { get; init; } = _ => null;

    /// <summary>A person is at a terminal to answer questions; false makes every question take its safe default.</summary>
    public required bool Interactive { get; init; }

    /// <summary>The platform as archives name it, or null for one acb restores nothing on.</summary>
    public required string? Platform { get; init; }

    public required string UserStoreRoot { get; init; }

    /// <summary>The administrator-only store, or null where there is none (Linux).</summary>
    public required string? MachineStoreRoot { get; init; }

    /// <summary>The folder the machine store is made under; its own ACL is not relied on.</summary>
    public required string? MachineStoreParent { get; init; }

    public required Func<string, bool> IsRunning { get; init; }

    /// <summary>Finds Steam from a folder, or on the PC when given null.</summary>
    public required Func<string?, SteamInstall?> LocateSteam { get; init; }

    public required Func<ElevationState> Elevation { get; init; }

    public required IElevator Elevator { get; init; }

    /// <summary>The elevated run's way back to the run that started it, by pipe name; null when there is none.</summary>
    public required Func<string, IElevatedChannel?> ConnectElevated { get; init; }

    public required Func<DateTimeOffset> Now { get; init; }

    /// <summary>Creates and checks the machine store before an elevated run writes to it; throws when it may not be used.</summary>
    public required Action<string, string> EnsureMachineStore { get; init; }

    /// <summary>The registry, power plans and driver the engine reads and changes; null is this PC. Tests pass a fake.</summary>
    internal IMachine? Machine { get; init; }

    /// <summary>The CS2OpenDev service that signs backups for a Steam account (ACB_IDENTITY_SERVICE); null when none is set.</summary>
    public string? IdentityService { get; init; }

    /// <summary>
    ///     The signer for a service address, its sign-in words going to the writer given. The real one signs in
    ///     through the browser; tests give a stand-in that signs with test keys.
    /// </summary>
    public Func<string, TextWriter, Archive.IIdentitySigner> IdentitySigner { get; init; } =
        (service, words) => new ServiceSigner(service, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, OpenBrowser, words);

    /// <summary>This acb's own program file: what <c>backup --self-contained</c> carries, and what a carried restorer is compared with.</summary>
    public string? Executable { get; init; }

    /// <summary>Runs the wizard on the terminal and returns its exit code; null where there is no screen to draw on (tests).</summary>
    public Func<AcbHost, int>? Wizard { get; init; }

    /// <summary>Called with the record's folder just before a failed part is rolled back. Tests only.</summary>
    internal Action<string>? BeforeRollback { get; init; }

    /// <summary>Starts NVIDIA's driver API for the engine; null starts the real one. Tests only.</summary>
    internal NvidiaStarter? StartNvidia { get; init; }

    /// <summary>How long the engine waits for an NVIDIA driver part. Tests only.</summary>
    internal int DriverTimeoutMs { get; init; } = ConfigBackup.DriverPartTimeoutMs;

    public UndoStore UserStore => new(UserStoreRoot, isMachine: false);

    public UndoStore? MachineStore => MachineStoreRoot is null ? null : new UndoStore(MachineStoreRoot, isMachine: true);

    public static AcbHost Real() => new()
    {
        Out = Console.Out,
        Err = Console.Error,
        ReadLine = Console.ReadLine,
        ReadSecret = ReadHidden,
        Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected,
        Platform = ArchiveFormat.CurrentPlatform,
        UserStoreRoot = UndoStore.UserRoot(),
        MachineStoreRoot = UndoStore.MachineRoot(),
        MachineStoreParent = UndoStore.MachineParent(),
        IsRunning = SteamInstall.IsRunning,
        LocateSteam = SteamInstall.Locate,
        Elevation = ElevationState.Detect,
        Elevator = OperatingSystem.IsWindows() ? new WindowsElevator() : new NoElevator(),
        ConnectElevated = pipe => OperatingSystem.IsWindows() ? WindowsElevator.Connect(pipe) : null,
        Now = () => DateTimeOffset.Now,
        Executable = Environment.ProcessPath,
        IdentityService = Environment.GetEnvironmentVariable("ACB_IDENTITY_SERVICE") is { Length: > 0 } service ? service : null,
        Wizard = CS2OpenDev.ConfigManager.Cli.Wizard.Tui.Run,
        EnsureMachineStore = (root, parent) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("There is no administrator's undo store on this platform.");
            }

            StoreSecurity.EnsureMachineStore(root, parent);
        }
    };

    /// <summary>This host with its words going to <paramref name="writer" />, for a run inside the wizard, which owns the screen.</summary>
    public AcbHost WithWriter(TextWriter writer) => new()
    {
        Out = writer,
        Err = writer,
        ReadLine = () => null,
        Interactive = false,
        Platform = Platform,
        UserStoreRoot = UserStoreRoot,
        MachineStoreRoot = MachineStoreRoot,
        MachineStoreParent = MachineStoreParent,
        IsRunning = IsRunning,
        LocateSteam = LocateSteam,
        Machine = Machine,
        Elevation = Elevation,
        Elevator = Elevator,
        ConnectElevated = ConnectElevated,
        Now = Now,
        Executable = Executable,
        IdentityService = IdentityService,
        IdentitySigner = IdentitySigner,
        EnsureMachineStore = EnsureMachineStore,
        BeforeRollback = BeforeRollback,
        StartNvidia = StartNvidia,
        DriverTimeoutMs = DriverTimeoutMs
    };

    /// <summary>Opens a web page in the person's browser; false when none could be started.</summary>
    private static bool OpenBrowser(string url)
    {
        try
        {
            using System.Diagnostics.Process? started = OperatingSystem.IsWindows()
                ? System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })
                : System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", [url]) { UseShellExecute = false });
            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Reads a line from the console without echoing it. Escape or Ctrl+C gives null, as an empty answer does.</summary>
    private static string? ReadHidden(string question)
    {
        if (Console.IsInputRedirected)
        {
            return null;
        }

        Console.Error.Write(question);
        System.Text.StringBuilder text = new();
        bool treatControlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        return text.Length == 0 ? null : text.ToString();
                    case ConsoleKey.Escape:
                        return null;
                    case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                        return null;
                    case ConsoleKey.Backspace:
                        if (text.Length > 0)
                        {
                            // A character outside the Basic Multilingual Plane is two UTF-16 units; remove both.
                            int cut = text.Length >= 2 && char.IsLowSurrogate(text[^1]) && char.IsHighSurrogate(text[^2]) ? 2 : 1;
                            text.Length -= cut;
                        }

                        break;
                    default:
                        if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        {
                            text.Append(key.KeyChar);
                        }

                        break;
                }
            }
        }
        finally
        {
            Console.TreatControlCAsInput = treatControlC;
            Console.Error.WriteLine();
        }
    }

    /// <summary>Asks a yes-or-no question whose default is no; anything but y or yes, or nobody to answer, is no.</summary>
    public bool Confirm(string question)
    {
        if (!Interactive)
        {
            return false;
        }

        Out.Write(question + " [y/N] ");
        string? answer = ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Asks for a line; null when there is nobody to answer.</summary>
    public string? Ask(string question)
    {
        if (!Interactive)
        {
            return null;
        }

        Out.Write(question + " ");
        return ReadLine()?.Trim();
    }
}
