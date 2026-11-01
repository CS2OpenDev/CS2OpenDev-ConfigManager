using CS2OpenDev.ConfigManager.Kit;
using KitBackup = CS2OpenDev.ConfigManager.Kit.ConfigBackup;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     What a backup leaves out, what a restore refuses, and how a restore or undo that stops partway leaves the
///     target. Restores here go only into fake Steam folders, over the file sections.
/// </summary>
public class SafetyTests
{
    [Test]
    [Arguments("rcon_password hunter2", "", "rcon_password")]
    [Arguments("echo hi\nsv_password \"club night\"\nmp_warmup_end", "echo hi\n\nmp_warmup_end", "sv_password")]
    [Arguments("sv_setsteamaccount ABCDEF0123456789", "", "sv_setsteamaccount")]
    [Arguments("bind f5 \"connect 1.2.3.4:27015; password scrim\"", "bind f5 \"connect 1.2.3.4:27015; \"", "password")]
    [Arguments("-novid +sv_setsteamaccount TOKEN +exec autoexec", "-novid  +exec autoexec", "sv_setsteamaccount")]
    [Arguments("SV_PASSWORD x;tv_password y", ";", "sv_password")]
    public async Task RemoveSecrets_TakesOutTheCommandAndItsValue_WhereverItStands(string text, string expected, string first)
    {
        string result = KitBackup.RemoveSecrets(text, out List<string> found);

        await Assert.That(result).IsEqualTo(expected);
        await Assert.That(found[0]).IsEqualTo(first);
    }

    [Test]
    [Arguments("sv_cheats 1\nbot_kick")]
    [Arguments("sv_password \"\"")]
    [Arguments("my_sv_password_alias 1")]
    [Arguments("cl_crosshairsize 2")]
    [Arguments("\t\t\"password\"\t\t\"\"\n")]
    [Arguments("\t\t\"my_password\"\t\t\"1\"\n")]
    public async Task RemoveSecrets_LeavesEverythingElse(string text)
    {
        string result = KitBackup.RemoveSecrets(text, out List<string> found);

        await Assert.That(result).IsEqualTo(text);
        await Assert.That(found).IsEmpty();
    }

    [Test]
    [Arguments("\t\t\"password\"\t\t\"scrim\"\n\t\t\"volume\"\t\t\"0.5\"\n", "\t\t\"volume\"\t\t\"0.5\"\n", "password")]
    [Arguments("\t\"SV_Password\"\t\"x y\"\r\n}", "}", "sv_password")]
    public async Task RemoveSecrets_TakesOutASavedConvarLine(string text, string expected, string first)
    {
        string result = KitBackup.RemoveSecrets(text, out List<string> found);

        await Assert.That(result).IsEqualTo(expected);
        await Assert.That(found[0]).IsEqualTo(first);
    }

