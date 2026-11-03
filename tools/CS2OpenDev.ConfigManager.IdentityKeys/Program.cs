using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;

// The key ceremony for the identity level (docs/identity-service.md, "Keys"). Every command works on files the
// holder keeps offline; nothing here talks to a network.
const string Usage = """
    Usage:
      acb-identity-keys new-key <private.pem>
          make a P-256 key; prints its id and public key (SubjectPublicKeyInfo, base64)
      acb-identity-keys delegate <delegation.json> --version n --key <key.pem>,<notBefore>,<notAfter>[,<revokedFrom>] ...
          write an unsigned delegation of the service's signing keys (public or private PEM; times ISO 8601 UTC)
      acb-identity-keys cosign <delegation.json> --root <root.pem>
          add one root holder's signature; run once per holder until enough have signed
      acb-identity-keys check <delegation.json> --root-public <spki> ... [--threshold 2]
          say which roots signed it and what it delegates
      acb-identity-keys trust <spki> ...
          print the roots as C# for IdentityTrust.Shipped
    """;

try
{
    return args switch
    {
        ["new-key", var path] => NewKey(path),
        ["delegate", var path, .. var rest] => Delegate(path, rest),
        ["cosign", var path, "--root", var root] => Cosign(path, root),
        ["check", var path, .. var rest] => Check(path, rest),
        ["trust", .. var spkis] when spkis.Length > 0 => Trust(spkis),
        _ => Fail(Usage)
    };
}
catch (Exception ex) when (ex is IOException or CryptographicException or FormatException or JsonException or ArgumentException or ArchiveRejectedException)
{
    return Fail(ex.Message);
}

static int NewKey(string path)
{
    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write))
    using (StreamWriter writer = new(file))
    {
        writer.Write(key.ExportPkcs8PrivateKeyPem());
    }

    (string spki, string keyid) = IdentityIssuer.Describe(key);
    Console.WriteLine("keyid " + keyid);
    Console.WriteLine("spki  " + spki);
    return 0;
}

static int Delegate(string path, string[] rest)
{
    int version = 0;
    List<DelegatedKey> keys = [];
    for (int i = 0; i < rest.Length - 1; i += 2)
    {
        switch (rest[i])
        {
            case "--version":
                version = int.Parse(rest[i + 1], CultureInfo.InvariantCulture);
                break;
            case "--key":
                string[] parts = rest[i + 1].Split(',');
                if (parts.Length is < 3 or > 4)
                {
                    return Fail("--key is <key.pem>,<notBefore>,<notAfter>[,<revokedFrom>]");
                }

                using (ECDsa key = Load(parts[0]))
                {
                    (string spki, string keyid) = IdentityIssuer.Describe(key);
                    keys.Add(new DelegatedKey(keyid, spki, Time(parts[1]), Time(parts[2]), parts.Length == 4 ? Time(parts[3]) : null));
                }

                break;
            default:
                return Fail(Usage);
        }
    }

    if (version <= 0 || keys.Count == 0)
    {
        return Fail("delegate needs --version (above every earlier delegation's) and at least one --key");
    }

    DsseEnvelope unsigned = IdentityIssuer.Delegate(version, DateTimeOffset.UtcNow, keys, []);
    using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write);
    using StreamWriter writer = new(file);
    writer.Write(IdentityIssuer.ToJson(unsigned));
    Console.WriteLine($"wrote {path}: version {version}, {keys.Count} key(s), no signatures yet");
    return 0;
}

static int Cosign(string path, string rootPem)
{
    DsseEnvelope envelope = IdentityIssuer.FromJson(File.ReadAllText(path));
    using ECDsa root = Load(rootPem);
    DsseEnvelope signed = IdentityIssuer.Cosign(envelope, root);
    File.WriteAllText(path, IdentityIssuer.ToJson(signed));
    Console.WriteLine($"signed by {IdentityIssuer.Describe(root).Keyid}; {signed.Signatures.Count} signature(s) now");
    return 0;
}

static int Check(string path, string[] rest)
{
    List<string> roots = [];
    int threshold = 2;
    for (int i = 0; i < rest.Length - 1; i += 2)
    {
        switch (rest[i])
        {
            case "--root-public":
                roots.Add(rest[i + 1]);
                break;
            case "--threshold":
                threshold = int.Parse(rest[i + 1], CultureInfo.InvariantCulture);
                break;
            default:
                return Fail(Usage);
        }
    }

    DsseEnvelope envelope = IdentityIssuer.FromJson(File.ReadAllText(path));
    IdentityTrust trust = new([.. roots.Select(r => new IdentityRoot(Identity.KeyId(Convert.FromBase64String(r)), r))], threshold,
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
    Delegation delegation = Identity.CheckDelegation(envelope, trust);
    Console.WriteLine($"version {delegation.Version}, issued {delegation.Issued:u}, signed by at least {threshold} of {roots.Count} roots");
    foreach (DelegatedKey key in delegation.Keys)
    {
        Console.WriteLine($"  {key.Keyid}  {key.NotBefore:u} to {key.NotAfter:u}" + (key.RevokedFrom is { } r ? $", revoked from {r:u}" : ""));
    }

    return 0;
}

static int Trust(string[] spkis)
{
    foreach (string spki in spkis)
    {
        Console.WriteLine($"new IdentityRoot(\"{Identity.KeyId(Convert.FromBase64String(spki))}\", \"{spki}\"),");
    }

    return 0;
}

static ECDsa Load(string pemPath)
{
    ECDsa key = ECDsa.Create();
    key.ImportFromPem(File.ReadAllText(pemPath));
    return key.KeySize == 256 ? key : throw new ArgumentException($"{pemPath} is not a P-256 key.");
}

static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 64;
}
