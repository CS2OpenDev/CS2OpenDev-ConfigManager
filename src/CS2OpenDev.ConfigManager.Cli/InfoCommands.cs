using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Cli;

/// <summary><c>acb verify</c>, <c>acb show</c> and <c>acb backup</c>: the commands that change nothing on the PC but a new archive.</summary>
internal static class InfoCommands
{
    /// <summary>Checks an archive and says what it is. Any platform: checking a backup is not restoring it.</summary>
    public static int Verify(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "verify", ["json"], ["policy", "report", "passphrase-file"]);
        Output output = new(host, "verify", line.Flag("json"), line.Value("report"));
        string path = line.Single("a backup file");
        OrganiserPolicy? policy = Archives.Policy(line.Value("policy"), out string? policyProblem);
        if (policyProblem is not null)
        {
            return output.Refuse(policyProblem);
        }

        VerifiedArchive? archive = Archives.Open(path, null, policy?.MinimumLevel ?? TrustLevel.Hash, out string? problem, Archives.Passphrase(line, host));
        if (archive is null)
        {
            return output.Refuse(problem!);
        }

        Archives.Header(output, path, archive, host);
        if (policy?.ArchiveProblem(archive) is { } refusal)
        {
            return output.Refuse(refusal);
        }

        output.Line($"Files:    {archive.Manifest.Entries.Count}, each matching manifest.json, and nothing added or missing");
        output.Line("Sections:");
        foreach (ArchiveSection section in archive.Manifest.Sections)
        {
            string notes = section.Notes is { Count: > 0 } list ? " (" + string.Join("; ", list) + ")" : "";
            output.Line($"  {section.Id,-9} {section.Label}{notes}");
        }

