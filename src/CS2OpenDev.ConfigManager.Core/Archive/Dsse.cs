using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>
///     DSSE (github.com/secure-systems-lab/dsse): a signature covers the pre-authentication encoding of the
///     payload type and bytes, never the JSON text of the envelope, so re-serialising an envelope cannot change
///     what was signed.
/// </summary>
public static class Dsse
{
    /// <summary><c>"DSSEv1" SP len(type) SP type SP len(body) SP body</c>, lengths as ASCII decimal byte counts.</summary>
    public static byte[] PreAuthEncoding(string payloadType, ReadOnlySpan<byte> payload)
    {
        byte[] type = Encoding.UTF8.GetBytes(payloadType);
        byte[] head = Encoding.ASCII.GetBytes($"DSSEv1 {type.Length} ");
        byte[] mid = Encoding.ASCII.GetBytes($" {payload.Length} ");
        byte[] result = new byte[head.Length + type.Length + mid.Length + payload.Length];
        int at = 0;
        head.CopyTo(result, at);
        at += head.Length;
        type.CopyTo(result, at);
        at += type.Length;
        mid.CopyTo(result, at);
        at += mid.Length;
        payload.CopyTo(result.AsSpan(at));
        return result;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>An envelope as JSON, written as an archive writes it.</summary>
    public static string ToJson(DsseEnvelope envelope) => JsonSerializer.Serialize(envelope, ArchiveJson.Strict.DsseEnvelope);

    /// <summary>An envelope read as strictly as an archive's; throws <see cref="JsonException" /> for anything else.</summary>
    public static DsseEnvelope FromJson(string json) =>
        JsonSerializer.Deserialize(json, ArchiveJson.Strict.DsseEnvelope) ?? throw new JsonException("The envelope is empty.");
}
