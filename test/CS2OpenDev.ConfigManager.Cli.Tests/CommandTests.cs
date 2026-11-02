using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>acb's commands as a person or a script uses them: what they print, what they accept, and their exit codes.</summary>
public class CommandTests
{
    private const string Autoexec = "autoexec.cfg";

    [Test]
    public async Task Verify_SaysWhatTheBackupIs_AndChangesNothing()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = TestArchives.FromSteam(source, host.Temp);

        int code = host.Run("verify", zip);

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("hash (unsigned");
        await Assert.That(host.Output).Contains("Steam account " + FakeSteam.AccountId);
        await Assert.That(host.Output).Contains("cs2game");
        await Assert.That(host.Output).Contains("Unchanged since it was made");
        await Assert.That(Directory.Exists(host.UserStore)).IsFalse();
    }

    [Test]
    public async Task Verify_ReadsABackupForAnotherPlatform_AndSaysWhereItRestores()
    {
        using TestHost host = new();
        string other = ArchiveFormat.CurrentPlatform == Platforms.WindowsX64 ? Platforms.LinuxX64 : Platforms.WindowsX64;
        string zip = TestArchives.Craft(host.Temp, "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n}\n", platform: other);

        await Assert.That(host.Run("verify", zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("It restores only on " + other);
    }

    [Test]
    public async Task Verify_Json_PrintsOnlyTheReportOnStandardOutput()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = TestArchives.FromSteam(source, host.Temp);

        await Assert.That(host.Run("verify", "--json", zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        CommandReport report = JsonSerializer.Deserialize(host.Output, CliJson.Default.CommandReport)!;
        await Assert.That(report.Command).IsEqualTo("verify");
        await Assert.That(report.Level).IsEqualTo("hash");
        await Assert.That(report.ArchiveSha256).IsEqualTo(ArchiveReader.Open(zip, new ArchiveReadOptions()).ManifestSha256);
        await Assert.That(host.Errors).Contains("Unchanged since it was made");
    }

    [Test]
    public async Task Show_ListsTheFiles_PrintsASection_AndPrintsAFile()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = TestArchives.FromSteam(source, host.Temp);

        await Assert.That(host.Run("show", zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("files/cs2game/autoexec.cfg");
        await Assert.That(host.Output).Contains("launch: CS2 launch options");

        await Assert.That(host.Run("show", zip, "--section", "launch")).IsEqualTo(ExitCodes.Done);
        await Assert.That(host.Output).Contains("-novid -high");

        await Assert.That(host.Run("show", zip, "--file", "cs2game/autoexec.cfg")).IsEqualTo(ExitCodes.Done);
        await Assert.That(host.Output).Contains("exec sd_core");

        await Assert.That(host.Run("show", zip, "--file", "cs2game/nope.cfg")).IsEqualTo(ExitCodes.Refused);
    }

    [Test]
    public async Task Backup_WritesAV2Archive_AndNeverReplacesAFile()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        string zip = host.Temp.Sub("made.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2,cs2game,launch", "--steam-path", source.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(ArchiveReader.Open(zip, new ArchiveReadOptions()).Manifest.Sections.Count).IsEqualTo(3);

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2", "--steam-path", source.Root)).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("already exists");
    }

    [Test]
    public async Task Backup_WithNothingToSave_WritesNoArchive_AndExits1()
    {
        using TestHost host = new();
        string zip = host.Temp.Sub("made.zip");

        await Assert.That(host.Run("backup", "-o", zip, "--only", "cs2game", "--steam-path", host.Temp.Sub("no-steam"))).IsEqualTo(ExitCodes.Refused);
        await Assert.That(File.Exists(zip)).IsFalse();
    }

    [Test]
    public async Task Restore_ThenUndo_PutsThePcBack_AndRecordsTheArchive()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);

        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec\nexec sd_core");
        KvNode record = host.Records().Single().Manifest!;
        await Assert.That(record.Get("archive.sha256")).IsEqualTo(ArchiveReader.Open(zip, new ArchiveReadOptions()).ManifestSha256);
        await Assert.That(record.Get("archive.level")).IsEqualTo("hash");
        await Assert.That(host.Output).Contains("acb undo");

        await Assert.That(host.Run("undo", "--list")).IsEqualTo(ExitCodes.Done);
        await Assert.That(host.Output).Contains(Path.GetFileName(zip));

        await Assert.That(host.Run("undo", "--yes", "--steam-path", target.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-target");
        await Assert.That(host.Records()).IsEmpty();
        await Assert.That(host.Run("undo", "--yes")).IsEqualTo(ExitCodes.Done);
        await Assert.That(host.Output).Contains("Nothing to undo");
    }

    [Test]
    public async Task Restore_WithoutATerminalOrYes_ChangesNothing_AndExits1()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);

        await Assert.That(host.Run("restore", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("--yes");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
    }

    [Test]
    public async Task Restore_Interactive_DefaultsToNo()
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        host.Answers.Enqueue("");

        await Assert.That(host.Run("restore", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("[y/N]");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
    }

    /// <summary>The backup's account has not signed in here: the menu never guesses, its default leaves the parts, and a pick is used.</summary>
    [Test]
    [Arguments("", false)]
    [Arguments("1", true)]
    public async Task Restore_Interactive_OffersTheAccountsHere_WithLeaveAsTheDefault(string answer, bool restored)
    {
        using TestHost host = new() { Interactive = true };
        using FakeSteam source = new();
        using FakeSteam target = new(accountId: "12345678", launchOptions: "-mine");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Launch);
        host.Answers.Enqueue(answer);
        host.Answers.Enqueue("y");

        int code = host.Run("restore", "--steam-path", target.Root, zip);

        await Assert.That(host.Output).Contains("Pick one only if it is yours under another number");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions("12345678")).IsEqualTo(restored ? "-novid -high" : "-mine");
        await Assert.That(code).IsEqualTo(restored ? ExitCodes.Done : ExitCodes.PartsLeft).Because(host.All);
    }

    [Test]
    public async Task APolicy_NarrowsWhatIsRestored()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        string policy = host.Temp.Sub("policy.json");
        await File.WriteAllTextAsync(policy, """{ "sections": { "allow": ["launch"] } }""");

        await Assert.That(host.Run("restore", "--yes", "--policy", policy, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("left alone by the organiser's policy");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-novid -high");
    }

    [Test]
    [Arguments("""{ "requireCs2Closed": true }""", "restores only while CS2 is closed")]
    [Arguments("""{ "steamIds": ["76561198029679050"] }""", "only an identity-level backup proves who signed it")]
    [Arguments("""{ "sections": { "allow": ["cs2"] }, "widen": true }""", "cannot be read")]
    public async Task APolicy_ThatRefuses_StopsBeforeAnyChange(string json, string expected)
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        string policy = host.Temp.Sub("policy.json");
        await File.WriteAllTextAsync(policy, json);
        host.Running.Add("cs2");

        await Assert.That(host.Run("restore", "--yes", "--policy", policy, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains(expected);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
    }

    /// <summary>A reviewed plan is applied exactly, and refused once the PC changed under it.</summary>
    [Test]
    public async Task RestorePlan_AppliesAReviewedPlan_AndRefusesItOnceThePcChanged()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        string plan = host.Temp.Sub("plan.json");
        string stale = host.Temp.Sub("stale.json");

        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", plan, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        File.Copy(plan, stale);
        target.Write(FakeSteam.Cfg("sd_core.cfg"), "bind mouse5 +changed");

        await Assert.That(host.Run("restore", "--plan", stale, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("changed since the plan was made");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");

        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", plan, zip)).IsEqualTo(ExitCodes.Done);
        await Assert.That(host.Run("restore", "--plan", plan, "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec\nexec sd_core");
        await Assert.That(target.Read(FakeSteam.Cfg("sd_core.cfg"))).IsEqualTo("bind mouse5 +lookatweapon");
        await Assert.That(host.Run("restore", "--plan", plan, "--only", "cs2", "--yes")).IsEqualTo(ExitCodes.Usage);
    }

    /// <summary>An organiser types the administrator's password: the elevated run is asked for, and its report becomes this run's.</summary>
    [Test]
    public async Task Restore_Interactive_OffersTheAdministratorsRun_AndReportsIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("only Windows has a section that needs an administrator");
            return;
        }

        using TestHost host = new() { Interactive = true };
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);
        string policy = host.Temp.Sub("policy.json");
        await File.WriteAllTextAsync(policy, """{ "nvidia": { "deny": ["0x1057EB71"] } }""");
        host.Answers.Enqueue("y");
        host.Answers.Enqueue("y");
        host.Elevator.Answer = new ElevatedResult(false, ExitCodes.Done, new CommandReport
        {
            Sections = [new SectionLine(Sections.Nvidia, Sections.Label(Sections.Nvidia), "done", "changed 1 settings on the CS2 profile")],
            UndoRecord = @"C:\ProgramData\CS2OpenDev\ConfigBackup\undo\000001-20261101-180000Z"
        }, null);

        int code = host.Run("restore", "--policy", policy, "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("Ask Windows now?");
        IReadOnlyList<string> call = host.Elevator.Calls.Single();
        await Assert.That(call).Contains(Path.GetFullPath(zip));
        await Assert.That(call).Contains(ArchiveReader.Open(zip, new ArchiveReadOptions()).ManifestSha256);
        await Assert.That(call).Contains("--policy-sha256");
        await Assert.That(call.Any(a => a.Contains(host.UserStore, StringComparison.OrdinalIgnoreCase))).IsFalse()
            .Because("the administrator's run is told nothing about the player's store");
        await Assert.That(host.Output).Contains("changed 1 settings on the CS2 profile");
    }

    [Test]
    public async Task Restore_TheAdministratorsRunDeclined_LeavesItsSections_AndExits2()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("only Windows has a section that needs an administrator");
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        string zip = FailurePolicyTests.NvidiaArchive(host.Temp);

        int code = host.Run("restore", "--yes", "--elevate", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("cancelled or refused");
    }

    [Test]
    [Arguments("restore --bogus x.zip")]
    [Arguments("restore --only cs2,remote x.zip")]
    [Arguments("restore")]
    [Arguments("plan a.zip b.zip")]
    [Arguments("undo some.zip")]
    [Arguments("backup")]
    [Arguments("frobnicate")]
    [Arguments("")]
    public async Task AnythingElse_IsAUsageError_AndChangesNothing(string line)
    {
        using TestHost host = new();

        int code = host.Run(line.Length == 0 ? [] : line.Split(' '));

        await Assert.That(code).IsEqualTo(ExitCodes.Usage).Because(host.All);
        await Assert.That(host.Errors).Contains("Usage:");
    }

    [Test]
    public async Task AFolder_IsNotABackup()
    {
        using TestHost host = new();
        Directory.CreateDirectory(host.Temp.Sub("unzipped"));

        await Assert.That(host.Run("verify", host.Temp.Sub("unzipped"))).IsEqualTo(ExitCodes.Refused);
        await Assert.That(host.Output).Contains("is a folder");
    }
}
