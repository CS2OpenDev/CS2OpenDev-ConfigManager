using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>
///     The administrator-only undo store on Windows. Every folder from %ProgramData% down is made by the elevated
///     process with a protected ACL (Administrators and SYSTEM, full control) and checked again on every use, so a
///     folder a player made there first, or a link a player planted, is refused instead of being written to as
///     administrator. %ProgramData% lets any user create a subfolder, which is why its own ACL is not relied on.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StoreSecurity
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3425870976-2374617826-3634166101");

    /// <summary>
    ///     Rights that change a folder, what it holds, or who may: write or append (add a file or folder), extended
    ///     attributes, attributes, delete child, delete, change permissions, take ownership, and generic write or all.
    /// </summary>
    private const int ChangingRights = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;

    /// <summary>What players get on the record folder itself: list it and read attributes, so `acb undo` can tell whether records wait.</summary>
    private const FileSystemRights ListOnly = FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize;

    /// <summary>
    ///     Creates every missing folder of <paramref name="root" /> below <paramref name="trustedParent" /> with the
    ///     store's ACL, then checks each one, existing or new. Throws <see cref="UnauthorizedAccessException" /> naming
    ///     the folder when one fails the check; nothing is written to the store then.
    /// </summary>
    public static void EnsureMachineStore(string root, string trustedParent)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(trustedParent);
        string full = Path.GetFullPath(root);
        string parent = Path.GetFullPath(trustedParent).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{root} is not under {trustedParent}.", nameof(root));
        }

        string at = parent;
        string[] parts = full[(parent.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            at = Path.Combine(at, parts[i]);
            bool isRecordFolder = i == parts.Length - 1;
            if (!Directory.Exists(at))
            {
                // Does nothing when another process made the folder first; the check below then judges theirs.
                FileSystemAclExtensions.CreateDirectory(NewStoreSecurity(isRecordFolder), at);
            }

            string? problem = FolderProblem(at);
            if (problem is not null)
            {
                throw new UnauthorizedAccessException($"{at} {problem}, so the administrator's undo store is not used. An administrator should delete that folder.");
            }
        }
    }

    /// <summary>Why a store folder must not be used, or null: it is a link, or someone other than administrators could change it.</summary>
    public static string? FolderProblem(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return "is a link or another reparse point";
        }

        DirectorySecurity security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        return DescriptorProblem(new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0));
    }

    /// <summary>
    ///     Why a security descriptor lets someone other than Administrators, SYSTEM or TrustedInstaller change the
    ///     folder, or null. Unknown entry types count against it, since an entry this cannot read is one it cannot vouch for.
    /// </summary>
    public static string? DescriptorProblem(RawSecurityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Owner is not { } owner || !IsTrusted(owner))
        {
            return "is owned by " + Name(descriptor.Owner) + ", not by administrators";
        }

        if (descriptor.DiscretionaryAcl is not { } acl)
        {
            return "has no access list, so everyone may change it";
        }

        foreach (GenericAce ace in acl)
        {
            if (ace is not QualifiedAce known)
            {
                return "has an access entry of a kind this restorer does not read";
            }

            bool allows = known.AceQualifier == AceQualifier.AccessAllowed;
            if (allows && (known.AccessMask & ChangingRights) != 0 && !IsTrusted(known.SecurityIdentifier))
            {
                return "lets " + Name(known.SecurityIdentifier) + " change it";
            }
        }

        return null;
    }

    private static DirectorySecurity NewStoreSecurity(bool isRecordFolder)
    {
        DirectorySecurity security = new();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        if (isRecordFolder)
        {
            // This folder only, not inherited: players see that records exist, never what they hold.
            security.AddAccessRule(new FileSystemAccessRule(Users, ListOnly, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static bool IsTrusted(SecurityIdentifier sid) => sid == Administrators || sid == LocalSystem || sid == TrustedInstaller;

    private static string Name(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return "nobody";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }
}