    /// <summary>A dry run promises to change nothing, file attributes included.</summary>
    [Test]
    public async Task CheckWritable_InADryRun_LeavesAReadOnlyFileAlone()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        string file = temp.Sub("cs2_video.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        DateTime changed = File.GetLastWriteTimeUtc(file);
        try
        {
            SafeFile.CheckWritable(file, dryRun: true);

            await Assert.That(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)).IsTrue();
            await Assert.That(File.GetLastWriteTimeUtc(file)).IsEqualTo(changed);

            SafeFile.CheckWritable(file, dryRun: false);

            await Assert.That(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)).IsTrue()
                .Because("a real run puts the flag back after its check");
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task Capture_LeavesPasswordsAndServerTokensOut_AndSaysWhere()
    {
        using FakeSteam steam = new(launchOptions: "-novid +sv_setsteamaccount GSLT123");
        steam.Write(FakeSteam.Cfg("server.cfg"), "hostname prac\nrcon_password hunter2\nsv_setsteamaccount GSLT123\n");
        steam.Write(FakeSteam.Cfg("autoexec.cfg"), "bind f5 \"connect 1.2.3.4; password scrim\"\nexec sd_core");
        using TempDir temp = new();

        RunReport report = CaptureRestoreTests.Capture(steam, temp.Path);

        string all = string.Join("\n", Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        await Assert.That(all).DoesNotContain("hunter2");
        await Assert.That(all).DoesNotContain("GSLT123");
        await Assert.That(all).DoesNotContain("scrim");
        await Assert.That(File.ReadAllText(Path.Combine(temp.Path, "files", "cs2game", "server.cfg"))).IsEqualTo("hostname prac\n\n\n");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2Game).Detail).Contains("server.cfg");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Launch).Detail).Contains("sv_setsteamaccount");

        KvNode manifest = KitBackup.ReadManifest(temp.Path);
        foreach (KvNode file in manifest.Path("cs2game", "files")!.Children)
        {
            await Assert.That(KitBackup.Sha256(Path.Combine(temp.Path, "files", "cs2game", file.Key))).IsEqualTo(file.Value)
                .Because("the hash is of the copy with the secret left out");
        }
    }

    /// <summary>CS2 saves every bind it was given in its key-binding vcfg, and the password convar in its convars vcfg.</summary>
    [Test]
    public async Task Capture_LeavesPasswordsOutOfTheBindsAndConvarsCs2Saved()
    {
        using FakeSteam steam = new();
        string keys = "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"F1\"\t\t\"connect 1.2.3.4; password abc\"\n\t\t\"MOUSE4\"\t\t\"slot7\"\n\t}\n}\n";
        string filtered = keys.Replace("; password abc", "; ", StringComparison.Ordinal);
        steam.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg"), keys);
        steam.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg_lastclouded"), keys);
        string convars = "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"password\"\t\t\"scrim\"\n\t\t\"sensitivity\"\t\t\"1.2\"\n\t}\n}\n";
        string convarsFiltered = convars.Replace("\t\t\"password\"\t\t\"scrim\"\n", "", StringComparison.Ordinal);
        steam.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"), convars);
        string video = "\"video.cfg\"\n{\n\t\"setting.defaultres\"\t\t\"2560\"\n}\n";
        steam.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"), video);
        using TempDir temp = new();

        RunReport report = CaptureRestoreTests.Capture(steam, temp.Path);

        string cfg = Path.Combine(temp.Path, "files", "cs2", "local", "cfg");
        await Assert.That(File.ReadAllText(Path.Combine(cfg, "cs2_user_keys_0_slot0.vcfg"))).IsEqualTo(filtered);
        await Assert.That(File.ReadAllText(Path.Combine(cfg, "cs2_user_keys_0_slot0.vcfg_lastclouded"))).IsEqualTo(filtered)
            .Because("the twin must still match, so CS2 sees nothing to reconcile");
        await Assert.That(File.ReadAllText(Path.Combine(cfg, "cs2_user_convars_0_slot0.vcfg"))).IsEqualTo(convarsFiltered)
            .Because("CS2 archives the password convar a player connected with");
        await Assert.That(File.ReadAllText(Path.Combine(cfg, "cs2_video.txt"))).IsEqualTo(video);
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Detail).Contains("cs2_user_keys_0_slot0.vcfg");
        foreach (KvNode file in KitBackup.ReadManifest(temp.Path).Path("cs2", "files")!.Children)
        {
            await Assert.That(KitBackup.Sha256(Path.Combine(temp.Path, "files", "cs2", file.Key))).IsEqualTo(file.Value);
        }
    }

    [Test]
    [Arguments("69413322", "69413322")]
    [Arguments(" 69413322 ", "69413322")]
    [Arguments("76561198029679050", "69413322")]
    [Arguments("..", null)]
    [Arguments(".", null)]
    [Arguments("C:\\Users\\Public", null)]
    [Arguments("0", null)]
    [Arguments("-5", null)]
    [Arguments("4294967296", null)]
    [Arguments("1e5", null)]
    [Arguments("", null)]
    public async Task AccountIds_AreNumbersSteamNamesFoldersBy(string id, string? expected)
    {
        await Assert.That(SteamInstall.NormalizeAccountId(id)).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolveAccount_TakesNoPathAndNoAccountWithoutAFolder()
    {
        using FakeSteam target = new();
        SteamInstall install = SteamInstall.Locate(target.Root)!;

        await Assert.That(KitBackup.ResolveAccount(install, "..", null, out string? why)).IsNull();
        await Assert.That(why).Contains("not a Steam account id");
        await Assert.That(KitBackup.ResolveAccount(install, null, "..", out why)).IsNull()
            .Because("userdata/.. exists, but it is not an account");
        await Assert.That(KitBackup.ResolveAccount(install, null, target.Root, out why)).IsNull();
        await Assert.That(KitBackup.ResolveAccount(install, "555", null, out why)).IsNull();
        await Assert.That(why).Contains("has not signed in");
        await Assert.That(KitBackup.ResolveAccount(install, FakeSteam.SteamId64.ToString(System.Globalization.CultureInfo.InvariantCulture), null, out why))
            .IsEqualTo(FakeSteam.AccountId).Because("a SteamID64 names the same account");
    }

    [Test]
    public async Task Restore_IntoABackupNamingAPathAsItsAccount_WritesNothing()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        KvNode manifest = KitBackup.ReadManifest(temp.Path);
        manifest.Path("cs2")!.Set("account", "..");
        manifest.Path("launch")!.Set("account", "..");
        manifest.Save(Path.Combine(temp.Path, KitBackup.ManifestName));

        RunReport report = Engine.Restore(temp.Path, CaptureRestoreTests.Options(target));

        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Outcome).IsEqualTo("deferred");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Launch).Outcome).IsEqualTo("deferred");
        await Assert.That(target.Exists("userdata/730/local/cfg/cs2_video.txt")).IsFalse();
        await Assert.That(target.Exists("config/localconfig.vdf")).IsFalse();
    }

    /// <summary>
    ///     Every copy is checked before the first write, so a damaged copy that sorts last leaves the files that
    ///     sort before it as the target had them, and a .vcfg never lands without its _lastclouded twin.
    /// </summary>
    [Test]
    public async Task Restore_ADamagedCopyAnywhereInAPart_WritesNothingOfIt()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        string convars = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg");
        target.Write(convars, FakeSteam.Convars("target"));
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo target");
        await File.AppendAllTextAsync(Path.Combine(temp.Path, "files", "cs2", "local", "cfg", "cs2_video.txt"), "x");
        await File.AppendAllTextAsync(Path.Combine(temp.Path, "files", "cs2game", "sd_core.cfg"), "x");

        RunReport report = Engine.Restore(temp.Path, CaptureRestoreTests.Options(target));

        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Outcome).IsEqualTo("failed");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2Game).Outcome).IsEqualTo("failed");
        await Assert.That(target.Read(convars)).IsEqualTo(FakeSteam.Convars("target"));
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo target");
        await Assert.That(Directory.GetFiles(target.Root, "*.acb-tmp", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task Restore_ToAPcWithoutCs2Video_DropsTheSourcesHardwareKeys()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        string video = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt");
        source.Write(video, FakeSteam.Video("4318", "9989", "1280", "960").Replace("}", "\t\"setting.monitor_index\"\t\t\"2\"\n}", StringComparison.Ordinal));
        File.Delete(target.PathOf(video));
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);

        Engine.Restore(temp.Path, CaptureRestoreTests.Options(target));

        KvNode merged = KvNode.Load(target.PathOf(video));
        await Assert.That(merged.Get("setting.defaultres")).IsEqualTo("1280").Because("the player's resolution travels");
        await Assert.That(merged.Get("VendorID")).IsNull().Because("CS2 detects this PC's GPU itself");
        await Assert.That(merged.Get("DeviceID")).IsNull();
        await Assert.That(merged.Get("setting.monitor_index")).IsNull();
    }

    /// <summary>
    ///     An undo forgets each part once it is put back, so -Only or a part left for later leaves the rest of the
    ///     snapshot for the next run, and the snapshot is finished only once nothing is left.
    /// </summary>
    [Test]
    public async Task Undo_PutsBackPartByPart_AndKeepsWhatIsLeft()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo target");
        using TempDir temp = new();
        CaptureRestoreTests.Capture(source, temp.Path);
        RunReport restored = Engine.Restore(temp.Path, CaptureRestoreTests.Options(target));
        string snapshot = restored.UndoDir!;

        RestoreOptions onlyCfg = CaptureRestoreTests.Options(target);
        onlyCfg.Only = [Sections.Cs2Game];
        RunReport first = Engine.Undo(snapshot, onlyCfg);

        await Assert.That(first.Pending).IsEqualTo(2).Because(string.Join("; ", first.Results));
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo target");
        KvNode left = KitBackup.ReadManifest(snapshot);
        await Assert.That(left.Child(Sections.Cs2Game)).IsNull();
        await Assert.That(left.Child(Sections.Cs2)).IsNotNull();
        await Assert.That(left.Child(Sections.Launch)).IsNotNull();

        RunReport rest = Engine.Undo(snapshot, CaptureRestoreTests.Options(target));

        await Assert.That(rest.Pending).IsEqualTo(0).Because(string.Join("; ", rest.Results));
        await Assert.That(rest.Results.Select(r => r.Section)).DoesNotContain(Sections.Cs2Game)
            .Because("a part already put back is not applied again");
        await Assert.That(SteamInstall.Locate(target.Root)!.ReadLaunchOptions(FakeSteam.AccountId)).IsEqualTo("-target");
    }

    /// <summary>
    ///     A part that fails partway is rolled back from its record, which therefore has to hold the earlier change
    ///     before it was made: the record is read at the moment of the rollback, then the part is as the target had it.
    /// </summary>
    [Test]
    public async Task TheUndoRecord_IsOnDiskBeforeAPartFinishes_AndAFailedPartIsRolledBackFromIt()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        string machine = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_machine_convars.vcfg");
        source.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.1\"\n\t}\n}\n");
        string before = target.Read(machine);
        CaptureRestoreTests.Capture(source, temp.Path);
        // The second write of the part fails, on a folder standing where the file goes, which no check before
        // the first write looks for: the first file is changed and must already be on record.
        string keys = target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_keys_0_slot0.vcfg"));
        File.Delete(keys);
        Directory.CreateDirectory(keys);
        RestoreOptions options = CaptureRestoreTests.Options(target);
        options.Only = [Sections.Cs2];
        KvNode? onRecord = null;
        options.BeforeRollback = dir => onRecord = KitBackup.ReadManifest(dir);

        RunReport report = Engine.Restore(temp.Path, options);

        await Assert.That(report.Results.Single().Outcome).IsEqualTo("failed").Because(report.Results.Single().ToString());
        await Assert.That(onRecord).IsNotNull();
        await Assert.That(onRecord!.Path("cs2", "files")!.Child("local/cfg/cs2_machine_convars.vcfg")).IsNotNull();
        await Assert.That(report.RolledBack).Contains(Sections.Cs2);
        await Assert.That(report.RollbackProblem).IsNull();
        await Assert.That(target.Read(machine)).IsEqualTo(before).Because("the failed part was put back from its record");
        await Assert.That(KitBackup.ReadManifest(report.UndoDir!).Child(Sections.Cs2)).IsNull()
            .Because("a part rolled back is off the record, so acb undo does not apply it again");
    }

    [Test]
    public async Task PerUserParts_WaitForTheUserTheyBelongTo()
    {
        const string me = "PC\\player";
        const string sid = "S-1-5-21-1-2-3-1001";

        await Assert.That(KitBackup.OtherUserReason(me, me, null, sid)).IsNull();
        await Assert.That(KitBackup.OtherUserReason(me, "pc\\PLAYER", sid, sid)).IsNull();
        await Assert.That(KitBackup.OtherUserReason("PC\\admin", me, null, "S-1-5-21-1-2-3-500")).Contains("Run as administrator");
        await Assert.That(KitBackup.OtherUserReason(me, me, "S-1-5-21-1-2-3-500", sid)).Contains("another Windows user");
        await Assert.That(KitBackup.OtherUserReason(me, null, null, sid)).IsNull().Because("Windows did not say who is signed in");
    }

    [Test]
    public async Task RegistryEntries_MustBeTheKindWindowsReads()
    {
        static KvNode Entry(string key, string name, string kind, string data)
        {
            KvNode node = new("0");
            node.Set("key", key);
            node.Set("name", name);
            node.Set("kind", kind);
            node.Set("data", data);
            return node;
        }

        const string gameBar = @"Software\Microsoft\GameBar";
        const string layers = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "dword", "0"), false)).IsNull();
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "string", "0"), false)).Contains("Windows reads it as dword");
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "binary", "00"), false)).IsNotNull();
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "dword", "off"), false)).Contains("not valid");
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(layers, RegistryItem.Cs2ExeToken, "string", "~ DISABLEDXMAXIMIZEDWINDOWEDMODE"), false)).IsNull();
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "string", "0"), true))
            .IsNull().Because("an undo puts back the kind the target had");
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "expandstring", "%x%"), true)).IsNull();
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(gameBar, "AutoGameModeEnabled", "multistring", "a"), true)).IsNotNull();
        await Assert.That(KitBackup.RegistryEntryProblem(Entry(@"Software\Microsoft\Windows\CurrentVersion\Run", "x", "string", "evil.exe"), true))
            .Contains("does not carry");
    }
}
