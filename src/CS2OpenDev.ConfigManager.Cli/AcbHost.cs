using System.Runtime.InteropServices;
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

    /// <summary>
    ///     The CS2OpenDev service that signs backups for a Steam account: ACB_IDENTITY_SERVICE, else the one this build
    ///     knows; null when there is neither. Only the one this build knows is signed through.
    /// </summary>
    public string? IdentityService { get; init; }

    /// <summary>
    ///     The signer for a service address, its sign-in words going to the writer given, stopping when the token is
    ///     cancelled. The real one signs in through the browser; tests give a stand-in that signs with test keys.
    /// </summary>
    public Func<string, TextWriter, CancellationToken, Archive.IIdentitySigner> IdentitySigner { get; init; } =
        (service, words, cancel) => new ServiceSigner(service, new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, OpenBrowser, words, cancel: cancel);

    /// <summary>
    ///     Why signing in through a browser cannot work here; null when it can. The sign-in comes back to a port on
    ///     this PC, so a browser on another PC (an SSH session's) or none at all (no desktop) never finishes it.
    /// </summary>
    public string? BrowserSignInProblem { get; init; }

    /// <summary>
    ///     Calls the action on the first Ctrl+C instead of ending acb, until the handle returned is disposed, so a run
    ///     can stop cleanly; a second Ctrl+C ends acb as usual. Tests give a stand-in.
    /// </summary>
    public Func<Action, IDisposable> OnInterrupt { get; init; } = CatchInterrupt;

    /// <summary>acb runs as an administrator: elevated on Windows, root on Linux. Only then may it override a policy that refuses a revoked signature.</summary>
    public Func<bool> IsAdministrator { get; init; } = () => Environment.IsPrivilegedProcess;

    /// <summary>This acb's own program file: what <c>backup --self-contained</c> carries, and what a carried restorer is compared with.</summary>
    public string? Executable { get; init; }

    /// <summary>
    ///     Runs the wizard on the terminal and returns its exit code; null where there is no screen to draw on (tests).
    ///     Given a backup file (one dropped on acb, say), it opens on restoring that backup instead of the home screen.
    /// </summary>
    public Func<AcbHost, string?, int>? Wizard { get; init; }

    /// <summary>
    ///     Where a person's backups usually are, looked in for one to offer when none is named: Documents (where acb
    ///     saves them), Desktop, Downloads and the home folder. Tests give their own.
    /// </summary>
    public Func<IReadOnlyList<string>> BackupFolders { get; init; } = UsualFolders;

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
        IdentityService = Environment.GetEnvironmentVariable("ACB_IDENTITY_SERVICE") is { Length: > 0 } service ? service : IdentityServiceAddress.Current?.Service,
        BrowserSignInProblem = SignInProblem(Environment.GetEnvironmentVariable, OperatingSystem.IsLinux()),
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
        BackupFolders = BackupFolders,
        IsAdministrator = IsAdministrator,
        IdentityService = IdentityService,
        IdentitySigner = IdentitySigner,
        BrowserSignInProblem = BrowserSignInProblem,
        OnInterrupt = OnInterrupt,
        EnsureMachineStore = EnsureMachineStore,
        BeforeRollback = BeforeRollback,
        StartNvidia = StartNvidia,
        DriverTimeoutMs = DriverTimeoutMs
    };

    private static List<string> UsualFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> folders =
            [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)];
        if (home.Length > 0)
        {
            folders.Add(Path.Combine(home, "Downloads"));
            folders.Add(home);
        }

        return [.. folders.Where(f => f.Length > 0).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }

    private static readonly string[] SshVariables = ["SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY"];

    /// <summary>
    ///     Why a browser sign-in cannot come back to acb, from the environment: an SSH session (the browser that
    ///     opens the address is on the other PC), or on Linux no desktop to open a browser on. Null when it can.
    /// </summary>
    internal static string? SignInProblem(Func<string, string?> environment, bool linux)
    {
        if (SshVariables.Any(name => !string.IsNullOrEmpty(environment(name))))
        {
            return "acb is running in an SSH session, and signing in comes back to this PC, so it needs a browser on this PC, not on the one you connect from";
        }

        return linux && string.IsNullOrEmpty(environment("DISPLAY")) && string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"))
            ? "this session has no desktop to open a browser on, and signing in comes back to this PC, so it needs a browser on this PC"
            : null;
    }

    private static IDisposable CatchInterrupt(Action interrupted)
    {
        Func<bool> first = FirstOnly(interrupted);
        return PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = first());
    }

    /// <summary>
    ///     Calls the action on the first Ctrl+C and says to keep acb running; any later one is let through, so a step
    ///     that does not stop on the first (a prompt, a read that hangs) can still be ended.
    /// </summary>
    internal static Func<bool> FirstOnly(Action interrupted)
    {
        int seen = 0;
        return () =>
        {
            if (Interlocked.Increment(ref seen) != 1)
            {
                return false;
            }

            interrupted();
            return true;
        };
    }

    /// <summary>
    ///     Opens a web page in the person's browser; false when none could be started. Only an https page (or http on
    ///     this PC, for a test's stand-in) is opened, so no other kind of address reaches the shell. On Linux xdg-open and
    ///     the browser it starts get no terminal, so nothing they print lands on the screens.
    /// </summary>
    private static bool OpenBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? page) || !(page.Scheme == Uri.UriSchemeHttps || (page.Scheme == Uri.UriSchemeHttp && page.IsLoopback)))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A browser already running takes the page, and then no process is returned; a failure throws.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                return true;
            }

            using System.Diagnostics.Process? started = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("sh", ["-c", "exec xdg-open \"$1\" </dev/null >/dev/null 2>&1", "sh", page.AbsoluteUri])
                {
                    UseShellExecute = false
                });

            // sh always starts; xdg-open failing (missing, or no browser to hand the page to) shows only in its exit code.
            // One still running after a few seconds is taken to have opened a browser it now waits on.
            return started is not null && (!started.WaitForExit(3000) || started.ExitCode == 0);
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
