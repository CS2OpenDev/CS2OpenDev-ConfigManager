using Terminal.Gui.App;

namespace CS2OpenDev.ConfigManager.Cli.Wizard;

/// <summary>
///     acb with no command at a terminal: the home screen, and from it backing up, restoring or putting the PC back,
///     each on the task screen, returning home when it ends.
/// </summary>
internal static class Tui
{
    /// <summary>
    ///     Runs until the person quits. The exit code is the last task's that did something, as the command would
    ///     have returned; refused when none did.
    /// </summary>
    public static int Run(AcbHost host, string? backup)
    {
        using IApplication app = Application.Create();
        app.Init();
        int exitCode = ExitCodes.Refused;
        if (backup is not null)
        {
            (int? code, bool quit) = Restore(app, host, backup);
            exitCode = code ?? exitCode;
            if (quit)
            {
                return exitCode;
            }
        }

        while (true)
        {
            HomeChoice choice;
            using (Home home = new(PcStatus.Read(host)))
            {
                app.Run(home.Window);
                choice = home.Choice;
            }

            (int? code, bool quit) = choice switch
            {
                HomeChoice.Backup => Backup(app, host),
                HomeChoice.Restore => Restore(app, host, null),
                HomeChoice.Undo => Undo(app, host),
                _ => (null, true)
            };
            exitCode = code ?? exitCode;
            if (quit)
            {
                return exitCode;
            }
        }
    }

    private static (int?, bool) Backup(IApplication app, AcbHost host)
    {
        BackupFlow flow = new(host);
        using BackupScreen screen = new(flow);
        app.Run(screen.Shell.Window);
        return (flow.Saved ? flow.ExitCode : null, screen.Shell.Quit);
    }

    private static (int?, bool) Restore(IApplication app, AcbHost host, string? backup)
    {
        RestoreFlow flow = new(host, backup);
        using RestoreScreen screen = new(flow);
        app.Run(screen.Shell.Window);
        return (flow.Applied ? flow.ExitCode : null, screen.Shell.Quit);
    }

    private static (int?, bool) Undo(IApplication app, AcbHost host)
    {
        UndoFlow flow = new(host);
        using UndoScreen screen = new(flow);
        app.Run(screen.Shell.Window);
        return (flow.Applied ? flow.ExitCode : null, screen.Shell.Quit);
    }
}