        output.Line();
        // A hash-level backup proves only that its files match its manifest: anyone can rewrite both.
        string state = archive.Level switch
        {
            TrustLevel.Hash => "Its files match its manifest, so nothing was damaged or changed by accident. It is unsigned: anyone could have edited it, the account named above included.",
            TrustLevel.Seal => "The passphrase opens its seal, so it was made by someone who knows the passphrase and is unchanged since. "
                               + "Anyone who knows or guesses the passphrase could have made it, the account named above included.",
            _ => $"It is signed through the CS2OpenDev service for Steam account {archive.Signer}, and unchanged since. Whoever can sign in as that account could have made it."
        };
        output.Note(archive.Manifest.Platform == host.Platform
            ? $"{state} acb restores it on this PC."
            : $"{state} It restores only on {archive.Manifest.Platform}, and this PC is {host.Platform ?? "neither Windows x64 nor Linux x64"}.");
        return output.Finish(ExitCodes.Done);
    }

    /// <summary>Lists what an archive holds, or prints one section's capture record or one file, after checking it.</summary>
    public static int Show(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "show", ["json"], ["section", "file", "report", "passphrase-file"]);
        Output output = new(host, "show", line.Flag("json"), line.Value("report"));
        string path = line.Single("a backup file");
        VerifiedArchive? archive = Archives.Open(path, null, TrustLevel.Hash, out string? problem, Archives.Passphrase(line, host));
        if (archive is null)
        {
            return output.Refuse(problem!);
        }

        ArchiveSource source;
        try
        {
            source = new ArchiveSource(archive);
        }
        catch (ArchiveRejectedException ex)
        {
            return output.Refuse(ex.Message);
        }

        if (line.Value("file") is { } file)
        {
            string entry = file.StartsWith("files/", StringComparison.Ordinal) || file == ArchiveFormat.CaptureEntry ? file : "files/" + file;
            if (!archive.Contains(entry))
            {
                return output.Refuse($"the backup holds no {entry}. acb show {path} lists its files.");
            }

            output.Text.Write(Output.Printable(ConfigBackup.Text(archive.Read(entry).ToArray()), keepLines: true));
            return output.Finish(ExitCodes.Done);
        }

        if (line.Value("section") is { } sectionId)
        {
            if (source.Manifest.Child(sectionId) is not { } node)
            {
                return output.Refuse($"the backup holds no section '{sectionId}'.");
            }

            output.Line($"{Sections.Label(sectionId)}, as capture.vdf records it:");
            output.Text.Write(Output.Printable(node.ToText(), keepLines: true));
            return output.Finish(ExitCodes.Done);
        }

        Archives.Header(output, path, archive, host);
        foreach (string section in Sections.All)
        {
            if (source.Manifest.Child(section) is not { } node)
            {
                continue;
            }

            output.Line();
            output.Line($"{section}: {Sections.Label(section)}");
            if (node.Child("files") is { } files)
            {
                foreach (KvNode f in files.Children)
                {
                    output.Line("  " + (f.Value == "absent" ? f.Key + " (removed)" : "files/" + section + "/" + f.Key));
                }
            }
            else
            {
                output.Line($"  {Describe(node)}; acb show {path} --section {section} prints them");
            }
        }

        return output.Finish(ExitCodes.Done);
    }

    /// <summary>
    ///     Asks for a new passphrase twice, at a terminal only. Enter alone offers a generated phrase of six words,
    ///     shown once for the person to write down. Null when nobody can be asked or the person gave none.
    /// </summary>
    private static string? AskNewPassphrase(AcbHost host, Output output)
    {
        if (!host.Interactive)
        {
            return null;
        }

        output.Line($"A seal needs a passphrase of at least {Seal.MinimumLength} characters. Anyone holding the backup can try passphrases");
        output.Line("against it, so a long one matters; Enter alone makes one of six random words.");
        string? passphrase = host.ReadSecret("Passphrase: ");
        if (passphrase is null)
        {
            string generated = Seal.GeneratePhrase();
            output.Line();
            output.Line("Your passphrase (write it down; it is not kept anywhere and is shown only now):");
            output.Line();
            output.Line("    " + generated);
            output.Line();
            return host.Confirm("Seal the backup with it?") ? generated : null;
        }

        if (Seal.PassphraseProblem(passphrase) is null && host.ReadSecret("Again, to be sure: ") != passphrase)
        {
            output.Note("The two passphrases differ.");
            return null;
        }

        return passphrase;
    }

    /// <summary>Why a backup cannot be signed for a Steam account here; null when it can.</summary>
    internal static string? SignProblem(string? service)
    {
        if (service is null)
        {
            return "signing for a Steam account needs the CS2OpenDev service: give --identity-service <url>, or set ACB_IDENTITY_SERVICE.";
        }

        if (ServiceSigner.Problem(service) is { } bad)
        {
            return bad + ".";
        }

        return IdentityTrust.Current.Roots.Count == 0
            ? "this acb carries no root keys for the identity level yet, so it could not check the signature the service returns."
            : null;
    }

    private static string Describe(KvNode node)
    {
        int values = 0;
        Count(node, ref values);
        return values == 1 ? "1 value" : values + " values";

        static void Count(KvNode n, ref int count)
        {
            foreach (KvNode child in n.Children)
            {
                if (child.IsSection)
                {
                    Count(child, ref count);
                }
                else
                {
                    count++;
                }
            }
        }
    }

    /// <summary>Makes a v2 archive of this PC's settings, as the DemoViewer.NET plugin does. Read only, apart from the archive.</summary>
    public static int Backup(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "backup", ["gsi", "json", "seal", "sign", "self-contained"], ["output", "only", "account", "steam-path", "report", "passphrase-file", "identity-service"]);
        line.NoPositionals();
        Output output = new(host, "backup", line.Flag("json"), line.Value("report"));
        string zip = line.Value("output") ?? throw new UsageException("acb backup needs -o <file> for the backup it writes.");
        List<string>? only = Archives.Sections(line.List("only"));
        if (line.Value("passphrase-file") is not null && !line.Flag("seal"))
        {
            throw new UsageException("--passphrase-file seals the backup only with --seal.");
        }

        if (line.Flag("seal") && line.Flag("sign"))
        {
            throw new UsageException("A backup is sealed with a passphrase or signed for a Steam account, not both: give --seal or --sign.");
        }

        if (host.Platform is null)
        {
            return output.Refuse("backups are made on Windows x64 and Linux x64 only.");
        }

        if (File.Exists(zip))
        {
            return output.Refuse($"{zip} already exists; a backup never replaces a file.");
        }

        byte[]? restorer = null;
        if (line.Flag("self-contained"))
        {
            if (Restorers.Problem(host.Executable, host.Platform) is { } cannot)
            {
                return output.Refuse("--self-contained carries this acb into the backup, and " + cannot + ".");
            }

            try
            {
                restorer = File.ReadAllBytes(host.Executable!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return output.Refuse($"acb cannot read its own program file to carry it: {ex.Message}");
            }
        }

        IIdentitySigner? identity = null;
        if (line.Flag("sign"))
        {
            if (SignProblem(line.Value("identity-service") ?? host.IdentityService) is { } cannot)
            {
                return output.Refuse(cannot);
            }

            identity = host.IdentitySigner((line.Value("identity-service") ?? host.IdentityService)!, host.Err);
        }

        SealKey? seal = null;
        if (line.Flag("seal"))
        {
            string? passphrase = line.Value("passphrase-file") is { } file ? Archives.PassphraseFile(file, host) : AskNewPassphrase(host, output);
            if (passphrase is null)
            {
                return output.Refuse(host.Interactive
                    ? "no passphrase was given, so no backup was written."
                    : "--seal needs a passphrase: run at a terminal to be asked for one, or give --passphrase-file.");
            }

            if (Seal.PassphraseProblem(passphrase) is { } weak)
            {
                return output.Refuse(weak + ", so no backup was written.");
            }

            if (Seal.MayBeTypedDifferently(passphrase))
            {
                output.Note("The passphrase holds characters beyond plain ASCII letters, digits and punctuation. Some keyboards and systems type an "
                            + "accented letter another way, and a passphrase typed another way does not open the seal, so check it opens before you rely on it.");
            }

            seal = SealKey.Create(passphrase);
        }

        CaptureOptions options = new()
        {
            SteamRoot = line.Value("steam-path"),
            AccountId = line.Value("account"),
            IncludeGameStateIntegration = line.Flag("gsi"),
            Creator = "Advanced Config Backup (acb) " + Archives.Version(),
            Machine = host.Machine
        };
        if (only is not null)
        {
            options.Include.Clear();
            options.Include.AddRange(only);
        }

        RunReport report;
        try
        {
            report = BackupBuilder.Create(options, zip, new ArchiveTool("Advanced Config Backup (acb)", Archives.Version()), host.Now(), CancellationToken.None, seal, restorer, identity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return output.Refuse($"the backup could not be written: {ex.Message}");
        }

        output.Line("Saving:");
        foreach (SectionResult result in report.Results)
        {
            output.Result(result);
        }

        if (!BackupBuilder.HoldsAnything(report))
        {
            output.Note("Nothing was saved, so no backup was written. The reasons are above.");
            return output.Finish(ExitCodes.Refused);
        }

        output.Report.Archive = Path.GetFullPath(zip);
        output.Report.Level = ArchiveReader.Name(identity is not null ? TrustLevel.Identity : seal is null ? TrustLevel.Hash : TrustLevel.Seal);
        output.Note("Saved " + output.Report.Archive + (report.Count(Outcomes.Failed) > 0 ? ", without the parts that failed above." : "."));
        if (seal is not null)
        {
            output.Note("It is sealed: acb asks for the passphrase before it checks or restores it. Without the passphrase it cannot be opened.");
        }

        if (identity is not null && ArchiveReader.Open(zip, new ArchiveReadOptions { RequirePlatform = null }).IdentitySigner is { } signer)
        {
            output.Report.SignedBy = signer.SteamId64;
            output.Report.SignedAt = signer.SignedAt;
            output.Note($"It is signed for Steam account {signer.SteamId64}: anyone can check offline that this account made it and that it is unchanged since.");
        }

        if (restorer is not null)
        {
            output.Note($"It carries this acb as {ArchiveFormat.RestorerEntry(host.Platform)}: unpacked beside the backup, it finds the backup and restores it. Its README says how.");
        }

        return output.Finish(report.Count(Outcomes.Failed) > 0 ? ExitCodes.PartsLeft : ExitCodes.Done);
    }
}
