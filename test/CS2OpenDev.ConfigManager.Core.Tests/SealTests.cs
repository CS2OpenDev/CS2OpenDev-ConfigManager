using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Restoring;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The <c>seal</c> level: its key and signature against known answers (RFC 9106's Argon2id vector, and values
///     computed independently with the reference Argon2 code through argon2-cffi), and every way a sealed archive
///     changed after sealing, or opened with the wrong passphrase, is refused.
/// </summary>
public class SealTests
{
    private const string Passphrase = "correct horse battery staple";

    /// <summary>The salt the known answers use: bytes 0 to 15.</summary>
    private static readonly SealKdf FixedKdf = new(Seal.Argon2idName, Seal.MemoryKiB, Seal.Iterations, Seal.Parallelism,
        Convert.ToBase64String(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()));

    /// <summary>RFC 9106 section 5.3: Argon2id with a secret and associated data, 32 KiB, 3 passes, 4 lanes.</summary>
    [Test]
    public async Task Argon2id_MatchesRfc9106sTestVector()
    {
        byte[] tag = Seal.Argon2id(Fill(32, 0x01), Fill(16, 0x02), 32, 3, 4, 32, Fill(8, 0x03), Fill(12, 0x04));

        await Assert.That(Convert.ToHexStringLower(tag)).IsEqualTo("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");
    }

    /// <summary>The seal's own settings (64 MiB, 3 passes, 1 lane) over a known passphrase and salt, as argon2-cffi computes them.</summary>
    [Test]
    public async Task TheSealsKey_MatchesTheReferenceImplementation()
    {
        byte[] key = Seal.DeriveKey(Passphrase, FixedKdf);

        await Assert.That(Convert.ToHexStringLower(key)).IsEqualTo("0d1a3c6523c8f06e4e0af9c515aa5b5448cfebd6838f2d52c3d8b6ef8ddc3c2e");
    }

    /// <summary>HMAC-SHA256 under that key over DSSE's encoding of a payload, as Python's hmac computes it.</summary>
    [Test]
    public async Task TheSealsSignature_IsHmacSha256OverDssesEncoding()
    {
        SealKey key = SealKey.Derive(Passphrase, FixedKdf);

        byte[] signature = key.Sign(Dsse.PreAuthEncoding(ArchiveFormat.PayloadType, """{"test":"seal"}"""u8));

        await Assert.That(Convert.ToHexStringLower(signature)).IsEqualTo("bf9377c7bffb241481d41d9ce5f3fe4f5a180295c491635052d968c22a4beb63");
    }

    /// <summary>
    ///     The key is made from the passphrase's UTF-8 exactly as typed, never normalised: acb's invariant globalisation
    ///     cannot normalise, and the plugin must make the same key. So an accented e typed as one character, or as e
    ///     and a combining accent, makes two keys, and a passphrase with such characters is warned about when sealing.
    /// </summary>
    [Test]
    public async Task APassphrase_IsTakenByteForByte_SoAnAccentTypedTwoWaysMakesTwoKeys()
    {
        string composed = Convert.ToHexStringLower(Seal.DeriveKey("caf\u00e9 au lait!", FixedKdf));
        string decomposed = Convert.ToHexStringLower(Seal.DeriveKey("cafe\u0301 au lait!", FixedKdf));

        await Assert.That(composed).IsEqualTo("effc68bfb5537d43aa3aced32dc372d36ddd744169d5d396f0f641a47d613a61");
        await Assert.That(decomposed).IsNotEqualTo(composed);
        await Assert.That(Seal.MayBeTypedDifferently("caf\u00e9 au lait!")).IsTrue();
        await Assert.That(Seal.MayBeTypedDifferently(Passphrase)).IsFalse();
        await Assert.That(Seal.MayBeTypedDifferently(Seal.GeneratePhrase())).IsFalse();
    }

    [Test]
    public async Task ASealedArchive_RecordsItsKeySettingsInTheSignedStatement_UnderTheirNames()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));

        using JsonDocument envelope = JsonDocument.Parse(ArchiveV2Tests.Entry(zip, ArchiveFormat.SignatureEntry));
        JsonElement signatures = envelope.RootElement.GetProperty("signatures");
        using JsonDocument statement = JsonDocument.Parse(Convert.FromBase64String(envelope.RootElement.GetProperty("payload").GetString()!));
        JsonElement predicate = statement.RootElement.GetProperty("predicate");
        JsonElement kdf = predicate.GetProperty("kdf");

        await Assert.That(predicate.GetProperty("level").GetString()).IsEqualTo("seal");
        await Assert.That(kdf.GetProperty("name").GetString()).IsEqualTo("argon2id");
        await Assert.That(kdf.GetProperty("memoryKiB").GetInt32()).IsEqualTo(65536);
        await Assert.That(kdf.GetProperty("iterations").GetInt32()).IsEqualTo(3);
        await Assert.That(kdf.GetProperty("parallelism").GetInt32()).IsEqualTo(1);
        await Assert.That(Convert.FromBase64String(kdf.GetProperty("salt").GetString()!).Length).IsEqualTo(16);
        await Assert.That(signatures.GetArrayLength()).IsEqualTo(1);
        await Assert.That(signatures[0].GetProperty("keyid").GetString()).IsEqualTo("seal");
    }

    [Test]
    public async Task ASealedArchive_OpensWithItsPassphrase_AtTheSealLevel()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));

        VerifiedArchive archive = ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase, MinimumLevel = TrustLevel.Seal });

        await Assert.That(archive.Level).IsEqualTo(TrustLevel.Seal);
        await Assert.That(archive.SealKey).IsNotNull();
        await Assert.That(archive.Manifest.Entries.Count).IsEqualTo(3);
    }

    [Test]
    public async Task TwoSealsWithOnePassphrase_HaveTheirOwnSalts()
    {
        SealKey first = SealKey.Create(Passphrase);
        SealKey second = SealKey.Create(Passphrase);

        await Assert.That(first.Kdf.Salt).IsNotEqualTo(second.Kdf.Salt);
        await Assert.That(first.Bytes.SequenceEqual(second.Bytes)).IsFalse();
    }

    [Test]
    public async Task ASealedArchive_WithNoPassphrase_IsNotOpened_AndSaysOneIsNeeded()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));

        SealNotOpenedException? refused = Catch(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform));

        await Assert.That(refused).IsNotNull();
        await Assert.That(refused!.Wrong).IsFalse();
        await Assert.That(refused.Message).IsEqualTo(SealNotOpenedException.NeededMessage);
    }

    [Test]
    [Arguments("correct horse battery stapl")]
    [Arguments("correct horse battery staple ")]
    [Arguments("Correct horse battery staple")]
    public async Task ASealedArchive_WithAnotherPassphrase_IsRefused(string passphrase)
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));

        SealNotOpenedException? refused = Catch(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = passphrase }));

        await Assert.That(refused?.Wrong).IsTrue();
    }

    /// <summary>A changed file fails its manifest entry even with the right passphrase; the seal covers the manifest, which covers the files.</summary>
    [Test]
    public async Task ASealedArchiveWithAChangedFile_IsRefused_WithTheRightPassphrase()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, "files/cs2/local/cfg/cs2_user_keys_0_slot0.vcfg", _ => "\"config\" { \"bind\" \"quit\" }"u8.ToArray());

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("does not match the manifest");
    }

    /// <summary>
    ///     The manifest edited and the statement's digest rewritten to match, with the old seal kept: the seal no
    ///     longer matches what it signs, so it reads as the wrong passphrase.
    /// </summary>
    [Test]
    public async Task ASealedArchiveWithItsManifestAndStatementRewritten_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        byte[] manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ArchiveV2Tests.Entry(zip, ArchiveFormat.ManifestEntry))
            .Replace("69413322", "11111111", StringComparison.Ordinal));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.ManifestEntry, _ => manifest);
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, envelope => ChangeStatement(envelope, statement =>
            statement with { Subject = [new StatementSubject(ArchiveFormat.ManifestEntry, new DigestSet(Dsse.Sha256Hex(manifest)))] }));

        await Assert.That(Catch(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))?.Wrong).IsTrue();
    }

    /// <summary>A salt changed in the statement makes another key, and the seal does not match it.</summary>
    [Test]
    public async Task ASealedArchiveWithAnotherSalt_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, envelope => ChangeStatement(envelope, statement =>
            statement with { Predicate = statement.Predicate with { Kdf = FixedKdf } }));

        await Assert.That(Catch(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))?.Wrong).IsTrue();
    }

    /// <summary>
    ///     Key settings other than the ones acb writes are refused before any key is made, so an archive cannot set
    ///     the memory or passes acb spends, or weaken them to make guessing cheaper.
    /// </summary>
    [Test]
    [Arguments("argon2i", 65536, 3, 1, "not the ones this version uses")]
    [Arguments("argon2id", 4_194_304, 3, 1, "not the ones this version uses")]
    [Arguments("argon2id", 65536, 1, 1, "not the ones this version uses")]
    [Arguments("argon2id", 65536, 3, 8, "not the ones this version uses")]
    public async Task ASealWithOtherKeySettings_IsRefused(string name, int memory, int passes, int lanes, string expected)
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, envelope => ChangeStatement(envelope, statement =>
            statement with { Predicate = statement.Predicate with { Kdf = statement.Predicate.Kdf! with { Name = name, MemoryKiB = memory, Iterations = passes, Parallelism = lanes } } }));

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))
            .Throws<ArchiveRejectedException>().WithMessageContaining(expected);
    }

    [Test]
    [Arguments("AAAA", "salt is not 16 bytes")]
    [Arguments("not base64!", "salt is not base64")]
    public async Task ASealWithABadSalt_IsRefused(string salt, string expected)
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, envelope => ChangeStatement(envelope, statement =>
            statement with { Predicate = statement.Predicate with { Kdf = statement.Predicate.Kdf! with { Salt = salt } } }));

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))
            .Throws<ArchiveRejectedException>().WithMessageContaining(expected);
    }

    [Test]
    public async Task ASealStatementWithoutKeySettings_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, envelope => ChangeStatement(envelope, statement =>
            statement with { Predicate = new TrustPredicate("seal") }));

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("names no key settings");
    }

    /// <summary>A hash-level statement carrying a seal's settings claims more than its level; refused like one carrying a signature.</summary>
    [Test]
    public async Task AHashStatementWithKeySettings_IsRefused()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp);
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, envelope => ChangeStatement(envelope, statement =>
            statement with { Predicate = new TrustPredicate("hash", FixedKdf) }));

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform))
            .Throws<ArchiveRejectedException>().WithMessageContaining("unsigned but carries");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ASealWithoutExactlyOneSealSignature_IsRefused(int shape)
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, bytes =>
        {
            DsseEnvelope envelope = JsonSerializer.Deserialize(bytes, ArchiveJson.Strict.DsseEnvelope)!;
            DsseSignature seal = envelope.Signatures[0];
            DsseEnvelope changed = envelope with
            {
                Signatures = shape switch
                {
                    0 => [],
                    1 => [seal, seal],
                    2 => [seal with { Keyid = "identity" }],
                    _ => [seal with { Sig = "not base64!" }]
                }
            };
            return JsonSerializer.SerializeToUtf8Bytes(changed, ArchiveJson.Strict.DsseEnvelope);
        });

        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }))
            .Throws<ArchiveRejectedException>();
    }

    /// <summary>
    ///     Anyone can strip a seal and rewrite the statement at the hash level: that is what the hash level means. It
    ///     reads as hash, never as seal, and a policy that asks for at least seal refuses it.
    /// </summary>
    [Test]
    public async Task ASealStrippedToTheHashLevel_ReadsAsHash_AndFailsAMinimumOfSeal()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        zip = ArchiveV2Tests.Rewrite(zip, temp, ArchiveFormat.SignatureEntry, bytes =>
        {
            byte[] stripped = ChangeStatement(bytes, statement => statement with { Predicate = new TrustPredicate("hash") });
            DsseEnvelope envelope = JsonSerializer.Deserialize(stripped, ArchiveJson.Strict.DsseEnvelope)!;
            return JsonSerializer.SerializeToUtf8Bytes(envelope with { Signatures = [] }, ArchiveJson.Strict.DsseEnvelope);
        });

        await Assert.That(ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform).Level).IsEqualTo(TrustLevel.Hash);
        await Assert.That(() => ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { MinimumLevel = TrustLevel.Seal }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("at least 'seal' is required");
    }

    /// <summary>The administrator's run gets the key, not the passphrase; a key made for another archive does not open this one.</summary>
    [Test]
    public async Task AHandedOnKey_OpensItsOwnArchive_AndNoOther()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        byte[] key = ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { Passphrase = Passphrase }).SealKey!.Bytes.ToArray();
        using TempDir other = new();
        string otherZip = ArchiveV2Tests.Write(other, seal: SealKey.Create(Passphrase));

        await Assert.That(ArchiveReader.Open(zip, ArchiveV2Tests.AnyPlatform with { SealKey = key }).Level).IsEqualTo(TrustLevel.Seal);
        await Assert.That(Catch(() => ArchiveReader.Open(otherZip, ArchiveV2Tests.AnyPlatform with { SealKey = key }))?.Wrong).IsTrue();
    }

    [Test]
    public async Task OpeningASealedArchive_AsksForThePassphrase_AndAgainAfterAWrongOne_ThreeTimesAtMost()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));
        List<bool> asked = [];

        VerifiedArchive? opened = RestoreSession.OpenArchive(zip, null, TrustLevel.Hash, out string? problem,
            again => { asked.Add(again); return asked.Count == 3 ? Passphrase : "wrong passphrase!"; });
        List<bool> askedWrong = [];
        VerifiedArchive? never = RestoreSession.OpenArchive(zip, null, TrustLevel.Hash, out string? wrongProblem,
            again => { askedWrong.Add(again); return "wrong passphrase!"; });

        await Assert.That(opened?.Level).IsEqualTo(TrustLevel.Seal).Because(problem ?? "");
        await Assert.That(asked).IsEquivalentTo([false, true, true]);
        await Assert.That(never).IsNull();
        await Assert.That(askedWrong.Count).IsEqualTo(RestoreSession.PassphraseTries);
        await Assert.That(wrongProblem).IsEqualTo(SealNotOpenedException.WrongMessage);
    }

    [Test]
    public async Task OpeningASealedArchive_WithNobodyToAsk_SaysHowToGiveThePassphrase()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp, seal: SealKey.Create(Passphrase));

        VerifiedArchive? archive = RestoreSession.OpenArchive(zip, null, TrustLevel.Hash, out string? problem);
        VerifiedArchive? declined = RestoreSession.OpenArchive(zip, null, TrustLevel.Hash, out string? declinedProblem, _ => null);

        await Assert.That(archive).IsNull();
        await Assert.That(problem).Contains("--passphrase-file");
        await Assert.That(declined).IsNull();
        await Assert.That(declinedProblem).IsEqualTo(SealNotOpenedException.NeededMessage);
    }

    /// <summary>An unsealed archive never asks: nothing about a passphrase reaches a hash-level backup.</summary>
    [Test]
    public async Task OpeningAnUnsealedArchive_NeverAsks()
    {
        using TempDir temp = new();
        string zip = ArchiveV2Tests.Write(temp);
        int asked = 0;

        VerifiedArchive? archive = RestoreSession.OpenArchive(zip, null, TrustLevel.Hash, out _, _ => { asked++; return Passphrase; });

        await Assert.That(archive?.Level).IsEqualTo(TrustLevel.Hash);
        await Assert.That(asked).IsEqualTo(0);
    }

    [Test]
    [Arguments("short phrase", true)]
    [Arguments("eleven char", false)]
    [Arguments("            ", false)]
    [Arguments("", false)]
    [Arguments("twelve chars", true)]
    public async Task APassphrase_NeedsTwelveCharacters(string passphrase, bool accepted)
    {
        await Assert.That(Seal.PassphraseProblem(passphrase) is null).IsEqualTo(accepted);
        if (!accepted)
        {
            await Assert.That(() => SealKey.Create(passphrase)).Throws<ArgumentException>();
        }
    }

    /// <summary>Characters are counted as a person sees them: an accent typed separately or an emoji is one.</summary>
    [Test]
    public async Task APassphrasesLength_CountsWhatAPersonSees()
    {
        string elevenSeen = string.Concat(Enumerable.Repeat("e\u0301", 11));
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 11));

        await Assert.That(Seal.PassphraseProblem(elevenSeen)).Contains("is 11 characters");
        await Assert.That(Seal.PassphraseProblem(emoji)).Contains("is 11 characters");
    }

    [Test]
    public async Task AGeneratedPhrase_IsSixWordsFromTheList_AndDiffersEachTime()
    {
        string first = Seal.GeneratePhrase();
        string second = Seal.GeneratePhrase();
        string[] words = first.Split(' ');

        await Assert.That(words.Length).IsEqualTo(Seal.GeneratedWords);
        await Assert.That(words.All(w => w.Length > 0 && w.All(c => c is (>= 'a' and <= 'z') or '-'))).IsTrue();
        await Assert.That(Seal.PassphraseProblem(first)).IsNull();
        await Assert.That(first).IsNotEqualTo(second);
    }

    private static SealNotOpenedException? Catch(Func<VerifiedArchive> open)
    {
        try
        {
            open();
            return null;
        }
        catch (SealNotOpenedException ex)
        {
            return ex;
        }
    }

    /// <summary>The envelope with its statement changed and re-encoded, its signatures kept as they were.</summary>
    private static byte[] ChangeStatement(byte[] envelopeBytes, Func<Statement, Statement> change)
    {
        DsseEnvelope envelope = JsonSerializer.Deserialize(envelopeBytes, ArchiveJson.Strict.DsseEnvelope)!;
        Statement statement = JsonSerializer.Deserialize(Convert.FromBase64String(envelope.Payload), ArchiveJson.Strict.Statement)!;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(change(statement), ArchiveJson.Strict.Statement);
        return JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(payload) }, ArchiveJson.Strict.DsseEnvelope);
    }

    private static byte[] Fill(int length, byte value) => [.. Enumerable.Repeat(value, length)];
}
