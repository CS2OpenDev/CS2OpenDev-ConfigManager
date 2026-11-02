using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>
///     A verified v2 archive as the engine reads it: capture.vdf as the record and files/&lt;section&gt;/... as the
///     files, all from the bytes the reader checked. Nothing is unpacked, so no folder anyone else could write
///     stands between the check and the write.
/// </summary>
public sealed class ArchiveSource : IBackupSource
{
    private readonly VerifiedArchive _archive;

    public ArchiveSource(VerifiedArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        _archive = archive;
        if (!archive.Contains(ArchiveFormat.CaptureEntry))
        {
            throw new ArchiveRejectedException($"The backup has no {ArchiveFormat.CaptureEntry}, so there is nothing to restore.");
        }

        try
        {
            Manifest = ConfigBackup.ParseManifest(ConfigBackup.Text(archive.Read(ArchiveFormat.CaptureEntry).ToArray()), ArchiveFormat.CaptureEntry);
        }
        catch (FormatException ex)
        {
            throw new ArchiveRejectedException($"The backup's {ArchiveFormat.CaptureEntry} cannot be read: {ex.Message}");
        }
    }

    public KvNode Manifest { get; }

    public byte[]? ReadFile(string section, string relative)
    {
        string path = "files/" + section + "/" + relative;
        return ArchivePaths.Problem(path) is null && _archive.Contains(path) ? _archive.Read(path).ToArray() : null;
    }

    /// <summary>An archive is never changed, whatever the run: only undo records note what is put back.</summary>
    public void SaveManifest() => throw new InvalidOperationException("A backup archive is never written to.");
}
