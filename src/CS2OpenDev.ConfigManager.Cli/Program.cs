using CS2OpenDev.ConfigManager.Cli;

#pragma warning disable CA1031 // The last line of defence: whatever escaped is reported and ends the run with a code, never a crash dialog.
try
{
    if (OperatingSystem.IsWindows())
    {
        NativeLoading.OnlyFromSystem32();
    }

    int exit = Acb.Run(args, AcbHost.Real());
    KeepWindowOpen(args);
    return exit;
}
catch (Exception ex)
{
    Console.Error.WriteLine("acb stopped unexpectedly: " + ex.Message);
    Console.Error.WriteLine("If it was restoring or undoing, every change it made is on an undo record first: run acb undo --list.");
    KeepWindowOpen(args);
    return ExitCodes.RecordsNeedAttention;
}
#pragma warning restore CA1031

// Double-clicked, acb's window closes as soon as it ends; when it printed words rather than showing its screens
// (which the person quits themselves), wait so they can be read. An elevated run is never waited on.
static void KeepWindowOpen(string[] args)
{
    if (Acb.ScreensShown || (args.Length > 0 && args[0] == "__elevated") || !ConsoleWindow.IsOwnWindow())
    {
        return;
    }

    Console.Error.WriteLine();
    Console.Error.Write("Press Enter to close this window.");
    Console.ReadLine();
}
