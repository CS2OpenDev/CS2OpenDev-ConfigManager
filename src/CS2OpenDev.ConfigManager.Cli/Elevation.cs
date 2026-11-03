using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
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

/// <summary>
///     Whether, and how, this run can get an administrator: known before anything is offered, so no prompt is bound
///     to fail. <paramref name="AlwaysElevated" />: an administrator's run on a PC where this account has no other
///     kind (User Account Control off, or the built-in Administrator), so "run acb without administrator" cannot be
///     followed there.
/// </summary>
internal sealed record ElevationState(ElevationKind Kind, string Detail, bool AlwaysElevated = false)
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
            // A full token with no split (type 1) is how every program of the account runs when UAC is off.
            return new ElevationState(ElevationKind.Elevated, "acb runs as an administrator", AlwaysElevated: TokenElevationType() == 1);
        }

        if (TokenElevationType() == 3)
        {
            // The person who confirms is the account that runs acb now, so a folder only they can change is safe.
            using WindowsIdentity self = WindowsIdentity.GetCurrent();
            return ProgramProblem(self.User) ?? new ElevationState(ElevationKind.Consent, "Windows asks you to confirm");
        }

        using RegistryKey? policies = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
        if (policies?.GetValue("EnableLUA") is int lua && lua == 0)
        {
            return new ElevationState(ElevationKind.Unavailable,
                "User Account Control is off on this PC, so Windows cannot ask for an administrator. " + AskAnAdministrator);
        }

        if (policies?.GetValue("ConsentPromptBehaviorUser") is int user && user == 0)
        {
            return new ElevationState(ElevationKind.Unavailable,
                "this PC turns down every request for an administrator from a standard user. " + AskAnAdministrator);
        }

        return ProgramProblem(null) ?? new ElevationState(ElevationKind.Credentials, "Windows asks for an administrator's name and password");
    }

    /// <summary>
    ///     What to do when Windows will not ask. An administrator's acb applies the administrator's parts and those that
    ///     are no one's files, and leaves the Steam folder's to a run without administrator.
    /// </summary>
    private const string AskAnAdministrator = "Ask an administrator to run acb from an administrator's prompt for the NVIDIA settings";

    /// <summary>
    ///     Refuses to offer the prompt when acb's own program could be changed, or a DLL put beside it, by someone
    ///     other than administrators and <paramref name="alsoTrusted" />: Windows would load that code into the
    ///     administrator's run before acb starts.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static ElevationState? ProgramProblem(SecurityIdentifier? alsoTrusted)
    {
        string? exe = Environment.ProcessPath;
        string? problem;
        try
        {
            problem = exe is null ? "acb cannot tell where its own program is" : StoreSecurity.ProgramProblem(exe, alsoTrusted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = "acb cannot check who may change its own program (" + ex.Message + ")";
        }

        return problem is null
            ? null
            : new ElevationState(ElevationKind.Unavailable,
                "Windows is not asked to run this copy of acb as an administrator: " + problem
                + ", so another program could run in its place. Copy acb into a folder only administrators can change, such as C:\\Program Files\\acb, and run it from there");
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

/// <summary>
///     Where acb loads DLLs from at run time: System32 only, never its own folder or the current one. The DLLs it
///     imports are held to the same by the linker's dependent load flag (the project file).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeLoading
{
    private const uint SearchSystem32 = 0x800;

    public static void OnlyFromSystem32() => SetDefaultDllDirectories(SearchSystem32);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint flags);
}

/// <summary>
///     Whether acb's console window is its own: started by double-clicking acb.exe (or dropping a file on it) rather
///     than from a terminal. Windows closes such a window the moment acb ends, so anything acb said is lost unless
///     it waits for the person first.
/// </summary>
internal static class ConsoleWindow
{
    public static bool IsOwnWindow()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        uint[] processes = new uint[2];
        return GetConsoleProcessList(processes, (uint)processes.Length) == 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
}

/// <summary>
///     What came back from an elevated run. <paramref name="Started" />: the administrator's run was started, so
///     without a report what it changed is unknown.
/// </summary>
internal sealed record ElevatedResult(bool Declined, int ExitCode, CommandReport? Report, string? Problem, bool Started = false);

/// <summary>
///     Starts acb again as an administrator, for the sections only an administrator applies. <c>input</c> is what
///     the elevated run reads instead of opening a path the player named: the backup's bytes and the policy's.
/// </summary>
internal interface IElevator
{
    ElevatedResult Run(IReadOnlyList<string> arguments, IReadOnlyList<byte[]?> input);
}

internal sealed class NoElevator : IElevator
{
    public ElevatedResult Run(IReadOnlyList<string> arguments, IReadOnlyList<byte[]?> input) =>
        new(false, ExitCodes.PartsLeft, null, "this platform has no administrator's sections");
}

/// <summary>The elevated run's end of the pipe: what the first run sent it, and where its report goes.</summary>
internal interface IElevatedChannel : IDisposable
{
    /// <summary>The first run's input, in the order it sent it; null when it sent nothing that could be read.</summary>
    IReadOnlyList<byte[]?>? Input { get; }

    void Send(CommandReport report);
}

/// <summary>
///     How the first run's input travels: a count, then each item as a length (-1 for none) and its bytes. Read with
///     a cap, since the elevated run cannot trust the other end of the pipe.
/// </summary>
internal static class ElevatedInput
{
    private const int MaxItems = 4;

    public static byte[] Encode(IReadOnlyList<byte[]?> items)
    {
        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer);
        writer.Write(items.Count);
        foreach (byte[]? item in items)
        {
            writer.Write(item is null ? -1L : item.LongLength);
            if (item is not null)
            {
                writer.Write(item);
            }
        }

        writer.Flush();
        return buffer.ToArray();
    }

    /// <summary>Null when the stream does not hold a whole, well-formed input within the cap.</summary>
    public static List<byte[]?>? Decode(Stream stream)
    {
        try
        {
            using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            int count = reader.ReadInt32();
            if (count is < 0 or > MaxItems)
            {
                return null;
            }

            List<byte[]?> items = [];
            for (int i = 0; i < count; i++)
            {
                long length = reader.ReadInt64();
                if (length is < -1 or > ArchiveFormat.MaxTotalBytes)
                {
                    return null;
                }

                byte[]? item = length < 0 ? null : reader.ReadBytes((int)length);
                if (item is not null && item.Length != length)
                {
                    return null;
                }

                items.Add(item);
            }

            return items;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>
///     Runs this same acb executable through the UAC prompt, which shows its publisher before anyone types a
///     password. The elevated run gets a command line (the manifest digest it must match, the sections, the policy's
///     digest, a pipe name) and, over that pipe, the backup's and the policy's bytes: it opens no path the player
///     named, so it never reaches a share or a link as the administrator. It checks the archive itself and keeps
///     its records in the administrator's store, so an organiser's password works on a player's account. It talks
///     over the pipe anonymously, so the pipe's owner cannot act as the administrator.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsElevator : IElevator
{
    private const int ErrorCancelled = 1223;

    /// <summary>How long the first run waits for the administrator's run, far longer than its driver calls may take.</summary>
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(10);

    public ElevatedResult Run(IReadOnlyList<string> arguments, IReadOnlyList<byte[]?> input)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            return new ElevatedResult(false, ExitCodes.PartsLeft, null, "acb cannot tell where its own program is");
        }

        // Only this user and administrators may open it, so nobody else reads the backup or writes a report.
        PipeSecurity security = new();
        using (WindowsIdentity self = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        string pipeName = "acb-" + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
        Task<byte[]> reading = ExchangeAsync(pipe, ElevatedInput.Encode(input));
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

            if (!process.WaitForExit(RunLimit))
            {
                // An elevated process is not this run's to stop; it may still change the PC.
                return new ElevatedResult(false, -1, null, $"the administrator's run did not finish within {RunLimit.TotalMinutes} minutes and may still be running", Started: true);
            }

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

        return new ElevatedResult(false, code, report, report is null ? "the administrator's run ended without a report" : null, Started: true);
    }

    /// <summary>Sends the input once the elevated run connects, then reads its report until it closes the pipe.</summary>
    private static async Task<byte[]> ExchangeAsync(NamedPipeServerStream pipe, byte[] input)
    {
        try
        {
            await pipe.WaitForConnectionAsync().ConfigureAwait(false);
            await pipe.WriteAsync(input).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
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
    ///     The elevated run's end: connects to the first run's pipe on this PC, anonymously (the pipe's owner, the
    ///     player, cannot act as the administrator), and reads its input. Null when the name is not one acb makes or
    ///     nothing answers.
    /// </summary>
    public static IElevatedChannel? Connect(string pipeName)
    {
        if (pipeName.Length > 64 || !pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return null;
        }

        NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Anonymous);
        try
        {
            pipe.Connect(3000);
            return new Channel(pipe, ElevatedInput.Decode(pipe));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return null;
        }
    }

    private sealed class Channel(NamedPipeClientStream pipe, IReadOnlyList<byte[]?>? input) : IElevatedChannel
    {
        public IReadOnlyList<byte[]?>? Input { get; } = input;

        public void Send(CommandReport report)
        {
            try
            {
                JsonSerializer.Serialize(pipe, report, CliJson.Default.CommandReport);
                pipe.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The report is also on the elevated window; the exit code still reaches the first run.
            }
        }

        public void Dispose() => pipe.Dispose();
    }
}
