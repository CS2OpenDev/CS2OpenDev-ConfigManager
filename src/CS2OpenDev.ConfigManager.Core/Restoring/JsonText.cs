using System.Text;

namespace CS2OpenDev.ConfigManager.Restoring;

/// <summary>
///     The JSON files a person saves for acb (a policy, a plan) as UTF-8 without a byte order mark, which is what the
///     reader takes. Windows' own tools often add a mark, and Windows PowerShell 5.1's <c>&gt;</c> writes UTF-16, so
///     those are read by their mark rather than refused as unreadable. The file's digest stays over its own bytes.
/// </summary>
internal static class JsonText
{
    public static byte[] Utf8(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return bytes[3..];
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2));
        }

        return bytes;
    }
}
