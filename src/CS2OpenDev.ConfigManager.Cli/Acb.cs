namespace CS2OpenDev.ConfigManager.Cli;

/// <summary>The command dispatcher: one entry point for the real console and for tests.</summary>
internal static class Acb
{
    public const string Usage = """
        Usage:
          acb                                              back up, restore or put this PC back on screen (needs a terminal)
          acb verify  <backup.zip> [--policy f] [--json]       check it; say its trust level, when it was made, what it holds
          acb show    <backup.zip> [--section s | --file p]    list it, or print one part or one file
          acb plan    <backup.zip> [options] [--json]          what a restore would change; changes nothing
          acb restore <backup.zip> [options] [--yes] [--elevate]
          acb restore --plan plan.json [--policy f] [--gsi] [--yes] [--elevate]
                                                           apply exactly a reviewed plan, or refuse if the PC changed
          acb restore ... --dry-run                        the same checks as a restore, changing nothing
          acb undo    [--list | --id n | --set-aside n] [--only s] [--yes] [--elevate] [--dry-run]
          acb backup  -o <backup.zip> [--only s] [--account id] [--steam-path p] [--gsi] [--seal | --sign] [--self-contained]

        Options:
          --only a,b         only these sections: cs2, cs2game, launch, input, gamebar, cs2exe, power, nvidia, vibrance
          --account id       the Steam account to restore to (default: the backup's own, once it has signed in here)
          --steam-path p     the Steam folder (default: found; for undo, the one each restore wrote to)
          --policy f         an organiser's policy file, which can only narrow what is restored
          --gsi              backup: carry gamestate_integration_*.cfg; plan and restore: write them too (they send
                             live game data to the address they name, for every account on the PC)
          --yes              do not ask; without a terminal, nothing changes unless this is given
          --elevate          with --yes, let Windows ask for an administrator for the NVIDIA settings
          --seal             backup: seal it with a passphrase, asked for (Enter alone makes one) or from
                             --passphrase-file; acb then asks for it before it checks or restores the backup
          --sign             backup: sign it for your Steam account through the CS2OpenDev service (signs you in
                             with Steam in your browser); anyone can then check offline who made it
          --identity-service u
                             the CS2OpenDev service that signs (default: ACB_IDENTITY_SERVICE)
          --self-contained   backup: carry this acb in the backup as restorer/acb[.exe], so the PC it is restored on
                             needs nothing else; the backup's README says how to run it
          --passphrase-file f
                             the passphrase is the first line of f (- for standard input), not asked for
          --json             print the report as JSON on standard output (words go to standard error)
          --report f         also save the JSON report to f; a plan's report is the plan file (acb plan b.zip
                             --report plan.json)

        Exit codes: 0 done; 1 refused, nothing changed; 2 done with parts left for later; 3 a part failed and
        was put back; 4 undo records need attention; 64 usage.
        """;

    public static int Run(IReadOnlyList<string> args, AcbHost host)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        if (args.Count == 0)
        {
            if (host.Interactive && host.Wizard is { } wizard)
            {
                return wizard(host);
            }

            host.Err.WriteLine("acb on its own opens its screens, which need a terminal. Without one, use the commands:");
            host.Err.WriteLine();
            host.Err.WriteLine(Usage);
            return ExitCodes.Usage;
        }

        string command = args[0];
        List<string> rest = [.. args.Skip(1)];
        try
        {
            switch (command)
            {
                case "verify":
                    return InfoCommands.Verify(rest, host);
                case "show":
                    return InfoCommands.Show(rest, host);
                case "plan":
                    return RestoreCommand.Run(rest, host, dryRun: true, asRestore: false);
                case "restore" when rest.Remove("--dry-run"):
                    return RestoreCommand.Run(rest, host, dryRun: true, asRestore: true);
                case "restore":
                    return RestoreCommand.Run(rest, host, dryRun: false, asRestore: true);
                case "undo":
                    return UndoCommand.Run(rest, host);
                case "backup":
                    return InfoCommands.Backup(rest, host);
                case "__elevated" when rest.Count > 0 && rest[0] == "restore":
                    return RestoreCommand.Elevated(rest[1..], host);
                case "__elevated" when rest.Count > 0 && rest[0] == "undo":
                    return UndoCommand.Elevated(rest[1..], host);
                case "help" or "--help" or "-h" or "/?":
                    host.Out.WriteLine(Usage);
                    return ExitCodes.Done;
                case "--version":
                    host.Out.WriteLine("acb " + Archives.Version());
                    return ExitCodes.Done;
                default:
                    throw new UsageException($"'{command}' is not an acb command.");
            }
        }
        catch (UsageException ex)
        {
            host.Err.WriteLine(ex.Message);
            host.Err.WriteLine();
            host.Err.WriteLine(Usage);
            return ExitCodes.Usage;
        }
    }
}
