using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.Archive;

/// <summary>
///     The key ceremony's commands (docs/identity-service.md, "The ceremony"), run offline by the root key holders
///     through <c>acb-identity-keys</c>. Root keys are kept only encrypted under a passphrase the holder types, and
///     the service's signing key is given only as its public key. <paramref name="ask" /> asks for a line the holder
///     types where they can see it; both give null when nobody is there to answer. <paramref name="shipped" /> is the
///     trust the release carries (<see cref="IdentityTrust.Shipped" /> unless a test gives another).
/// </summary>
public sealed class Ceremony(TextWriter output, TextWriter error, Func<string, string?> askSecret, Func<string, string?> ask, IdentityTrust? shipped = null)
{
    private readonly IdentityTrust _shipped = shipped ?? IdentityTrust.Shipped;

    public const string Usage = """
        Usage:
          acb-identity-keys new-key <root.pem>
              make a P-256 root key, encrypted under a passphrase you type; prints its id and public key
          acb-identity-keys delegate <delegation.json> --version n [--previous <delegation.json>] --key <public.pem>,<notBefore>,<notAfter>[,<revokedFrom>] ...
              write an unsigned delegation of the service's signing keys (public PEMs only; times ISO 8601 UTC).
              Above version 1, --previous names the current delegation, whose version this must exceed.
          acb-identity-keys cosign <delegation.json> --root <root.pem>
              show what it delegates, and add one root holder's signature once they type its version; run once per holder
          acb-identity-keys check <delegation.json> [--root-public <spki> ...] [--without <keyid> ...] [--threshold n]
              say how many roots signed it and what it delegates; it passes only once every root checked signed
              (at least 2 are needed), so that replacing any one root later still leaves it enough. The roots
              checked are the ones this build ships plus any --root-public; --without leaves a shipped one out, and says so.
          acb-identity-keys trust <spki> ...
              print the roots (P-256 only) as C# for IdentityTrust.Shipped
          acb-identity-keys pin <delegation.json>
              print a delegation's digest as C# for IdentityTrust.Shipped's pinned delegations, for the release that
              removes a root; it must check against the roots this build ships
          acb-identity-keys revoke <keyid>
              print a stolen key's id as C# for IdentityTrust.Shipped's revoked keys: no signature made with it counts
        """;

    /// <summary>The fewest roots a delegation may need: one holder alone never vouches for a signing key.</summary>
    public const int MinimumThreshold = 2;

    /// <summary>The exit code for anything refused: a wrong command line, a file, a key or a passphrase.</summary>
    public const int Refused = 64;

