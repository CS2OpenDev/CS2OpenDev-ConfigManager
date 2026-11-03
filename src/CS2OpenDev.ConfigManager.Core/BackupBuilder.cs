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
    ///     part was captured; the report says why. With <paramref name="seal" /> the archive is sealed with it, and
    ///     with <paramref name="restorer" /> it carries those bytes as its restorer (self-contained). With
    ///     <paramref name="identity" /> it is signed for a Steam account instead of sealed.
    /// </summary>
    public static RunReport Create(CaptureOptions options, string zipPath, ArchiveTool tool, DateTimeOffset now, CancellationToken ct,
        SealKey? seal = null, byte[]? restorer = null, IIdentitySigner? identity = null)
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
            ArchiveWriter.Write(pack, zipPath, new ArchiveContent(platform, tool, account, sections, Readme(platform, seal is not null, restorer is not null)), now, ct,
                seal, restorer, identity);
            return report;
        }
        finally
        {
            DeleteQuietly(work);
        }
    }

    /// <summary>
    ///     What a backup with <paramref name="options" /> would save, part by part, without writing one: the capture
    ///     runs into a private temp folder that is deleted afterwards. Reads only, like <see cref="Create" />.
    /// </summary>
    public static RunReport Preview(CaptureOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        string work = Path.Combine(Path.GetTempPath(), "acb-" + Guid.NewGuid().ToString("N"));
        options.Cancellation = ct;
        try
        {
            return KitBackup.Capture(options, Path.Combine(work, "capture"), null);
        }
        finally
        {
            DeleteQuietly(work);
        }
    }

    internal static string Readme(string platform, bool sealedWithPassphrase = false, bool selfContained = false) =>
        $"""
         Advanced Config Backup
         ======================

         CS2 settings, and the {platform} settings that go with them, saved by Advanced Config Backup.

         Everything in here is plain text you can open and read. manifest.json lists every file with its
         SHA-256, and signature.dsse.json names manifest.json's own SHA-256. If any file is changed, added or
         removed, the restorer refuses the whole backup: make a new one instead of editing this one. An
         unsigned (hash level) backup shows only that nothing was damaged or changed by accident: anyone
         could have edited it and rewritten its manifest.{(sealedWithPassphrase ? SealReadme : "")}

         To check or restore it, use the restorer, acb:

           acb verify <this file>     says how it is signed, when it was made, and that its files match
           acb plan <this file>       says what a restore would change, changing nothing
           acb restore <this file>    restores it, asking first and recording what it replaces
           acb undo                   puts the PC back as it was before the restore

         Game state integration files (gamestate_integration_*.cfg), if it carries any, send live game data
         to the address they name, so a restore writes them only when given --gsi. Give --gsi to acb plan
         as well, so the plan shows them as written; a restore --plan needs the same --gsi as its plan.

         It restores only on {platform}.{(selfContained ? SelfContainedReadme(platform) : "")}
         """;

    private static string SelfContainedReadme(string platform)
    {
        bool windows = platform == Platforms.WindowsX64;
        string exe = windows ? ArchiveFormat.RestorerEntry(platform).Replace('/', '\\') : ArchiveFormat.RestorerEntry(platform);
        string unpack = windows
            ? "right-click this file, pick Extract All, and keep the folder it offers"
            : "unzip <this file> -d <its name without .zip>";
        string trust = windows
            ? "Windows shows who published it before it runs; trust that, not anything this file says."
            : "Before running it, check sha256sum restorer/acb against the SHA256SUMS of the acb release it should be.";
        return $"""


            This backup carries its own restorer, {exe}: the acb that made it. To use it, unpack the
            whole backup into a folder of the same name beside this file:

              {unpack}

            then run {exe} from that folder. It finds this file beside the folder and checks it as
            above; the unpacked copy is never what it restores. The restorer is a program, so run it only
            from a backup whose source you trust. {trust}
            """;
    }

    private const string SealReadme = """


         This backup is sealed with a passphrase: acb asks for it, and only that passphrase opens the seal, so a
         backup that opens was made by someone who knew it and is unchanged since. Anyone holding this file can
         try passphrases, though, and a guessed one could seal a changed copy, so keep the passphrase long.
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
