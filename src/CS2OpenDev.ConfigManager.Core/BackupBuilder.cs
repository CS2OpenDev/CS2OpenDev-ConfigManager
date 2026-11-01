using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;
using KitBackup = CS2OpenDev.ConfigManager.Kit.ConfigBackup;

namespace CS2OpenDev.ConfigManager;

/// <summary>Captures this PC's settings and writes them as a v2 archive.</summary>
public static class BackupBuilder
{
    /// <summary>The zip's name: the date and time only, so a copy left on another PC does not name this one.</summary>
    public static string DefaultFileName(DateTime now) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"AdvancedConfigBackup-{now:yyyyMMdd-HHmmss}.zip");

    /// <summary>Whether a capture saved any part; without one no archive is written.</summary>
    public static bool HoldsAnything(RunReport report) => report is not null && report.Count("done") > 0;

    /// <summary>
    ///     Captures into a private temp folder, then writes <paramref name="zipPath" /> from only what the capture
    ///     record lists, so the copies of a part that failed never reach the archive. No archive is written when no
    ///     part was captured; the report says why.
    /// </summary>
    public static RunReport Create(CaptureOptions options, string zipPath, ArchiveTool tool, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        string platform = ArchiveFormat.CurrentPlatform
                          ?? throw new PlatformNotSupportedException("Backups are made on Windows x64 and Linux x64 only.");
        string work = Path.Combine(Path.GetTempPath(), "acb-" + Guid.NewGuid().ToString("N"));
        string capture = Path.Combine(work, "capture");
        string pack = Path.Combine(work, "pack");
        options.Cancellation = ct;
        try
        {
            RunReport report = KitBackup.Capture(options, capture, null);
            ct.ThrowIfCancellationRequested();
            if (!HoldsAnything(report))
            {
                return report;
            }

            KvNode record = KitBackup.ReadManifest(capture);
            Directory.CreateDirectory(pack);
            File.Copy(Path.Combine(capture, KitBackup.ManifestName), Path.Combine(pack, ArchiveFormat.CaptureEntry));
            foreach (string section in Sections.All)
            {
                KvNode? files = record.Path(section, "files");
                if (files == null)
                {
                    continue;
                }

                foreach (KvNode file in files.Children)
                {
                    string relative = "files/" + section + "/" + KitBackup.SafeRelative(file.Key);
                    string target = ArchivePaths.Under(pack, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(ArchivePaths.Under(capture, relative), target);
                }
            }

            string? account = record.Path(Sections.Cs2, "account")?.Value ?? record.Path(Sections.Launch, "account")?.Value;
            List<ArchiveSection> sections = [.. report.Results.Where(r => r.Outcome == "done")
                .Select(r => new ArchiveSection(r.Section, Sections.Label(r.Section), string.IsNullOrEmpty(r.Detail) ? null : [r.Detail]))];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath))!);
            ArchiveWriter.Write(pack, zipPath, new ArchiveContent(platform, tool, account, sections, Readme(platform)), now, ct);
            return report;
        }
        finally
        {
            DeleteQuietly(work);
        }
    }

    internal static string Readme(string platform) =>
        $"""
         Advanced Config Backup
         ======================

         CS2 settings, and the {platform} settings that go with them, saved by Advanced Config Backup.

         Everything in here is plain text you can open and read. manifest.json lists every file with its
         SHA-256, and signature.dsse.json names manifest.json's own SHA-256. If any file is changed, added or
         removed, the restorer refuses the whole backup: make a new one instead of editing this one.

         To check or restore it, use the restorer, acb:

           acb verify <this file>     says who made it, when, and whether anything was changed
           acb                        walks you through a restore, showing every file before it is written

         It restores only on {platform}, and keeps a record so `acb undo` can put the PC back.
         """;

    private static void DeleteQuietly(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