    /// <summary>How root keys are encrypted: AES-256 under PBKDF2-SHA256 at 600,000 iterations (OWASP's figure for it).</summary>
    private static readonly PbeParameters RootEncryption = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    public int Run(string[] args)
    {
        try
        {
            return args switch
            {
                ["new-key", var path] => NewKey(path),
                ["delegate", var path, .. var rest] => Delegate(path, rest),
                ["cosign", var path, "--root", var root] => Cosign(path, root),
                ["check", var path, .. var rest] => Check(path, rest),
                ["pin", var path] => Pin(path),
                ["trust", .. var spkis] when spkis.Length > 0 => Trust(spkis),
                ["revoke", var keyid] => Revoke(keyid),
                _ => ShowUsage()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException or JsonException
                                       or ArgumentException or ArchiveRejectedException)
        {
            return Fail(ex.Message);
        }
    }

    private int NewKey(string path)
    {
        if (askSecret($"Passphrase for {path}: ") is not { } passphrase)
        {
            return Fail("no passphrase was given, so no key was made.");
        }

        if (Seal.PassphraseProblem(passphrase) is { } weak)
        {
            return Fail(weak + ", so no key was made.");
        }

        if (askSecret("Again: ") != passphrase)
        {
            return Fail("the two passphrases differ, so no key was made.");
        }

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (StreamWriter writer = new(path, options))
        {
            writer.Write(key.ExportEncryptedPkcs8PrivateKeyPem(passphrase, RootEncryption));
        }

        (string spki, string keyid) = IdentityIssuer.Describe(key);
        output.WriteLine("keyid " + keyid);
        output.WriteLine("spki  " + spki);
        return 0;
    }

    private int Delegate(string path, string[] rest)
    {
        int version = 0;
        int? previous = null;
        List<DelegatedKey> keys = [];
        if (Options(rest, out List<(string Flag, string Value)> options, "--version", "--previous") is { } wrong)
        {
            return Fail(wrong);
        }

        foreach ((string flag, string value) in options)
        {
            switch (flag)
            {
                case "--version":
                    if (Number(flag, value, out version) is { } notNumber)
                    {
                        return Fail(notNumber);
                    }

                    break;
                case "--previous":
                    previous = Read(File.ReadAllText(value)).Version;
                    break;
                case "--key":
                    string[] parts = value.Split(',');
                    if (parts.Length is < 3 or > 4)
                    {
                        return Fail("--key is <public.pem>,<notBefore>,<notAfter>[,<revokedFrom>]");
                    }

                    DateTimeOffset notBefore = Time(parts[1]);
                    DateTimeOffset notAfter = Time(parts[2]);
                    if (notBefore >= notAfter)
                    {
                        return Fail($"{parts[0]}: notBefore must be before notAfter.");
                    }

                    using (ECDsa key = LoadPublic(parts[0]))
                    {
                        (string spki, string keyid) = IdentityIssuer.Describe(key);
                        keys.Add(new DelegatedKey(keyid, spki, notBefore, notAfter, parts.Length == 4 ? Time(parts[3]) : null));
                    }

                    break;
                default:
                    return ShowUsage();
            }
        }

        if (version <= 0 || keys.Count == 0)
        {
            return Fail("delegate needs --version (above every earlier delegation's) and at least one --key");
        }

        if (version > 1 && previous is null)
        {
            return Fail("above version 1, delegate needs --previous <the current delegation.json>, so the new version is checked against it");
        }

        if (version <= previous)
        {
            return Fail($"--version {version} is not above the previous delegation's {previous}");
        }

        DsseEnvelope unsigned = IdentityIssuer.Delegate(version, DateTimeOffset.UtcNow, keys, []);
        using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write))
        using (StreamWriter writer = new(file))
        {
            writer.Write(Dsse.ToJson(unsigned));
        }

