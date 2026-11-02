using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     The v2 archive: what the writer puts in, and every way an archive changed after it was made is refused by
///     the reader before anything could be restored from it.
/// </summary>
public class ArchiveV2Tests
{
    private static readonly string[] ExpectedEntries = ["README.txt", "capture.vdf", "files/cs2/local/cfg/cs2_user_keys_0_slot0.vcfg"];

    private static readonly DateTimeOffset Created = new(2026, 11, 1, 18, 2, 11, TimeSpan.Zero);

    internal static readonly ArchiveTool Tool = new("Advanced Config Backup", "test");

    /// <summary>The README inside every archive says --gsi is needed to restore GSI files, and never that a plan writes anything.</summary>
    [Test]
    public async Task TheReadme_NamesGsi_AndNeverSaysAPlanWrites()
    {
        string readme = BackupBuilder.Readme("Windows");

        await Assert.That(readme).Contains("a restore writes them only when given --gsi");
        await Assert.That(readme).Contains("Give --gsi to acb plan");
        await Assert.That(readme).DoesNotContain("plan and restore write");
    }

    [Test]
    public async Task ACaptureThatSavedNothing_WritesNoZip()
    {
        using TempDir temp = new();
        string zip = temp.Sub("backup.zip");
        CaptureOptions options = new() { SteamRoot = temp.Sub("no-steam-here"), Creator = "test" };
        options.Include.Clear();
        options.Include.Add(Sections.Cs2Game);

        RunReport report = BackupBuilder.Create(options, zip, Tool, Created, CancellationToken.None);

        await Assert.That(BackupBuilder.HoldsAnything(report)).IsFalse();
        await Assert.That(File.Exists(zip)).IsFalse();
        await Assert.That(File.Exists(zip + ".partial")).IsFalse();
    }

    [Test]
    public async Task ACancelledCapture_StopsBeforeTheNextPart()
    {
        using FakeSteam steam = new();
        using TempDir temp = new();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test", Cancellation = cancelled.Token };
        options.Include.Clear();
        options.Include.AddRange(ThreeSections);

        await Assert.That(() => ConfigBackup.Capture(options, temp.Sub("staging"), null)).Throws<OperationCanceledException>();
        await Assert.That(File.Exists(Path.Combine(temp.Sub("staging"), ConfigBackup.ManifestName))).IsFalse();
    }

