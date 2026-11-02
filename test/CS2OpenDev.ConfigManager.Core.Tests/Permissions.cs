using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     Files and folders this user may not write, for the tests of what a restore does when a write is refused.
///     Each is put back on dispose. Compiled into both test assemblies.
/// </summary>
internal static class Permissions
{
    /// <summary>A file this user may not write: a deny ACL on Windows, no permissions at all on Linux.</summary>
    public static IDisposable Unwritable(string path) =>
        OperatingSystem.IsWindows() ? DenyFile(path, FileSystemRights.WriteData) : UnixMode(path, UnixFileMode.None);

    /// <summary>
    ///     An undo store in which no record can be written, the way a full disk refuses: on Windows the folder for
    ///     a record can be made but no file in it; on Linux not even the folder.
    /// </summary>
    public static IDisposable RefuseRecords(string root)
    {
        Directory.CreateDirectory(root);
        return OperatingSystem.IsWindows()
            ? DenyInFolders(root, FileSystemRights.CreateFiles)
            : UnixMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    /// <summary>A folder this user may not add a file to, though its files stay writable: a cfg folder a cafe locks.</summary>
    public static IDisposable NoNewFiles(string folder) =>
        OperatingSystem.IsWindows()
            ? DenyFolder(folder, FileSystemRights.CreateFiles)
            : UnixMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    /// <summary>A folder this user may not list, though what is in it can still be reached by name.</summary>
    public static IDisposable Unlistable(string folder) =>
        OperatingSystem.IsWindows()
            ? DenyFolder(folder, FileSystemRights.ListDirectory)
            : UnixMode(folder, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    /// <summary>Denies <paramref name="rights" /> on the folder itself, not on what is in it.</summary>
    [SupportedOSPlatform("windows")]
    private static Undo DenyFolder(string folder, FileSystemRights rights)
    {
        DirectoryInfo dir = new(folder);
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        DirectorySecurity acl = dir.GetAccessControl();
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        return new Undo(() =>
        {
            DirectorySecurity back = dir.GetAccessControl();
            back.RemoveAccessRule(deny);
            dir.SetAccessControl(back);
        });
    }

    [SupportedOSPlatform("windows")]
    private static Undo DenyFile(string path, FileSystemRights rights)
    {
        FileInfo info = new(path);
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        FileSecurity acl = info.GetAccessControl();
        acl.AddAccessRule(deny);
        info.SetAccessControl(acl);
        return new Undo(() =>
        {
            FileSecurity back = info.GetAccessControl();
            back.RemoveAccessRule(deny);
            info.SetAccessControl(back);
        });
    }

    /// <summary>Denies <paramref name="rights" /> in every folder made inside <paramref name="root" />, not in the root itself.</summary>
    [SupportedOSPlatform("windows")]
    private static Undo DenyInFolders(string root, FileSystemRights rights)
    {
        DirectoryInfo dir = new(root);
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, rights,
            InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly, AccessControlType.Deny);
        DirectorySecurity acl = dir.GetAccessControl();
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        return new Undo(() =>
        {
            DirectorySecurity back = dir.GetAccessControl();
            back.RemoveAccessRule(deny);
            dir.SetAccessControl(back);
        });
    }

    [UnsupportedOSPlatform("windows")]
    private static Undo UnixMode(string path, UnixFileMode mode)
    {
        UnixFileMode before = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode);
        return new Undo(() => File.SetUnixFileMode(path, before));
    }

    private sealed class Undo(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