        output.WriteLine($"wrote {path}: version {version}, {keys.Count} key(s), no signatures yet");
        return 0;
    }

    /// <summary>
    ///     Shows the holder every key the delegation vouches for, and signs only once they type its version back, so
    ///     a delegation swapped on the way between holders is seen before it is signed, never after.
    /// </summary>
    private int Cosign(string path, string rootPem)
    {
        DsseEnvelope envelope = Dsse.FromJson(File.ReadAllText(path));
        Delegation delegation = Identity.ReadUnchecked(envelope);
        if (KeysProblem(delegation) is { } bad)
        {
            return Fail(bad + ", so nothing was signed.");
        }

        // Every id printed below is 64 hex digits, so nothing from the file can move the cursor over what is shown.
        if (envelope.Signatures.FirstOrDefault(s => !IsKeyId(s.Keyid)) is { } odd)
        {
            return Fail($"a signature on it has the id '{odd.Keyid}', which is not a key id (64 lowercase hex digits), so nothing was signed.");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{path}: delegation version {delegation.Version}, issued {delegation.Issued:u}, delegating:"));
        WriteKeys(delegation);
        output.WriteLine(envelope.Signatures.Count == 0
            ? "No root has signed it yet."
            : "Signatures on it already (not checked here; check does that): " + string.Join(", ", envelope.Signatures.Select(s => s.Keyid)));
        output.WriteLine("Compare every key id above with the list the service's operators gave you by some other way than this file.");
        string version = delegation.Version.ToString(CultureInfo.InvariantCulture);
        if (ask($"Type the version ({version}) to sign it: ")?.Trim() != version)
        {
            return Fail("the version was not typed, so nothing was signed.");
        }

        using ECDsa? root = LoadRoot(rootPem);
        if (root is null)
        {
            return Fail("no passphrase was given, so nothing was signed.");
        }

        string keyid = IdentityIssuer.Describe(root).Keyid;
        DsseEnvelope signed = IdentityIssuer.Cosign(envelope, root, out CosignResult result);
        if (result == CosignResult.AlreadySigned)
        {
            output.WriteLine($"already signed by you ({keyid}); nothing changed");
            return 0;
        }

        Replace(path, Dsse.ToJson(signed));
        output.WriteLine(result == CosignResult.Replaced
            ? $"a signature under your id ({keyid}) did not check against this delegation, so yours replaced it; {signed.Signatures.Count} signature(s) now"
            : $"signed by {keyid}; {signed.Signatures.Count} signature(s) now");
        return 0;
    }

    private int Check(string path, string[] rest)
    {
        List<string> roots = [.. _shipped.Roots.Select(r => r.Spki)];
        List<string> without = [];
        int threshold = _shipped.Roots.Count > 0 ? _shipped.Threshold : MinimumThreshold;
        if (Options(rest, out List<(string Flag, string Value)> options, "--threshold") is { } wrong)
        {
            return Fail(wrong);
        }

        foreach ((string flag, string value) in options)
        {
            switch (flag)
            {
                case "--root-public":
                    if (RootProblem(value) is { } notRoot)
                    {
                        return Fail(notRoot);
                    }

                    roots.Add(value);
                    break;
                case "--without":
                    if (_shipped.Roots.All(r => r.Keyid != value))
                    {
                        return Fail($"--without '{value}' is not a root this build ships.");
                    }

                    without.Add(value);
                    break;
                case "--threshold":
                    if (Number(flag, value, out threshold) is { } notNumber)
                    {
                        return Fail(notNumber);
                    }

                    break;
                default:
                    return ShowUsage();
            }
        }

        if (threshold < MinimumThreshold)
        {
            return Fail($"--threshold {threshold} is below {MinimumThreshold}: one holder alone never vouches for a signing key.");
        }

        DsseEnvelope envelope = Dsse.FromJson(File.ReadAllText(path));
        IdentityTrust trust = new([
                .. roots.Select(r => new IdentityRoot(Identity.KeyId(Convert.FromBase64String(r)), r)).Where(r => !without.Contains(r.Keyid))
            ], threshold,
            new HashSet<string>(StringComparer.Ordinal));
        foreach (string keyid in without.Distinct(StringComparer.Ordinal))
        {
            output.WriteLine($"left out by --without: shipped root {keyid}, which is not checked");
        }

        (Delegation delegation, IReadOnlyCollection<string> signedBy) = Identity.CheckDelegation(envelope, trust);
        if (KeysProblem(delegation) is { } bad)
        {
            return Fail(bad + ".");
        }

        int given = trust.Roots.Select(r => r.Keyid).Distinct(StringComparer.Ordinal).Count();
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"version {delegation.Version}, issued {delegation.Issued:u}, signed by {signedBy.Count} of {given} roots ({threshold} needed): {string.Join(", ", signedBy)}"));
        WriteKeys(delegation);
        if (signedBy.Count < given)
        {
            // A backup keeps its delegation for good, so a root replaced later must still leave every delegation at the threshold.
            return Fail(string.Create(CultureInfo.InvariantCulture,
                $"{given - signedBy.Count} of the roots checked did not sign it. Every holder whose key is still in use signs each delegation, so that replacing any one root later leaves it at the threshold; have them cosign, or, for a root the next release removes, leave it out with --without."));
        }

        return 0;
    }

    /// <summary>
    ///     A delegation's line for <see cref="IdentityTrust.Pinned" />, refused unless it checks against the roots this
    ///     build ships, so only a delegation the holders made is ever pinned.
    /// </summary>
    private int Pin(string path)
    {
        if (_shipped.Roots.Count == 0)
        {
            return Fail("this build ships no roots, so there is nothing to check the delegation against and nothing to pin.");
        }

        DsseEnvelope envelope = Dsse.FromJson(File.ReadAllText(path));
        Delegation delegation = Identity.CheckDelegation(envelope, _shipped with { Pinned = new HashSet<string>(StringComparer.Ordinal) }).Delegation;
        if (KeysProblem(delegation) is { } bad)
        {
            return Fail(bad + ".");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"\"{Identity.DelegationDigest(envelope)}\", // delegation version {delegation.Version}, issued {delegation.Issued:u}"));
        return 0;
    }

    /// <summary>Why a delegated key is not shown or signed: not P-256, not under its own id, or a window that ends before it starts.</summary>
    private static string? KeysProblem(Delegation delegation)
    {
        for (int i = 0; i < delegation.Keys.Count; i++)
        {
            DelegatedKey key = delegation.Keys[i];
            byte[] spki;
            try
            {
                spki = Convert.FromBase64String(key.Spki);
            }
            catch (FormatException)
            {
                spki = [];
            }

            if (!Identity.IsP256Spki(spki) || Identity.KeyId(spki) != key.Keyid || key.NotBefore >= key.NotAfter)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"key {i + 1} ('{key.Keyid}') is not a P-256 key under its own id in a window that ends after it starts");
            }
        }

        return null;
    }

    private static bool IsKeyId(string keyid) => keyid.Length == 64 && keyid.All(char.IsAsciiHexDigitLower);

    private void WriteKeys(Delegation delegation)
    {
        foreach (DelegatedKey key in delegation.Keys)
        {
            string revoked = key.RevokedFrom is { } r ? string.Create(CultureInfo.InvariantCulture, $", revoked from {r:u}") : "";
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {key.Keyid}  {key.NotBefore:u} to {key.NotAfter:u}{revoked}"));
        }
    }

    /// <summary>The roots' lines for a release, refused unless each is a P-256 public key given once.</summary>
    private int Trust(string[] spkis)
    {
        if (spkis.Select(RootProblem).FirstOrDefault(p => p is not null) is { } bad)
        {
            return Fail(bad);
        }

        if (spkis.Distinct(StringComparer.Ordinal).Count() != spkis.Length)
        {
            return Fail("the same root is given twice.");
        }

        foreach (string spki in spkis)
        {
            output.WriteLine($"new IdentityRoot(\"{Identity.KeyId(Convert.FromBase64String(spki))}\", \"{spki}\"),");
        }

        return 0;
    }

    /// <summary>Why <paramref name="spki" /> is not a root's public key: base64 of a P-256 SubjectPublicKeyInfo.</summary>
    private static string? RootProblem(string spki)
    {
        try
        {
            return Identity.IsP256Spki(Convert.FromBase64String(spki)) ? null : $"'{spki}' is not a P-256 public key (SubjectPublicKeyInfo, base64).";
        }
        catch (FormatException)
        {
            return $"'{spki}' is not base64.";
        }
    }

    /// <summary>
    ///     The flag and value pairs after a command's file. Refused (with why) for a flag left without its value, or
    ///     one of <paramref name="once" /> given twice, either of which would otherwise drop an argument without a word.
    /// </summary>
    private static string? Options(string[] rest, out List<(string Flag, string Value)> options, params string[] once)
    {
        options = [];
        if (rest.Length % 2 != 0)
        {
            return $"'{rest[^1]}' has no value after it; every option is a flag and its value.";
        }

        for (int i = 0; i < rest.Length; i += 2)
        {
            string flag = rest[i];
            if (once.Contains(flag) && options.Any(o => o.Flag == flag))
            {
                return $"{flag} is given twice.";
            }

            options.Add((flag, rest[i + 1]));
        }

        return null;
    }

    private static string? Number(string flag, string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? null : $"{flag} {text} is not a whole number in range.";

    /// <summary>
    ///     Writes <paramref name="text" /> to <paramref name="path" /> through a file beside it, moved over the old one
    ///     only once it is whole, so an interrupted write leaves the old file as it was.
    /// </summary>
    private static void Replace(string path, string text)
    {
        string next = path + ".new";
        using (FileStream file = new(next, FileMode.Create, FileAccess.Write))
        {
            using (StreamWriter writer = new(file, leaveOpen: true))
            {
                writer.Write(text);
            }

            file.Flush(flushToDisk: true);
        }

        File.Move(next, path, overwrite: true);
    }

    /// <summary>
    ///     A stolen key's line for the release. It has no time: whoever holds the key writes the signing time, so a
    ///     revocation from any time after the key's start is dodged by dating a forgery before it.
    /// </summary>
    private int Revoke(string keyid)
    {
        if (!IsKeyId(keyid))
        {
            return Fail($"'{keyid}' is not a key id (64 lowercase hex digits).");
        }

        output.WriteLine($"\"{keyid}\",");
        return 0;
    }

    /// <summary>A root key: an encrypted PEM, opened with the passphrase the holder types. Null when none is given.</summary>
    private ECDsa? LoadRoot(string pemPath)
    {
        string text = File.ReadAllText(pemPath);
        if (!PemEncoding.TryFind(text, out PemFields fields) || text[fields.Label] != "ENCRYPTED PRIVATE KEY")
        {
            throw new ArgumentException($"{pemPath} is not an encrypted private key; root keys are made with new-key.");
        }

        if (askSecret($"Passphrase for {pemPath}: ") is not { } passphrase)
        {
            return null;
        }

        ECDsa key = ECDsa.Create();
        key.ImportFromEncryptedPem(text, passphrase);
        return P256(key, pemPath);
    }

    /// <summary>A signing key: its public PEM only, so the service's private key never needs to leave its vault.</summary>
    private static ECDsa LoadPublic(string pemPath)
    {
        string text = File.ReadAllText(pemPath);
        if (!PemEncoding.TryFind(text, out PemFields fields) || text[fields.Label] != "PUBLIC KEY")
        {
            throw new ArgumentException($"{pemPath} is not a public key PEM; give the signing key's public key, never its private one.");
        }

        ECDsa key = ECDsa.Create();
        key.ImportFromPem(text);
        return P256(key, pemPath);
    }

    private static ECDsa P256(ECDsa key, string pemPath)
    {
        if (Identity.IsP256Spki(key.ExportSubjectPublicKeyInfo()))
        {
            return key;
        }

        key.Dispose();
        throw new ArgumentException($"{pemPath} is not a P-256 key.");
    }

    private static Delegation Read(string json)
    {
        DsseEnvelope envelope = Dsse.FromJson(json);
        if (envelope.PayloadType != Identity.DelegationPayloadType)
        {
            throw new ArgumentException("That is not a delegation.");
        }

        return JsonSerializer.Deserialize(Convert.FromBase64String(envelope.Payload), ArchiveJson.Strict.Delegation)
               ?? throw new JsonException("The delegation is empty.");
    }

    private static DateTimeOffset Time(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>
    ///     Says why something was refused. Control characters and the marks that reorder text are written as
    ///     escapes, since a message may quote the files it read.
    /// </summary>
    private int Fail(string message)
    {
        StringBuilder shown = new(message.Length);
        foreach (char c in message)
        {
            if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            {
                shown.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            }
            else
            {
                shown.Append(c);
            }
        }

        error.WriteLine(shown.ToString());
        return Refused;
    }

    private int ShowUsage()
    {
        error.WriteLine(Usage);
        return Refused;
    }
}
