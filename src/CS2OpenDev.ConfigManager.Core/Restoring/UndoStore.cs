using System.Globalization;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>One undo record in a store: a folder named by its sequence number, holding backup.vdf and the copies.</summary>
internal sealed class UndoRecord
{
    internal UndoRecord(string dir, KvNode? manifest, string? problem)
    {
        Dir = dir;
        Name = Path.GetFileName(dir);
        Sequence = int.Parse(Name.AsSpan(0, 6), NumberStyles.None, CultureInfo.InvariantCulture);
        Manifest = manifest;
        Problem = problem;
    }

    public string Dir { get; }

    /// <summary>The folder name: six-digit sequence, then the UTC time it was made.</summary>
    public string Name { get; }

    public int Sequence { get; }

    /// <summary>The record, or null when it cannot be read (see <see cref="Problem" />) or holds none (it changed nothing).</summary>
    public KvNode? Manifest { get; }

    /// <summary>Why the record cannot be read, or null.</summary>
    public string? Problem { get; }

    public bool IsReadable => Problem is null;

    /// <summary>The parts still to be put back; empty for a record that changed nothing or is done.</summary>
    public IReadOnlyList<string> Parts => Manifest is null ? [] : ConfigBackup.PartsLeft(Manifest);

    public string? Created => Manifest?.Get("created");

    /// <summary>The SHA-256 of the backup's manifest.json the restore applied, as recorded.</summary>
    public string? ArchiveDigest => Manifest?.Get("archive.sha256");
}

/// <summary>
///     Where the restorer keeps undo records: its own folder, never beside a backup. Records are numbered, claimed
///     atomically and written before each change (by the engine); finished ones move into <c>applied/</c>, and an
///     unreadable one the person sets aside moves into <c>set-aside/</c>, so it no longer holds back older ones.
/// </summary>
internal sealed class UndoStore
{
    public const string AppliedFolder = "applied";
    public const string SetAsideFolder = "set-aside";

    public UndoStore(string root, bool isMachine)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Path.GetFullPath(root);
        IsMachine = isMachine;
    }

    public string Root { get; }

    /// <summary>The administrator-only store, which only an elevated process writes.</summary>
    public bool IsMachine { get; }

    /// <summary>
    ///     The per-user store: %LOCALAPPDATA%\CS2OpenDev\ConfigBackup\undo on Windows, and
    ///     $XDG_STATE_HOME/cs2opendev/configbackup/undo (~/.local/state/...) on Linux.
    /// </summary>
    public static string UserRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } fromEnv && Path.IsPathFullyQualified(fromEnv)
                ? fromEnv
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "CS2OpenDev", "ConfigBackup", "undo");
        }

        string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathFullyQualified(state))
        {
            state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        }

        return Path.Combine(state, "cs2opendev", "configbackup", "undo");
    }

    /// <summary>%ProgramData% on Windows, the folder the administrator-only store is made under; null elsewhere.</summary>
    public static string? MachineParent() =>
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) : null;

    /// <summary>%ProgramData%\CS2OpenDev\ConfigBackup\undo on Windows; null on Linux, which has no machine-wide sections yet.</summary>
    public static string? MachineRoot() =>
        MachineParent() is { } parent ? Path.Combine(parent, "CS2OpenDev", "ConfigBackup", "undo") : null;

    /// <summary>Creates the store when missing. The per-user store on Linux is private to the user (0700).</summary>
    public void EnsureUserStore()
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Root);
        }
        else
        {
            Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Whether the store holds any record folder, without reading one (players may list the machine store, not read it).</summary>
    public bool HasRecords()
    {
        try
        {
            return Directory.Exists(Root) && Directory.EnumerateDirectories(Root).Any(d => ConfigBackup.IsUndoSnapshotName(Path.GetFileName(d)));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Every record waiting in the store, newest first by sequence number, which the clock cannot reorder.
    ///     Throws <see cref="UndoStoreUnreachableException" /> when the store may be there but cannot be reached, so
    ///     that is never taken for a store with nothing in it.
    /// </summary>
    public IReadOnlyList<UndoRecord> Records()
    {
        if (!Directory.Exists(Root))
        {
            return UnreachableReason() is { } why ? throw new UndoStoreUnreachableException(Root, why, null) : [];
        }

        string[] names;
        try
        {
            names = [.. Directory.EnumerateDirectories(Root).Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UndoStoreUnreachableException(Root, ex.Message, ex);
        }

        List<UndoRecord> records = [];
        foreach (string name in ConfigBackup.NewestFirst(names))
        {
            string dir = Path.Combine(Root, name);
            if (!File.Exists(Path.Combine(dir, ConfigBackup.ManifestName)))
            {
                // A claim makes its backup.vdf before anything else goes into the folder, so an empty folder is a
                // claim cut short that changed nothing. Anything else beside a missing backup.vdf means the record
                // was lost after the restore wrote it, and what it changed is unknown.
                records.Add(IsEmptyFolder(dir)
                    ? new UndoRecord(dir, null, null)
                    : new UndoRecord(dir, null, ConfigBackup.ManifestName + " is missing, though the folder holds what a restore left"));
                continue;
            }

            try
            {
                records.Add(new UndoRecord(dir, ConfigBackup.ReadManifest(dir), null));
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                records.Add(new UndoRecord(dir, null, ex.Message));
            }
        }

        return records;
    }

    private static bool IsEmptyFolder(string dir)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Why a store folder that does not show up may be there all the same, or null when it is simply not made
    ///     yet: the nearest folder above it that is there cannot be listed, or lists the way down that could not be
    ///     followed, or none is there at all (a drive or share that is offline).
    /// </summary>
    private string? UnreachableReason()
    {
        string child = Root;
        for (string? parent = Path.GetDirectoryName(Root); parent is not null; child = parent, parent = Path.GetDirectoryName(parent))
        {
            if (!Directory.Exists(parent))
            {
                continue;
            }

            try
            {
                return Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(child)).Any()
                    ? child + " is there but cannot be opened"
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return parent + " cannot be listed (" + ex.Message + ")";
            }
        }

        return "no folder it is in can be reached";
    }

    /// <summary>The record named by its folder name or its sequence number, or null.</summary>
    public UndoRecord? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        bool isNumber = int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int sequence);
        return Records().FirstOrDefault(r => r.Name == id || (isNumber && r.Sequence == sequence));
    }

    /// <summary>Moves a finished record into applied/. Retries for a moment while a virus scanner or indexer holds a file.</summary>
    public void FileAway(UndoRecord record) => Move(record, AppliedFolder);

    /// <summary>
    ///     Moves an unreadable record into set-aside/, where it no longer holds back older records. Only on the
    ///     person's word: what it recorded is then not put back.
    /// </summary>
    public void SetAside(UndoRecord record) => Move(record, SetAsideFolder);

    private void Move(UndoRecord record, string folder)
    {
        ArgumentNullException.ThrowIfNull(record);
        string into = Path.Combine(Root, folder);
        Directory.CreateDirectory(into);
        string target = Path.Combine(into, record.Name);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(record.Dir, target);
                return;
            }
            catch (IOException) when (attempt < 5 && !Directory.Exists(target))
            {
                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>The store may hold records, but cannot be reached: a folder on the way denies access, or a drive or share is offline.</summary>
internal sealed class UndoStoreUnreachableException : IOException
{
    public UndoStoreUnreachableException(string root, string reason, Exception? inner)
        : base($"the undo store {root} cannot be reached ({reason}), so whether records wait there is unknown", inner)
    {
    }
}