    [Test]
    public async Task BackupBuilder_WritesACaptureAsAV2Archive_ThatReadsBack()
    {
        if (ArchiveFormat.CurrentPlatform is null)
        {
            Skip.Test("backups are made on Windows x64 and Linux x64 only");
        }

        using FakeSteam steam = new();
        steam.Write(FakeSteam.Cfg("autoexec.cfg"), "echo hi\nrcon_password hunter2\n");
        using TempDir temp = new();
        string zip = temp.Sub("backup.zip");
        CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test" };
        options.Include.Clear();
        options.Include.AddRange([Sections.Cs2, Sections.Cs2Game, Sections.Launch]);

        RunReport report = BackupBuilder.Create(options, zip, new ArchiveTool("Advanced Config Backup", "test"), Created, CancellationToken.None);

        await Assert.That(report.Count("done")).IsEqualTo(3);
        VerifiedArchive archive = ArchiveReader.Open(zip, new ArchiveReadOptions());
        await Assert.That(archive.Manifest.Platform).IsEqualTo(ArchiveFormat.CurrentPlatform);
        await Assert.That(archive.Manifest.SteamAccount).IsEqualTo(FakeSteam.AccountId);
        await Assert.That(archive.Manifest.Sections.Select(s => s.Id).ToArray()).IsEquivalentTo(ThreeSections);
        await Assert.That(archive.Contains(ArchiveFormat.CaptureEntry)).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(archive.Read("files/cs2game/autoexec.cfg").Span)).IsEqualTo("echo hi\n\n")
            .Because("secrets are removed before the archive is written, as in v1");
        await Assert.That(archive.Manifest.Entries.Any(e => e.Path.StartsWith("kit/", StringComparison.Ordinal) || e.Path.EndsWith(".ps1", StringComparison.Ordinal)))
            .IsFalse().Because("a v2 archive holds no code");
    }

    private static readonly string[] ThreeSections = [Sections.Cs2, Sections.Cs2Game, Sections.Launch];

    [Test]
    public async Task Dsse_PreAuthEncoding_MatchesTheSpecsExample()
    {
        byte[] pae = Dsse.PreAuthEncoding("http://example.com/HelloWorld", "hello world"u8);

        await Assert.That(Encoding.ASCII.GetString(pae)).IsEqualTo("DSSEv1 29 http://example.com/HelloWorld 11 hello world");
    }

    [Test]
    public async Task AWrittenArchive_ReadsBack_WithEveryEntryAndTheHashLevel()
    {
        using TempDir temp = new();
        string zip = Write(temp);

        VerifiedArchive archive = ArchiveReader.Open(zip, AnyPlatform);

        await Assert.That(archive.Level).IsEqualTo(TrustLevel.Hash);
        await Assert.That(archive.Manifest.Platform).IsEqualTo(Platforms.WindowsX64);
        await Assert.That(archive.Manifest.SteamAccount).IsEqualTo("69413322");
        await Assert.That(archive.Manifest.Entries.Select(e => e.Path).ToArray())
            .IsEquivalentTo(ExpectedEntries);
        await Assert.That(Encoding.UTF8.GetString(archive.Read("capture.vdf").Span)).IsEqualTo("\"ConfigBackup\"\n{\n}\n");
        await Assert.That(archive.ManifestSha256).IsEqualTo(Dsse.Sha256Hex(Entry(zip, ArchiveFormat.ManifestEntry)));

        string unpacked = temp.Sub("unpacked");
        Directory.CreateDirectory(unpacked);
        archive.ExtractTo(unpacked);
        await Assert.That(File.ReadAllText(Path.Combine(unpacked, "files", "cs2", "local", "cfg", "cs2_user_keys_0_slot0.vcfg"))).IsEqualTo("\"config\" {}");
    }

    [Test]
    public async Task TheManifest_IsPlainIndentedJson_ThatAPersonCanRead()
    {
        using TempDir temp = new();
        string zip = Write(temp);

        string manifest = Encoding.UTF8.GetString(Entry(zip, ArchiveFormat.ManifestEntry));

        await Assert.That(manifest).Contains("\"format\": \"acb/2\"");
        await Assert.That(manifest).Contains("\"path\": \"files/cs2/local/cfg/cs2_user_keys_0_slot0.vcfg\"");
        await Assert.That(manifest).Contains("Settings for account 69413322's CS2 'slot 0'");
    }

    [Test]
    public async Task TheWriter_NeverReplacesAnExistingFile()
    {
        using TempDir temp = new();
        string zip = Write(temp);

        await Assert.That(() => Write(temp, zip)).Throws<IOException>();
        await Assert.That(File.Exists(zip + ".partial")).IsFalse();
    }

    [Test]
    public async Task AChangedFile_IsRefused()
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, "files/cs2/local/cfg/cs2_user_keys_0_slot0.vcfg", _ => "\"config\" {bind}"u8.ToArray());

        await Refused(zip, "does not match the manifest");
    }

    [Test]
    public async Task AnAddedFile_IsRefused()
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, add: ("files/cs2game/autoexec.cfg", "exec evil"u8.ToArray()));

        await Refused(zip, "does not list");
    }

    [Test]
    public async Task ARemovedFile_IsRefused()
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, "capture.vdf", _ => null);

        await Refused(zip, "is missing 'capture.vdf'");
    }

    [Test]
    public async Task AnEditedManifest_IsRefused_EvenWhenItsEntriesStillMatch()
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, ArchiveFormat.ManifestEntry,
            b => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Replace("69413322", "11111111", StringComparison.Ordinal)));

        await Refused(zip, "does not match the one the backup's signature names");
    }

    [Test]
    public async Task AManifestEditedAlongWithItsHashStatement_IsStillOnlyHashLevel()
    {
        // Anyone can rewrite both; that is what the hash level means, and the reader says so by its level.
        using TempDir temp = new();
        string zip = Write(temp);
        byte[] manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Entry(zip, ArchiveFormat.ManifestEntry))
            .Replace("69413322", "11111111", StringComparison.Ordinal));
        string edited = Rewrite(zip, temp, ArchiveFormat.ManifestEntry, _ => manifest);
        edited = Rewrite(edited, temp, ArchiveFormat.SignatureEntry, _ => Envelope(Dsse.Sha256Hex(manifest), "hash", []));

        VerifiedArchive archive = ArchiveReader.Open(edited, AnyPlatform);

        await Assert.That(archive.Level).IsEqualTo(TrustLevel.Hash);
        await Assert.That(() => ArchiveReader.Open(edited, AnyPlatform with { MinimumLevel = TrustLevel.Seal }))
            .Throws<ArchiveRejectedException>();
    }

    [Test]
    public async Task AHashStatementThatCarriesASignature_IsRefused()
    {
        using TempDir temp = new();
        string zip = Write(temp);
        string sha = Dsse.Sha256Hex(Entry(zip, ArchiveFormat.ManifestEntry));
        zip = Rewrite(zip, temp, ArchiveFormat.SignatureEntry, _ => Envelope(sha, "hash", [new DsseSignature("x", "AAAA")]));

        await Refused(zip, "unsigned but carries a signature");
    }

    [Test]
    [Arguments("seal", "cannot check yet")]
    [Arguments("identity", "cannot check yet")]
    [Arguments("trust-me", "does not know")]
    public async Task ALevelThisVersionCannotCheck_IsRefused(string level, string expected)
    {
        using TempDir temp = new();
        string zip = Write(temp);
        string sha = Dsse.Sha256Hex(Entry(zip, ArchiveFormat.ManifestEntry));
        zip = Rewrite(zip, temp, ArchiveFormat.SignatureEntry, _ => Envelope(sha, level, []));

        await Refused(zip, expected);
    }

    [Test]
    public async Task ABackupForAnotherPlatform_IsRefusedWhenRestoring_AndReadableWhenChecking()
    {
        using TempDir temp = new();
        string zip = Write(temp);

        await Assert.That(() => ArchiveReader.Open(zip, new ArchiveReadOptions { RequirePlatform = Platforms.LinuxX64 }))
            .Throws<ArchiveRejectedException>().WithMessageContaining("restore only on the platform they were made on");
        await Assert.That(ArchiveReader.Open(zip, AnyPlatform).Manifest.Platform).IsEqualTo(Platforms.WindowsX64);
    }

    [Test]
    public async Task AnUnknownManifestField_IsRefused_NotIgnored()
    {
        using TempDir temp = new();
        string zip = Write(temp);
        byte[] manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Entry(zip, ArchiveFormat.ManifestEntry))
            .Replace("\"format\"", "\"run\": \"calc.exe\",\n  \"format\"", StringComparison.Ordinal));
        zip = Rewrite(zip, temp, ArchiveFormat.ManifestEntry, _ => manifest);
        zip = Rewrite(zip, temp, ArchiveFormat.SignatureEntry, _ => Envelope(Dsse.Sha256Hex(manifest), "hash", []));

        await Refused(zip, "manifest.json cannot be read");
    }

    [Test]
    [Arguments("../outside.cfg", "climbs out")]
    [Arguments("files/cs2/CON.txt", "Windows device")]
    [Arguments("files/cs2/a.cfg ", "dot or a space")]
    [Arguments("files\\cs2\\a.cfg", "treats specially")]
    public async Task AnAmbiguousEntryName_IsRefused(string name, string expected)
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, add: (name, "x"u8.ToArray()));

        await Refused(zip, expected);
    }

    [Test]
    public async Task TwoEntriesDifferingOnlyInCase_AreRefused()
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, add: ("CAPTURE.VDF", "x"u8.ToArray()));

        await Refused(zip, "twice");
    }

    [Test]
    public async Task AnEntryThatUnpacksPastTheCap_IsRefusedWhileReading()
    {
        using TempDir temp = new();
        string zip = Rewrite(Write(temp), temp, add: ("files/cs2/big.vcfg", new byte[ArchiveFormat.MaxEntryBytes + 1]));

        await Refused(zip, "more than 16 MB");
    }

    [Test]
    public async Task AFileThatIsNotAZip_IsRefused()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string zip = temp.Sub("not.zip");
        File.WriteAllText(zip, "hello");

        await Refused(zip, "not a readable zip");
    }

    [Test]
    [Arguments("files/cs2/local/cfg/a.vcfg", null)]
    [Arguments("", "an empty path")]
    [Arguments("/etc/passwd", "an absolute path")]
    [Arguments("files//a", "an empty segment")]
    [Arguments("files/./a", "climbs out")]
    [Arguments("C:/x", "treats specially")]
    [Arguments("files/aux.cfg", "Windows device")]
    [Arguments("files/auxiliary.cfg", null)]
    public async Task ArchivePaths_AcceptOneSpellingOnly(string path, string? expected)
    {
        string? problem = ArchivePaths.Problem(path);

        if (expected is null)
        {
            await Assert.That(problem).IsNull();
        }
        else
        {
            await Assert.That(problem).Contains(expected);
        }
    }

    private static readonly ArchiveReadOptions AnyPlatform = new() { RequirePlatform = null };

    private static async Task Refused(string zip, string expected) =>
        await Assert.That(() => ArchiveReader.Open(zip, AnyPlatform)).Throws<ArchiveRejectedException>().WithMessageContaining(expected);

    private static string Write(TempDir temp, string? zip = null)
    {
        string staging = temp.Sub("staging");
        if (!Directory.Exists(staging))
        {
            string cfg = Path.Combine(staging, "files", "cs2", "local", "cfg");
            Directory.CreateDirectory(cfg);
            File.WriteAllText(Path.Combine(staging, "capture.vdf"), "\"ConfigBackup\"\n{\n}\n");
            File.WriteAllText(Path.Combine(cfg, "cs2_user_keys_0_slot0.vcfg"), "\"config\" {}");
        }

        zip ??= temp.Sub("backup.zip");
        ArchiveContent content = new(Platforms.WindowsX64, new ArchiveTool("Advanced Config Backup", "test"), "69413322",
            [new ArchiveSection("cs2", "Settings for account 69413322's CS2 'slot 0'")], "Advanced Config Backup\n");
        ArchiveWriter.Write(staging, zip, content, Created, CancellationToken.None);
        return zip;
    }

    private static byte[] Entry(string zip, string name)
    {
        using ZipArchive archive = ZipFile.OpenRead(zip);
        using Stream stream = archive.GetEntry(name)!.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static int _rewrites;

    /// <summary>A copy of <paramref name="zip" /> with one entry changed or removed (null), and optionally one added.</summary>
    private static string Rewrite(string zip, TempDir temp, string? name = null, Func<byte[], byte[]?>? change = null, (string Name, byte[] Bytes)? add = null)
    {
        string copy = temp.Sub($"rewritten-{Interlocked.Increment(ref _rewrites)}.zip");
        using (ZipArchive source = ZipFile.OpenRead(zip))
        using (ZipArchive target = ZipFile.Open(copy, ZipArchiveMode.Create))
        {
            foreach (ZipArchiveEntry entry in source.Entries)
            {
                using MemoryStream buffer = new();
                using (Stream s = entry.Open())
                {
                    s.CopyTo(buffer);
                }

                byte[]? bytes = entry.FullName == name ? change!(buffer.ToArray()) : buffer.ToArray();
                if (bytes != null)
                {
                    using Stream w = target.CreateEntry(entry.FullName).Open();
                    w.Write(bytes);
                }
            }

            if (add is { } extra)
            {
                using Stream w = target.CreateEntry(extra.Name).Open();
                w.Write(extra.Bytes);
            }
        }

        return copy;
    }

    private static byte[] Envelope(string manifestSha, string level, DsseSignature[] signatures)
    {
        Statement statement = new(ArchiveFormat.StatementType,
            [new StatementSubject(ArchiveFormat.ManifestEntry, new DigestSet(manifestSha))], ArchiveFormat.PredicateType, new TrustPredicate(level));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(statement, ArchiveJson.Strict.Statement);
        return JsonSerializer.SerializeToUtf8Bytes(new DsseEnvelope(ArchiveFormat.PayloadType, Convert.ToBase64String(payload), signatures),
            ArchiveJson.Strict.DsseEnvelope);
    }
}
