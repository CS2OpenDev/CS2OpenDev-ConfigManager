using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>
///     Everything acb reaches outside its own process: the console, the undo stores, running processes, the
///     administrator question and the clock. <see cref="Real" /> is the machine; tests build one around temp
///     folders and stand-ins, so no test needs the real Steam, registry, driver or UAC.
/// </summary>
internal sealed class AcbHost
{
    public required TextWriter Out { get; init; }

    public required TextWriter Err { get; init; }

    /// <summary>A line from the person, or null when there is nobody to ask (input ended or redirected).</summary>
    public required Func<string?> ReadLine { get; init; }

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

    public required Func<ElevationState> Elevation { get; init; }

    public required IElevator Elevator { get; init; }

    public required Func<DateTimeOffset> Now { get; init; }

    /// <summary>Creates and checks the machine store before an elevated run writes to it; throws when it may not be used.</summary>
    public required Action<string, string> EnsureMachineStore { get; init; }

    /// <summary>Called with the record's folder just before a failed part is rolled back. Tests only.</summary>
    internal Action<string>? BeforeRollback { get; init; }

    public UndoStore UserStore => new(UserStoreRoot, isMachine: false);

    public UndoStore? MachineStore => MachineStoreRoot is null ? null : new UndoStore(MachineStoreRoot, isMachine: true);

    public static AcbHost Real() => new()
    {
        Out = Console.Out,
        Err = Console.Error,
        ReadLine = Console.ReadLine,
        Interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected,
        Platform = ArchiveFormat.CurrentPlatform,
        UserStoreRoot = UndoStore.UserRoot(),
        MachineStoreRoot = UndoStore.MachineRoot(),
        MachineStoreParent = UndoStore.MachineParent(),
        IsRunning = SteamInstall.IsRunning,
        Elevation = ElevationState.Detect,
        Elevator = OperatingSystem.IsWindows() ? new WindowsElevator() : new NoElevator(),
        Now = () => DateTimeOffset.Now,
        EnsureMachineStore = (root, parent) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("There is no administrator's undo store on this platform.");
            }

            StoreSecurity.EnsureMachineStore(root, parent);
        }
    };

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
