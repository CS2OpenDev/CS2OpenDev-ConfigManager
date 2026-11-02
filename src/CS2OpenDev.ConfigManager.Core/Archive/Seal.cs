using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>
///     How a sealed archive's key was made from its passphrase, as its signed statement records it: Argon2id with
///     this memory (KiB), passes and lanes over a random salt (base64). A reader accepts only the settings this
///     version writes, so an archive cannot make it spend more memory or time than a seal of its own would.
/// </summary>
public sealed record SealKdf(string Name, int MemoryKiB, int Iterations, int Parallelism, string Salt);

/// <summary>
///     A key that seals or checks one archive: Argon2id over the passphrase, then HMAC-SHA256 over DSSE's encoding of
///     the statement. The key is per archive (its salt is), so handing it on, to the administrator's run, opens that
///     archive and no other.
/// </summary>
public sealed class SealKey
{
    private readonly byte[] _key;

    private SealKey(SealKdf kdf, byte[] key)
    {
        Kdf = kdf;
        _key = key;
    }

    public SealKdf Kdf { get; }

    /// <summary>The key's bytes, for handing to the administrator's run of the same archive.</summary>
    public ReadOnlySpan<byte> Bytes => _key;

    /// <summary>A key for a new archive: a fresh salt. Throws <see cref="ArgumentException" /> for a passphrase <see cref="Seal.PassphraseProblem" /> refuses.</summary>
    public static SealKey Create(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (Seal.PassphraseProblem(passphrase) is { } problem)
        {
            throw new ArgumentException(problem, nameof(passphrase));
        }

        SealKdf kdf = new(Seal.Argon2idName, Seal.MemoryKiB, Seal.Iterations, Seal.Parallelism,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(Seal.SaltBytes)));
        return new SealKey(kdf, Seal.DeriveKey(passphrase, kdf));
    }

    /// <summary>The key an archive's settings and this passphrase make. Any passphrase: a short one can only fail to open it.</summary>
    internal static SealKey Derive(string passphrase, SealKdf kdf) => new(kdf, Seal.DeriveKey(passphrase, kdf));

    /// <summary>A key handed on by the run that derived it, for an archive with these settings.</summary>
    internal static SealKey? FromBytes(SealKdf kdf, ReadOnlySpan<byte> key) =>
        key.Length == Seal.KeyBytes ? new SealKey(kdf, key.ToArray()) : null;

    internal byte[] Sign(byte[] preAuthEncoding) => HMACSHA256.HashData(_key, preAuthEncoding);

    internal bool Check(byte[] preAuthEncoding, ReadOnlySpan<byte> signature) =>
        CryptographicOperations.FixedTimeEquals(Sign(preAuthEncoding), signature);
}

/// <summary>The <c>seal</c> level's fixed settings, passphrase rules and generated phrases (docs/design.md, "seal: passphrase").</summary>
public static class Seal
{
    public const string KeyId = "seal";
    public const string Argon2idName = "argon2id";
    public const int MemoryKiB = 64 * 1024;
    public const int Iterations = 3;
    public const int Parallelism = 1;
    public const int SaltBytes = 16;
    public const int KeyBytes = 32;
    public const int MinimumLength = 12;
    public const int GeneratedWords = 6;

    private static readonly Lazy<string[]> Words = new(LoadWords);

    /// <summary>Why a passphrase may not seal a backup, or null. Counted in characters as a person sees them.</summary>
    public static string? PassphraseProblem(string passphrase)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return "the passphrase is empty";
        }

        int length = new StringInfo(passphrase).LengthInTextElements;
        return length < MinimumLength
            ? $"the passphrase is {length} characters, and a seal needs at least {MinimumLength} (a generated phrase of {GeneratedWords} words is stronger)"
            : null;
    }

    /// <summary>
    ///     Whether a passphrase holds characters beyond plain ASCII. Such a character can be typed in more than one way
    ///     (an accented letter as one character, or as a letter and an accent), and the key is made from exactly what
    ///     was typed, so the same-looking passphrase typed another way does not open the seal. Worth a warning, not a
    ///     refusal.
    /// </summary>
    public static bool MayBeTypedDifferently(string passphrase) => passphrase.Any(c => c > '\u007f');

    /// <summary>
    ///     Six words from the EFF's large list (7,776 words, about 77 bits together), picked with the system's
    ///     random number generator and joined by spaces.
    /// </summary>
    public static string GeneratePhrase()
    {
        string[] words = Words.Value;
        return string.Join(' ', Enumerable.Range(0, GeneratedWords).Select(_ => words[RandomNumberGenerator.GetInt32(words.Length)]));
    }

    /// <summary>Why an archive's key settings are not ones this version checks, or null.</summary>
    internal static string? KdfProblem(SealKdf? kdf)
    {
        if (kdf is null)
        {
            return "it names no key settings";
        }

        if (kdf.Name != Argon2idName || kdf.MemoryKiB != MemoryKiB || kdf.Iterations != Iterations || kdf.Parallelism != Parallelism)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"its key settings ({kdf.Name}, {kdf.MemoryKiB} KiB, {kdf.Iterations} passes, {kdf.Parallelism} lanes) are not the ones this version uses. A newer version may read it");
        }

        try
        {
            return Convert.FromBase64String(kdf.Salt).Length == SaltBytes ? null : $"its salt is not {SaltBytes} bytes";
        }
        catch (FormatException)
        {
            return "its salt is not base64";
        }
    }

    /// <summary>
    ///     Argon2id over the passphrase's UTF-8, exactly as typed: nothing is trimmed (a space is part of the
    ///     passphrase) and nothing is normalised. acb is built with invariant globalisation, where normalising does
    ///     nothing, so normalising here would make the plugin and acb derive different keys from one passphrase.
    /// </summary>
    internal static byte[] DeriveKey(string passphrase, SealKdf kdf)
    {
        if (KdfProblem(kdf) is { } problem)
        {
            throw new ArgumentException("The backup's seal cannot be checked: " + problem + ".", nameof(kdf));
        }

        byte[] secret = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Argon2id(secret, Convert.FromBase64String(kdf.Salt), kdf.MemoryKiB, kdf.Iterations, kdf.Parallelism, KeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>RFC 9106 Argon2id, version 0x13. <paramref name="secret" /> and <paramref name="associatedData" /> are for the RFC's test vector.</summary>
    internal static byte[] Argon2id(byte[] password, byte[] salt, int memoryKiB, int iterations, int lanes, int length,
        byte[]? secret = null, byte[]? associatedData = null)
    {
        using Argon2id argon = new(password)
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = lanes,
            KnownSecret = secret,
            AssociatedData = associatedData
        };
        return argon.GetBytes(length);
    }

    private static string[] LoadWords()
    {
        using Stream stream = typeof(Seal).Assembly.GetManifestResourceStream("CS2OpenDev.ConfigManager.Archive.eff-large-wordlist.txt")
                              ?? throw new InvalidOperationException("The word list is missing from the build.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string[] words = [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))];
        return words.Length == 7776 ? words : throw new InvalidOperationException($"The word list holds {words.Length} words, not 7776.");
    }
}
