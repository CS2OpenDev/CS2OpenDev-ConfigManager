using System.IO.Compression;
using System.Text;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.DemoViewer.ViewModels;

namespace CS2OpenDev.ConfigManager.DemoViewer.Tests;

/// <summary>
///     The page's "Check a backup", which reads with the restorer's own reader: a changed file means "do not
///     restore", another platform's backup is still read, and an unsigned one is never called unchanged since it
///     was made.
/// </summary>
public class CheckBackupTests
{
    [Test]
    public async Task AValidUnsignedBackup_IsCheckedAndSaidToBeUnsigned()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64);
        List<string> results = [];

        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(status).Contains("each matching its manifest");
        await Assert.That(status).Contains("anyone could have edited it");
        await Assert.That(status).DoesNotContain("unchanged since it was made");
        await Assert.That(results[0]).StartsWith("Trust level: hash (unsigned");
    }

    [Test]
    public async Task ABackupWithAChangedFile_IsNotToBeRestored()
    {
        using Scratch scratch = new();
        string zip = scratch.Archive(ArchiveFormat.CurrentPlatform ?? Platforms.WindowsX64);
        using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("files/cs2game/autoexec.cfg")!.Delete();
            using StreamWriter writer = new(archive.CreateEntry("files/cs2game/autoexec.cfg").Open(), new UTF8Encoding(false));
            await writer.WriteAsync("bind x quit");
        }

        List<string> results = [];
        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(status).StartsWith("Do not restore");
        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task ABackupForAnotherPlatform_IsStillRead()
    {
        using Scratch scratch = new();
        string other = ArchiveFormat.CurrentPlatform == Platforms.WindowsX64 ? Platforms.LinuxX64 : Platforms.WindowsX64;
        string zip = scratch.Archive(other);
        List<string> results = [];

        string status = ConfigBackupPageViewModel.Check(zip, results);

        await Assert.That(status).Contains("each matching its manifest");
        await Assert.That(results).Contains(r => r.EndsWith("for " + other, StringComparison.Ordinal));
    }

    /// <summary>A temp folder holding a hand-made v2 archive of one cfg file.</summary>
    private sealed class Scratch : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "dv-acb-plugin-" + Guid.NewGuid().ToString("N"));

        public string Archive(string platform)
        {
            string staging = Path.Combine(_dir, "staging");
            Directory.CreateDirectory(Path.Combine(staging, "files", "cs2game"));
            File.WriteAllText(Path.Combine(staging, ArchiveFormat.CaptureEntry), "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n}\n");
            File.WriteAllText(Path.Combine(staging, "files", "cs2game", "autoexec.cfg"), "echo autoexec");
            string zip = Path.Combine(_dir, "backup.zip");
            ArchiveWriter.Write(staging, zip, new ArchiveContent(platform, new ArchiveTool("Advanced Config Backup", "test"), null, [], "test\n"),
                DateTimeOffset.UtcNow, CancellationToken.None);
            return zip;
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
    }
}
