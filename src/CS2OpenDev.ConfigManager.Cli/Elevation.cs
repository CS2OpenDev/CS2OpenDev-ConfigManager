using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Kit;
using Microsoft.Win32;

namespace CS2OpenDev.ConfigManager.Cli;

internal enum ElevationKind
{
    /// <summary>The platform has no administrator's sections (Linux).</summary>
    NotNeeded,

    /// <summary>This process already runs as an administrator: it applies those sections itself.</summary>
    Elevated,

    /// <summary>The person is an administrator; Windows asks them to confirm.</summary>
    Consent,

    /// <summary>A standard user; Windows asks for an administrator's name and password, which an organiser can type.</summary>
    Credentials,

    /// <summary>Windows will not ask on this PC, so a prompt would only fail. <see cref="ElevationState.Detail" /> says why.</summary>
    Unavailable
}

/// <summary>Whether, and how, this run can get an administrator: known before anything is offered, so no prompt is bound to fail.</summary>
internal sealed record ElevationState(ElevationKind Kind, string Detail)
{
    public bool CanAsk => Kind is ElevationKind.Consent or ElevationKind.Credentials;

    public static ElevationState Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ElevationState(ElevationKind.NotNeeded, "");
        }

        return DetectWindows();
    }

    [SupportedOSPlatform("windows")]
    private static ElevationState DetectWindows()
    {
        // A filtered administrator token holds Administrators as deny-only, so this is true only once elevated.
        if (WindowsSettings.IsAdministrator())
        {
            return new ElevationState(ElevationKind.Elevated, "acb runs as an administrator");
        }

        if (TokenElevationType() == 3)
        {
            return new ElevationState(ElevationKind.Consent, "Windows asks you to confirm");
        }

        using RegistryKey? policies = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (policies?.GetValue("EnableLUA") is int lua && lua == 0)
        {
            return new ElevationState(ElevationKind.Unavailable,
                "User Account Control is off on this PC, so Windows cannot ask for an administrator. Ask one to run acb as administrator");
        }

        if (policies?.GetValue("ConsentPromptBehaviorUser") is int user && user == 0)
        {
            return new ElevationState(ElevationKind.Unavailable,
                "this PC turns down every request for an administrator from a standard user. Ask an administrator to run acb as administrator");
        }

        return new ElevationState(ElevationKind.Credentials, "Windows asks for an administrator's name and password");
    }

    /// <summary>TOKEN_ELEVATION_TYPE: 1 default (no split token), 2 full, 3 limited (an administrator, not elevated).</summary>
    [SupportedOSPlatform("windows")]
    private static int TokenElevationType()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.Token, 18, out int type, sizeof(int), out _) ? type : 1;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}

/// <summary>What came back from an elevated run.</summary>
internal sealed record ElevatedResult(bool Declined, int ExitCode, CommandReport? Report, string? Problem);

/// <summary>Starts acb again as an administrator, for the sections only an administrator applies.</summary>
internal interface IElevator
{
    ElevatedResult Run(IReadOnlyList<string> arguments);
}

internal sealed class NoElevator : IElevator
{
    public ElevatedResult Run(IReadOnlyList<string> arguments) =>
        new(false, ExitCodes.PartsLeft, null, "this platform has no administrator's sections");
}

/// <summary>
///     Runs this same acb executable through the UAC prompt, which shows its publisher before anyone types a
///     password. The elevated run gets only a command line: the archive's path and the manifest digest it must
///     match, the sections, and a pipe name. It reads nothing else the player wrote, checks the archive itself, and
///     keeps its records in the administrator's store, so an organiser's password works on a player's account.
///     Its report comes back over a pipe that cannot impersonate it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsElevator : IElevator
{
    private const int ErrorCancelled = 1223;

    public ElevatedResult Run(IReadOnlyList<string> arguments)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return new ElevatedResult(false, ExitCodes.PartsLeft, null, "acb cannot tell where its own program is");
        }

        string pipeName = "acb-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task<byte[]> reading = ReadAllAsync(pipe);
        ProcessStartInfo start = new(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipeName);
        int code;
        try
        {
            using Process? process = Process.Start(start);
            if (process is null)
            {
                return new ElevatedResult(false, ExitCodes.PartsLeft, null, "Windows did not start the administrator's run");
            }

            process.WaitForExit();
            code = process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new ElevatedResult(true, ExitCodes.PartsLeft, null, "the Windows prompt was cancelled or refused");
        }
        catch (Win32Exception ex)
        {
            return new ElevatedResult(false, ExitCodes.PartsLeft, null, ex.Message);
        }

        CommandReport? report = null;
        if (reading.Wait(TimeSpan.FromSeconds(5)) && reading.Result.Length > 0)
        {
            try
            {
                report = JsonSerializer.Deserialize(reading.Result, CliJson.Default.CommandReport);
            }
            catch (JsonException)
            {
            }
        }

        return new ElevatedResult(false, code, report, report is null ? "the administrator's run sent no report" : null);
    }

    private static async Task<byte[]> ReadAllAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            using MemoryStream buffer = new();
            await pipe.CopyToAsync(buffer).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>
    ///     Sends the elevated run's report to the run that started it. Anonymous: the pipe's owner, the player, can
    ///     read what is written but cannot act as the administrator who wrote it.
    /// </summary>
    public static void Send(string pipeName, CommandReport report)
    {
        if (pipeName.Length > 64 || !pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return;
        }

        try
        {
            using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Anonymous);
            pipe.Connect(3000);
            JsonSerializer.Serialize(pipe, report, CliJson.Default.CommandReport);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The report is also on the elevated window; the exit code still reaches the first run.
        }
    }
}
