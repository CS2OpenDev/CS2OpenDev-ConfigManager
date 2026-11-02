using CS2OpenDev.ConfigManager.Cli;

#pragma warning disable CA1031 // The last line of defence: whatever escaped is reported and ends the run with a code, never a crash dialog.
try
{
    if (OperatingSystem.IsWindows())
    {
        NativeLoading.OnlyFromSystem32();
    }

    return Acb.Run(args, AcbHost.Real());
}
catch (Exception ex)
{
    Console.Error.WriteLine("acb stopped unexpectedly: " + ex.Message);
    Console.Error.WriteLine("If it was restoring or undoing, every change it made is on an undo record first: run acb undo --list.");
    return ExitCodes.RecordsNeedAttention;
}
#pragma warning restore CA1031
