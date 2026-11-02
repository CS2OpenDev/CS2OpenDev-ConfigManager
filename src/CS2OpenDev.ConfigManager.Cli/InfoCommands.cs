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
        CommandLine line = CommandLine.Parse(args, "verify", ["json"], ["policy", "report"]);
        Output output = new(host, "verify", line.Flag("json"), line.Value("report"));
        string path = line.Single("a backup file");
        OrganiserPolicy? policy = Archives.Policy(line.Value("policy"), out string? policyProblem);
        if (policyProblem is not null)
        {
            return output.Refuse(policyProblem);
        }

        VerifiedArchive? archive = Archives.Open(path, null, policy?.MinimumLevel ?? TrustLevel.Hash, out string? problem);
        if (archive is null)
        {
            return output.Refuse(problem!);
        }

        Archives.Header(output, path, archive);
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
        string state = archive.Level == TrustLevel.Hash
            ? "Its files match its manifest, so nothing was damaged or changed by accident. It is unsigned: anyone could have edited it, the account named above included."
            : "Unchanged since it was made.";
        output.Note(archive.Manifest.Platform == host.Platform
            ? $"{state} acb restores it on this PC."
            : $"{state} It restores only on {archive.Manifest.Platform}, and this PC is {host.Platform ?? "neither Windows x64 nor Linux x64"}.");
        return output.Finish(ExitCodes.Done);
    }

    /// <summary>Lists what an archive holds, or prints one section's capture record or one file, after checking it.</summary>
    public static int Show(IReadOnlyList<string> args, AcbHost host)
    {
        CommandLine line = CommandLine.Parse(args, "show", ["json"], ["section", "file", "report"]);
        Output output = new(host, "show", line.Flag("json"), line.Value("report"));
        string path = line.Single("a backup file");
        VerifiedArchive? archive = Archives.Open(path, null, TrustLevel.Hash, out string? problem);
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

        Archives.Header(output, path, archive);
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
        CommandLine line = CommandLine.Parse(args, "backup", ["gsi", "json"], ["output", "only", "account", "steam-path", "report"]);
        line.NoPositionals();
        Output output = new(host, "backup", line.Flag("json"), line.Value("report"));
        string zip = line.Value("output") ?? throw new UsageException("acb backup needs -o <file> for the backup it writes.");
        List<string>? only = Archives.Sections(line.List("only"));
        if (host.Platform is null)
        {
            return output.Refuse("backups are made on Windows x64 and Linux x64 only.");
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

        if (File.Exists(zip))
        {
            return output.Refuse($"{zip} already exists; a backup never replaces a file.");
        }

        RunReport report;
        try
        {
            report = BackupBuilder.Create(options, zip, new ArchiveTool("Advanced Config Backup (acb)", Archives.Version()), host.Now(), CancellationToken.None);
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
        output.Note("Saved " + output.Report.Archive + (report.Count(Outcomes.Failed) > 0 ? ", without the parts that failed above." : "."));
        return output.Finish(report.Count(Outcomes.Failed) > 0 ? ExitCodes.PartsLeft : ExitCodes.Done);
    }
}
