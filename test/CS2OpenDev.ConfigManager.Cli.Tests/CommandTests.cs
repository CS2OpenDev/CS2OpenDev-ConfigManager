using System.Text.Json;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>acb's commands as a person or a script uses them: what they print, what they accept, and their exit codes.</summary>
public class CommandTests
{
    /// <summary>An undo whose store cannot be reached says so with exit 4, never "nothing to undo" with exit 0.</summary>
    [Test]
    [Arguments("undo", "--yes")]
    [Arguments("undo", "--list")]
    public async Task Undo_WhenTheStoreCannotBeReached_SaysSo_AndExits4(string command, string flag)
    {
        using TestHost host = new();
        Directory.CreateDirectory(host.Temp.Path);
        int code;
        using (Permissions.Unlistable(host.Temp.Path))
        {
            code = host.Run(command, flag);
        }

        await Assert.That(code).IsEqualTo(ExitCodes.RecordsNeedAttention).Because(host.All);
        await Assert.That(host.Output).Contains("cannot be reached");
        await Assert.That(host.Output).DoesNotContain("Nothing to undo");
    }

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
        await Assert.That(host.Output).Contains("unsigned: anyone could have edited it").Because("a hash-level backup proves only that its files match its manifest");
        await Assert.That(host.Output).DoesNotContain("Unchanged since it was made");
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
        await Assert.That(host.Errors).Contains("anyone could have edited it");
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

    /// <summary>
    ///     A plan made without administrator writes through the Steam folder, which an administrator's acb leaves, so
    ///     applying it as an administrator is refused for that reason, not as a change to the PC; and the other way round.
    /// </summary>
    [Test]
    public async Task RestorePlan_AtTheOtherKindOfPrompt_SaysSo()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        string plain = host.Temp.Sub("plain.json");
        string admin = host.Temp.Sub("admin.json");
        ElevationState plainPrompt = host.Elevation;
        ElevationState elevated = new(ElevationKind.Elevated, "acb runs as an administrator");
        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", plain, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        host.Elevation = elevated;
        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", admin, zip)).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Run("restore", "--plan", plain, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("which an administrator's acb leaves");
        await Assert.That(host.Output).DoesNotContain("changed since the plan was made");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");

