using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Cli.Wizard;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     Sealed backups through acb: <c>backup --seal</c> asks for a passphrase twice or makes one, every command that
///     opens a sealed backup asks for it (or reads --passphrase-file) and refuses without it, and the administrator's
///     run is handed the key, never asked.
/// </summary>
public class SealCommandTests
{
    private const string Passphrase = "correct horse battery staple";

    [Test]
    public async Task BackupSeal_WithAPassphraseFile_WritesASealedBackup_ThatVerifiesOnlyWithIt()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string file = PassphraseFile(host, Passphrase + "\n");
        string zip = host.Temp.Sub("sealed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal", "--passphrase-file", file))
            .IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.Run("verify", zip)).IsEqualTo(ExitCodes.Refused).Because("nobody can be asked, and no file was given");
        await Assert.That(host.Output).Contains("sealed with a passphrase, and none was given");
        await Assert.That(host.Output).Contains("--passphrase-file");

        using TestHost json = new();
        await Assert.That(json.Run("verify", "--json", zip, "--passphrase-file", file)).IsEqualTo(ExitCodes.Done).Because(json.All);
        CommandReport report = JsonSerializer.Deserialize(json.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Level).IsEqualTo("seal");
        await Assert.That(json.Errors).Contains("made by someone who knows the passphrase");

        string wrong = PassphraseFile(host, "not the passphrase at all");
        await Assert.That(host.Run("verify", zip, "--passphrase-file", wrong)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("does not open this backup's seal");
    }

    [Test]
    public async Task BackupSeal_AtATerminal_AsksTwice_AndRefusesWhenTheyDiffer()
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("sealed.zip");
        host.Secrets.Enqueue(Passphrase);
        host.Secrets.Enqueue(Passphrase + "!");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(File.Exists(zip)).IsFalse();
        await Assert.That(host.SecretPrompts.Count).IsEqualTo(2);

        host.Secrets.Enqueue(Passphrase);
        host.Secrets.Enqueue(Passphrase);
        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(ArchiveReader.Open(zip, new ArchiveReadOptions { Passphrase = Passphrase }).Level).IsEqualTo(TrustLevel.Seal);
    }

    /// <summary>Enter alone makes six words, shown once; the backup opens with exactly what was shown.</summary>
    [Test]
    public async Task BackupSeal_WithEnterAlone_MakesAPhrase_ShowsIt_AndSealsWithIt()
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam source = new();
        string zip = host.Temp.Sub("sealed.zip");
        host.Answers.Enqueue("y");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal")).IsEqualTo(ExitCodes.Done).Because(host.All);

        string shown = host.All.Split('\n').Select(l => l.Trim()).First(l => l.Split(' ').Length == Seal.GeneratedWords && l.All(c => c is ' ' or '-' or (>= 'a' and <= 'z')));
        await Assert.That(ArchiveReader.Open(zip, new ArchiveReadOptions { Passphrase = shown }).Level).IsEqualTo(TrustLevel.Seal);
    }

    [Test]
    public async Task BackupSeal_RefusesAShortPassphrase_AndWritesNothing()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = host.Temp.Sub("sealed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal", "--passphrase-file", PassphraseFile(host, "hunter2")))
            .IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("at least 12");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    [Test]
    public async Task BackupSeal_WithNobodyToAsk_AndNoFile_IsRefused()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = host.Temp.Sub("sealed.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", source.Root, "--seal")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("--seal needs a passphrase");
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    [Test]
    public async Task APassphraseFile_WithoutSeal_IsAUsageError()
    {
        using TestHost host = new();
        using FakeSteam source = new();

        await Assert.That(host.Run("backup", "-o", host.Temp.Sub("x.zip"), "--steam-path", source.Root, "--passphrase-file", PassphraseFile(host, Passphrase)))
            .IsEqualTo(ExitCodes.Usage);
    }

    /// <summary>A wrong passphrase at the terminal is asked for again; the restore then goes ahead and records the seal level.</summary>
    [Test]
    public async Task Restore_OfASealedBackup_AsksAgainAfterAWrongPassphrase_ThenRestores()
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo target");
        string zip = Sealed(host, source);
        host.Secrets.Enqueue("not the passphrase at all");
        host.Secrets.Enqueue(Passphrase);

        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.SecretPrompts.Count).IsEqualTo(2);
        await Assert.That(host.SecretPrompts[1]).Contains("does not open the seal");
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsNotEqualTo("echo target");
        await Assert.That(host.Records().Single().Manifest!.Get("archive.level")).IsEqualTo("seal");
    }

    [Test]
    public async Task PlanAndShow_OfASealedBackup_WithoutThePassphrase_AreRefused_AndChangeNothing()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = Sealed(host, source);

        await Assert.That(host.Run("plan", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Run("show", zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(Directory.Exists(host.UserStore)).IsFalse();

        await Assert.That(host.Run("show", zip, "--passphrase-file", PassphraseFile(host, Passphrase))).IsEqualTo(ExitCodes.Done).Because(host.All);
    }

    /// <summary>
    ///     The first run hands the administrator's run the key its passphrase made, beside the bytes; the
    ///     administrator's run opens the backup with that key and asks nobody, and without it refuses.
    /// </summary>
    [Test]
    public async Task TheAdministratorsRun_IsHandedTheKey_AndOpensTheSealWithIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("only Windows has an administrator's run");
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp, SealKey.Create(Passphrase));
        string passphrase = PassphraseFile(host, Passphrase);

        await Assert.That(host.Run("restore", "--yes", "--elevate", "--policy", Policy(host), "--account", FakeSteam.AccountId, "--steam-path", target.Root,
            "--passphrase-file", passphrase, zip)).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);

        IReadOnlyList<byte[]?> input = host.Elevator.Inputs.Single();
        byte[] key = input[2]!;
        await Assert.That(key.Length).IsEqualTo(Seal.KeyBytes);
        await Assert.That(host.Elevator.Calls.Single()).DoesNotContain(Passphrase).Because("the passphrase is never on a command line");

        using TestHost admin = new() { Elevation = new ElevationState(ElevationKind.Elevated, "acb runs as an administrator"), Interactive = true };
        string digest = ArchiveReader.Open(zip, new ArchiveReadOptions { Passphrase = Passphrase }).ManifestSha256;
        admin.ElevatedInput = [File.ReadAllBytes(zip), null, key];
        await Assert.That(admin.Run("__elevated", "restore", zip, "--digest", digest, "--only", "nvidia", "--pipe", "acb-test")).IsNotEqualTo(ExitCodes.Refused)
            .Because(admin.All);
        await Assert.That(admin.SecretPrompts).IsEmpty();

        using TestHost keyless = new() { Elevation = new ElevationState(ElevationKind.Elevated, "acb runs as an administrator"), Interactive = true };
        keyless.ElevatedInput = [File.ReadAllBytes(zip), null, null];
        await Assert.That(keyless.Run("__elevated", "restore", zip, "--digest", digest, "--only", "nvidia", "--pipe", "acb-test")).IsEqualTo(ExitCodes.Refused);
        await Assert.That(keyless.Output).Contains("sealed with a passphrase");
        await Assert.That(keyless.SecretPrompts).IsEmpty().Because("the administrator's run asks nobody");
    }

    /// <summary>The wizard reads the passphrase from its Open step, says on Check when it is missing or wrong, and opens with the right one.</summary>
    [Test]
    public async Task TheWizard_OpensASealedBackup_WithThePassphraseFromOpen()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = Sealed(host, source);
        RestoreFlow flow = new(host.Host()) { ArchivePath = zip, SteamPath = target.Root };

        await Assert.That(flow.Check()).IsFalse();
        await Assert.That(flow.Refusal).Contains("Type it on Open");

        flow.Passphrase = "not the passphrase at all";
        await Assert.That(flow.Check()).IsFalse();
        await Assert.That(flow.Refusal).IsEqualTo(SealNotOpenedException.WrongMessage);

        flow.Passphrase = Passphrase;
        await Assert.That(flow.Check()).IsTrue().Because(flow.Refusal ?? "");
        await Assert.That(flow.Archive!.Level).IsEqualTo(TrustLevel.Seal);
        await Assert.That(flow.CheckLines()).Contains(l => l.Contains("seal", StringComparison.Ordinal));
    }

    private static string Sealed(TestHost host, FakeSteam source)
    {
        string zip = host.Temp.Sub($"sealed-{Guid.NewGuid():N}.zip");
        CaptureOptions options = new() { SteamRoot = source.Root, Creator = "test" };
        options.Include.Clear();
        options.Include.AddRange([Sections.Cs2, Sections.Cs2Game]);
        BackupBuilder.Create(options, zip, new ArchiveTool("Advanced Config Backup", "test"), DateTimeOffset.UtcNow, CancellationToken.None, SealKey.Create(Passphrase));
        return zip;
    }

    private static string PassphraseFile(TestHost host, string text)
    {
        string file = host.Temp.Sub($"passphrase-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        return file;
    }

    private static string Policy(TestHost host)
    {
        string policy = host.Temp.Sub($"policy-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
        File.WriteAllText(policy, """{ "minimumLevel": "seal", "sections": { "allow": ["nvidia"] } }""");
        return policy;
    }
}
