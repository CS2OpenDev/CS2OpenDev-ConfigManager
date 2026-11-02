#nullable disable
#pragma warning disable CA1825, CA1846, CA2219
// Written in C# 5 for v1's script restorer, which compiled this kit in Windows PowerShell 5.1. That restorer
// is gone and the dialect no longer binds, but the code keeps its style until it is reworked: nullable checks are
// off, and each kit file turns off by name only the style rules that dialect breaks. The platform, disposal,
// security, culture and trimming analyzers run here as everywhere else. New code goes outside Kit/.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CS2OpenDev.ConfigManager.Kit
{
    /// <summary>
    ///     Valve's KeyValues text format: the shape of every CS2 .vcfg, cs2_video.txt, Steam's
    ///     localconfig.vdf and libraryfolders.vdf, and of this tool's own backup.vdf. A node is either a
    ///     key with a string value or a key with children. Keys may repeat and order is kept, so a file
    ///     read and written back unchanged comes out with the same entries in the same order.
    /// </summary>
    public sealed class KvNode
    {
        private readonly List<KvNode> _children;

        public KvNode(string key, string value)
        {
            Key = key;
            Value = value;
        }

        public KvNode(string key)
        {
            Key = key;
            _children = new List<KvNode>();
        }

        public string Key { get; set; }

        /// <summary>The string value, or null for a node with children.</summary>
        public string Value { get; set; }

        public bool IsSection
        {
            get { return _children != null; }
        }

        public IList<KvNode> Children
        {
            get { return _children ?? (IList<KvNode>)new KvNode[0]; }
        }

        /// <summary>The first child named <paramref name="key" />, ignoring case, or null.</summary>
        public KvNode Child(string key)
        {
            if (_children == null)
            {
                return null;
            }

            foreach (KvNode child in _children)
            {
                if (string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        /// <summary>Follows a path of child keys, or null where one is missing.</summary>
        public KvNode Path(params string[] keys)
        {
            KvNode node = this;
            foreach (string key in keys)
            {
                node = node.Child(key);
                if (node == null)
                {
                    return null;
                }
            }

            return node;
        }

        /// <summary>The string value of the child <paramref name="key" />, or null.</summary>
        public string Get(string key)
        {
            KvNode child = Child(key);
            return child == null ? null : child.Value;
        }

        /// <summary>The child section <paramref name="key" />, created at the end when missing.</summary>
        public KvNode Section(string key)
        {
            KvNode child = Child(key);
            if (child != null && child.IsSection)
            {
                return child;
            }

            if (child != null)
            {
                _children.Remove(child);
            }

            KvNode created = new KvNode(key);
            _children.Add(created);
            return created;
        }

        /// <summary>Sets the child <paramref name="key" /> to a string value, in place if it exists.</summary>
        public KvNode Set(string key, string value)
        {
            KvNode child = Child(key);
            if (child != null && !child.IsSection)
            {
                child.Value = value;
                return child;
            }

            if (child != null)
            {
                _children.Remove(child);
            }

            KvNode created = new KvNode(key, value);
            _children.Add(created);
            return created;
        }

        public KvNode Add(KvNode child)
        {
            _children.Add(child);
            return child;
        }

        public bool Remove(string key)
        {
            KvNode child = Child(key);
            return child != null && _children.Remove(child);
        }

        public static KvNode Parse(string text)
        {
            KvReader reader = new KvReader(text);
            KvNode root = new KvNode("");
            reader.ReadInto(root, true);
            if (root.Children.Count == 1 && root.Children[0].IsSection)
            {
                return root.Children[0];
            }

            if (root.Children.Count == 0)
            {
                throw new FormatException("The text holds no KeyValues section.");
            }

            return root;
        }

        public static KvNode Load(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public void Save(string path)
        {
            string directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            SafeFile.Write(path, new UTF8Encoding(false).GetBytes(ToText()));
        }

        /// <summary>Tab-indented, quoted, the layout Steam and CS2 write themselves.</summary>
        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, this, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, KvNode node, int depth)
        {
            sb.Append('\t', depth).Append(Quote(node.Key));
            if (node.IsSection)
            {
                sb.Append('\n');
                sb.Append('\t', depth).Append("{\n");
                foreach (KvNode child in node._children)
                {
                    Write(sb, child, depth + 1);
                }

                sb.Append('\t', depth).Append("}\n");
            }
            else
            {
                sb.Append("\t\t").Append(Quote(node.Value)).Append('\n');
            }
        }

        private static string Quote(string text)
        {
            StringBuilder sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
            {
                if (c == '"' || c == '\\')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.Append('"').ToString();
        }

        private sealed class KvReader
        {
            private readonly string _text;
            private int _pos;

            public KvReader(string text)
            {
                _text = text;
                if (_text.Length > 0 && _text[0] == (char)0xFEFF)
                {
                    _pos = 1;
                }
            }

            public void ReadInto(KvNode parent, bool topLevel)
            {
                while (true)
                {
                    string key = Token();
                    if (key == null)
                    {
                        if (!topLevel)
                        {
                            throw new FormatException("A section is not closed.");
                        }

                        return;
                    }

                    if (key == "}" && _lastWasBrace)
                    {
                        if (topLevel)
                        {
                            throw new FormatException("A '}' has no section to close.");
                        }

                        return;
                    }

                    string next = Token();
                    if (next == null)
                    {
                        throw new FormatException("The key '" + key + "' has no value.");
                    }

                    if (next == "{" && _lastWasBrace)
                    {
                        KvNode section = new KvNode(key);
                        parent.Add(section);
                        ReadInto(section, false);
                    }
                    else
                    {
                        parent.Add(new KvNode(key, next));
                        SkipConditional();
                    }
                }
            }

            private bool _lastWasBrace;

            private void SkipConditional()
            {
                int save = _pos;
                SkipSpaceAndComments();
                if (_pos < _text.Length && _text[_pos] == '[')
                {
                    int end = _text.IndexOf(']', _pos);
                    if (end > 0)
                    {
                        _pos = end + 1;
                        return;
                    }
                }

                _pos = save;
            }

            private void SkipSpaceAndComments()
            {
                while (_pos < _text.Length)
                {
                    char c = _text[_pos];
                    if (char.IsWhiteSpace(c))
                    {
                        _pos++;
                    }
                    else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                    {
                        while (_pos < _text.Length && _text[_pos] != '\n')
                        {
                            _pos++;
                        }
                    }
                    else
                    {
                        return;
                    }
                }
            }

            private string Token()
            {
                SkipSpaceAndComments();
                _lastWasBrace = false;
                if (_pos >= _text.Length)
                {
                    return null;
                }

                char c = _text[_pos];
                if (c == '{' || c == '}')
                {
                    _pos++;
                    _lastWasBrace = true;
                    return c.ToString();
                }

                StringBuilder sb = new StringBuilder();
                if (c == '"')
                {
                    _pos++;
                    while (_pos < _text.Length && _text[_pos] != '"')
                    {
                        char ch = _text[_pos++];
                        if (ch == '\\' && _pos < _text.Length)
                        {
                            char esc = _text[_pos++];
                            switch (esc)
                            {
                                case 'n': sb.Append('\n'); break;
                                case 't': sb.Append('\t'); break;
                                case '"': sb.Append('"'); break;
                                case '\\': sb.Append('\\'); break;
                                default: sb.Append('\\').Append(esc); break;
                            }
                        }
                        else
                        {
                            sb.Append(ch);
                        }
                    }

                    if (_pos >= _text.Length)
                    {
                        throw new FormatException("A quoted string is not closed.");
                    }

                    _pos++;
                    return sb.ToString();
                }

                while (_pos < _text.Length)
                {
                    char ch = _text[_pos];
                    if (char.IsWhiteSpace(ch) || ch == '{' || ch == '}' || ch == '"')
                    {
                        break;
                    }

                    sb.Append(ch);
                    _pos++;
                }

                return sb.ToString();
            }
        }
    }

    /// <summary>
    ///     Writes a file through a temporary copy beside it that is flushed to the disk and then swapped in, so a
    ///     process killed mid-write, or a power loss, leaves either the old file or the new one, never a torn or
    ///     zero-filled one. A read-only file stays read-only: the attribute is cleared for the swap and put back
    ///     on whichever file is left.
    /// </summary>
    public static class SafeFile
    {
        private const string TempSuffix = ".acb-tmp";

        public static void Write(string path, byte[] bytes)
        {
            Write(path, bytes, false);
        }

        /// <summary>
        ///     Writes a record that must be on the disk before the change it guards: unlike <see cref="Write(string, byte[])" />,
        ///     a failed flush after the swap throws, so the caller makes no change its record may not hold.
        /// </summary>
        public static void WriteRecord(string path, byte[] bytes)
        {
            Write(path, bytes, true);
        }

        private static void Write(string path, byte[] bytes, bool record)
        {
            string temp = path + TempSuffix;
            ClearReadOnly(temp);
            WriteFlushed(temp, bytes);
            Swap(temp, path, record);
        }

        public static void Copy(string source, string path)
        {
            Write(path, File.ReadAllBytes(source));
        }

        /// <summary>Writes a whole file and returns only once its data is on the disk, not just in the cache.</summary>
        public static void WriteFlushed(string path, byte[] bytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>Deletes a file, read-only or not.</summary>
        public static void Delete(string path)
        {
            if (File.Exists(path))
            {
                ClearReadOnly(path);
                File.Delete(path);
            }
        }

        /// <summary>Clears ReadOnly on <paramref name="path" />; whether it was set.</summary>
        public static bool ClearReadOnly(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0)
            {
                return false;
            }

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            return true;
        }

        /// <summary>
        ///     Throws when an existing file could not be swapped (another user's file, a deny ACL, or one a program
        ///     holds open), by asking for the rights the swap uses. In a real run a read-only file has its attribute
        ///     cleared for the check and put back, which changes no content, and a flag that cannot be put back is
        ///     an error, not a silent loss. A dry run changes nothing: a read-only file is only opened for reading
        ///     and its attributes, which is the access clearing the flag needs. The folder is checked too, new file
        ///     or not, since every write first adds a temporary file beside the target.
        /// </summary>
        public static void CheckWritable(string path, bool dryRun)
        {
            CheckFolder(path);
            if (!File.Exists(path))
            {
                return;
            }

            if (dryRun && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                if (!Platform.IsWindows)
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                    }

                    return;
                }

                // Read and write-attributes only: a read-only file allows both, and nothing is written.
                OpenOrThrow(path, 0x80000000 | 0x100);
                return;
            }

            bool readOnly = ClearReadOnly(path);
            bool checkedOk = false;
            try
            {
                if (!Platform.IsWindows)
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                    {
                    }
                }
                else
                {
                    // Read and write for the data, write-attributes for the read-only flag, delete for File.Replace.
                    OpenOrThrow(path, 0x80000000 | 0x2 | 0x100 | 0x10000);
                }

                checkedOk = true;
            }
            finally
            {
                if (readOnly)
                {
                    Exception putBack = null;
                    try
                    {
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    }
                    catch (IOException ex)
                    {
                        putBack = ex;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        putBack = ex;
                    }

                    // Cleanup never hides the error that brought it here; on its own it stops the part and is named.
                    if (putBack != null && checkedOk)
                    {
                        throw new IOException("its read-only flag was cleared for the check and could not be set again (" + putBack.Message + "); the file is now writable", putBack);
                    }
                }
            }
        }

        /// <summary>
        ///     Throws when an existing file could not be deleted (an ACL that denies it, or a program that holds it
        ///     open), by asking for the rights <see cref="Delete" /> uses; a file that is not there is nothing to do.
        ///     Removing a file adds nothing to its folder, so the folder is not asked to take a new file. Changes nothing.
        /// </summary>
        public static void CheckDeletable(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            if (!Platform.IsWindows)
            {
                // W_OK | X_OK on the folder: removing an entry needs both.
                string folder = Path.GetDirectoryName(path);
                if (folder != null && Access(folder, 0x2 | 0x1) != 0)
                {
                    throw new IOException("its folder " + folder + " does not let this user remove files");
                }

                return;
            }

            // DELETE (granted by the file or by its folder), and write-attributes when a read-only flag goes first.
            bool readOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            OpenOrThrow(path, 0x10000u | (readOnly ? 0x100u : 0u));
        }

        /// <summary>
        ///     Throws when this user may not add a file to the folder <paramref name="path" /> goes into, or, when that
        ///     folder is missing, add the folders it needs to the nearest one there is. Opens the folder for those
        ///     rights only, so nothing is created.
        /// </summary>
        private static void CheckFolder(string path)
        {
            string folder = Path.GetDirectoryName(path);
            bool makesFolders = false;
            while (folder != null && !Directory.Exists(folder))
            {
                makesFolders = true;
                folder = Path.GetDirectoryName(folder);
            }

            if (folder == null)
            {
                return;
            }

            if (Platform.IsWindows)
            {
                try
                {
                    // FILE_ADD_FILE, and FILE_ADD_SUBDIRECTORY when folders are made; a folder opens only with backup semantics.
                    OpenOrThrow(folder, makesFolders ? 0x6u : 0x2u, 0x02000000);
                }
                catch (IOException ex)
                {
                    throw new IOException("its folder " + folder + " does not let this user add " + (makesFolders ? "folders" : "files") + ": " + ex.Message, ex);
                }

                return;
            }

            // W_OK | X_OK: adding an entry needs both.
            if (Access(folder, 0x2 | 0x1) != 0)
            {
                throw new IOException("its folder " + folder + " does not let this user add " + (makesFolders ? "folders" : "files"));
            }
        }

        private static void OpenOrThrow(string path, uint access)
        {
            OpenOrThrow(path, access, 0);
        }

        private static void OpenOrThrow(string path, uint access, uint flags)
        {
            using (SafeFileHandle handle = CreateFileW(path, access, 0x7, IntPtr.Zero, 3, flags, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
            }
        }

        [DllImport("libc", EntryPoint = "access", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        /// <summary>
        ///     Flushes the swapped-in file, which on NTFS also commits the rename to the disk. For a plain file a
        ///     failure is ignored, since the data was flushed before the swap and the worst left is the previous
        ///     whole file; for a record it throws, since the previous whole file lacks what the change needs.
        /// </summary>
        private static void FlushSwapped(string path, bool record)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Flush(true);
                }
            }
            catch (IOException)
            {
                if (record)
                {
                    throw;
                }
            }
            catch (UnauthorizedAccessException)
            {
                if (record)
                {
                    throw;
                }
            }
        }

        private static void Swap(string temp, string path, bool record)
        {
            bool readOnly = false;
            try
            {
                if (File.Exists(path))
                {
                    readOnly = ClearReadOnly(path);
                    try
                    {
                        File.Replace(temp, path, null, true);
                    }
                    catch (IOException) when (!File.Exists(path) && File.Exists(temp))
                    {
                        // Replace can fail after the old file is gone; the temporary file is then the only copy left.
                        File.Move(temp, path);
                    }
                }
                else
                {
                    File.Move(temp, path);
                }

                FlushSwapped(path, record);
            }
            finally
            {
                // Cleanup never hides the error that brought it here.
                try
                {
                    if (readOnly && File.Exists(path))
                    {
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                    }

                    if (File.Exists(temp))
                    {
                        ClearReadOnly(temp);
                        File.Delete(temp);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    internal static class Inv
    {
        public static string S(long value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static int I(string text, int fallback)
        {
            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        public static string Hex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        public static byte[] Unhex(string hex)
        {
            if (hex == null || hex.Length % 2 != 0)
            {
                throw new FormatException("Not a hex string.");
            }

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return bytes;
        }
    }
}