        host.Elevation = plainPrompt;
        await Assert.That(host.Run("restore", "--plan", admin, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("made from an administrator's prompt");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");

        await Assert.That(host.Run("restore", "--plan", plain, "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec\nexec sd_core");
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

    /// <summary>
    ///     --dry-run added to a working restore line stops the changes and nothing else, and with --plan it checks the
    ///     reviewed plan against the PC without applying it.
    /// </summary>
    [Test]
    public async Task RestoreDryRun_TakesRestoresOptions_AndChecksAReviewedPlan()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        string plan = host.Temp.Sub("plan.json");

        await Assert.That(host.Run("restore", zip, "--only", "cs2game", "--steam-path", target.Root, "--yes", "--elevate", "--dry-run")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", plan, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Run("restore", "--plan", plan, "--dry-run")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("still matches the plan");

        target.Write(FakeSteam.Cfg("sd_core.cfg"), "bind mouse5 +changed");
        await Assert.That(host.Run("restore", "--plan", plan, "--dry-run")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("changed since the plan was made");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>A plan saved by Windows PowerShell 5.1's &gt; (UTF-16) or by a tool that adds a byte order mark is read as acb wrote it.</summary>
    [Test]
    [Arguments("utf-16")]
    [Arguments("utf-8-bom")]
    public async Task RestorePlan_SavedByAWindowsShell_IsRead(string encoding)
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp);
        string plan = host.Temp.Sub("plan.json");
        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", plan, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        string json = await File.ReadAllTextAsync(plan);
        await File.WriteAllTextAsync(plan, json, encoding == "utf-16" ? System.Text.Encoding.Unicode : new System.Text.UTF8Encoding(true));

        await Assert.That(host.Run("restore", "--plan", plan, "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo autoexec\nexec sd_core");
    }

    /// <summary>A plan made by another acb is refused for that reason, before its fields or its fingerprint are compared.</summary>
    [Test]
    public async Task RestorePlan_FromAnotherAcbVersion_SaysSo()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo target");
        string zip = TestArchives.FromSteam(source, host.Temp);
        string plan = host.Temp.Sub("plan.json");
        await Assert.That(host.Run("plan", "--only", "cs2game", "--steam-path", target.Root, "--report", plan, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        System.Text.Json.Nodes.JsonObject document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(plan))!.AsObject();
        document["acbVersion"] = "9.9.9";
        document["addedByThatVersion"] = true;
        await File.WriteAllTextAsync(plan, document.ToJsonString());

        await Assert.That(host.Run("restore", "--plan", plan, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("made by acb 9.9.9");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo target");
    }

    /// <summary>
    ///     Text a backup carries reaches the console with its control characters escaped, so a carriage return or an
    ///     escape sequence cannot hide or rewrite a line a person reads before they agree.
    /// </summary>
    [Test]
    public async Task Verify_PrintsTheBackupsOwnText_WithControlCharactersEscaped()
    {
        using TestHost host = new();
        string staging = host.Temp.Sub("staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, ArchiveFormat.CaptureEntry), "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n}\n");
        string zip = host.Temp.Sub("forged.zip");
        ArchiveWriter.Write(staging, zip, new ArchiveContent(ArchiveFormat.CurrentPlatform!, new ArchiveTool("tool\u001b[1A\u001b[2KLevel:    identity", "1"),
            FakeSteam.AccountId, [new ArchiveSection("cs2", "label\r  cs2       made up", ["note\nextra line"])], "test\n"), DateTimeOffset.UtcNow, CancellationToken.None);

        await Assert.That(host.Run("verify", zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.Output).Contains("tool\\x1b[1A\\x1b[2KLevel:    identity");
        await Assert.That(host.Output).Contains("label\\r  cs2");
        await Assert.That(host.Output).Contains("note\\nextra line");
        await Assert.That(host.Output).DoesNotContain("\u001b");
        await Assert.That(host.Output.Replace(Environment.NewLine, "", StringComparison.Ordinal)).DoesNotContain("\r");
    }

    /// <summary>
    ///     Launch options with a control character, which Steam's own window cannot enter, are not restored, and the
    ///     backup says so on the player's own PC.
    /// </summary>
    [Test]
    public async Task Restore_RefusesLaunchOptionsWithAControlCharacter()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        source.AddAccount(FakeSteam.AccountId, launchOptions: "+exec x\r  would     CS2 launch options: -novid -high");
        string zip = host.Temp.Sub("cr.zip");
        await Assert.That(host.Run("backup", "-o", zip, "--only", "launch", "--steam-path", source.Root)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("a restore will refuse them");

        await Assert.That(host.Run("plan", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("control character");
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-target");
    }

    /// <summary>A tab, which Steam's localconfig.vdf can hold and the plan prints as \t, does not stop launch options.</summary>
    [Test]
    public async Task Restore_TakesLaunchOptionsWithATab()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        source.AddAccount(FakeSteam.AccountId, launchOptions: "-novid\t-high");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Launch);

        await Assert.That(host.Run("plan", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("-novid\\t-high");
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-novid\t-high");
    }

    /// <summary>The list of outcomes cannot be changed by code that holds it.</summary>
    [Test]
    public async Task Outcomes_AreReadOnly()
    {
        await Assert.That(((IList<string>)Outcomes.All).IsReadOnly).IsTrue();
        await Assert.That(Outcomes.IsKnown(Outcomes.Left)).IsTrue();
        await Assert.That(Outcomes.IsKnown("DONE")).IsFalse();
        await Assert.That(Outcomes.IsKnown(null!)).IsFalse();
    }

    /// <summary>Every outcome a part can have maps to an exit code, and one that is not an outcome never passes as done.</summary>
    [Test]
    public async Task EveryOutcome_HasAnExitCode()
    {
        foreach (string outcome in Outcomes.All)
        {
            await Assert.That(ExitCodes.For(outcome)).IsBetween(ExitCodes.Done, ExitCodes.PartFailed);
        }

        await Assert.That(ExitCodes.For(Outcomes.Done)).IsEqualTo(ExitCodes.Done);
        await Assert.That(ExitCodes.For(Outcomes.Left)).IsEqualTo(ExitCodes.PartsLeft);
        await Assert.That(() => ExitCodes.For("unsupported")).Throws<ArgumentException>();
    }
}
