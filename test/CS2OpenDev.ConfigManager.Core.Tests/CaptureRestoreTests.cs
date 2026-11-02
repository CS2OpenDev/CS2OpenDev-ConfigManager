using CS2OpenDev.ConfigManager.Kit;
using KitBackup = CS2OpenDev.ConfigManager.Kit.ConfigBackup;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     Capture from one fake Steam folder and restore into another, over the file sections only (CS2's account
///     files, the cfg folder and the launch options). The machine-wide sections read and write this PC's own
///     registry, display and driver, so no test here restores one.
/// </summary>
public class CaptureRestoreTests
{
    private static readonly string[] FileSections = [Sections.Cs2, Sections.Cs2Game, Sections.Launch];

    private static readonly string[] ExpectedAccountFiles =
    [
        "local/cfg/cs2_machine_convars.vcfg",
        "local/cfg/cs2_user_convars_0_slot0.vcfg",
        "local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded",
        "local/cfg/cs2_user_keys_0_slot0.vcfg",
        "local/cfg/cs2_video.txt"
    ];

    private static readonly string[] ExpectedCfgFiles = ["autoexec.cfg", "practice/nades.cfg", "sd_core.cfg"];

    [Test]
    [Arguments("local/cfg/cs2_user_convars_0_slot0.vcfg", true)]
    [Arguments("local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded", true)]
    [Arguments("local/cfg/cs2_user_keys_0_slot3.vcfg", true)]
    [Arguments("local/cfg/cs2_machine_convars.vcfg", true)]
    [Arguments("local/cfg/cs2_video.txt", true)]
    [Arguments("remote/cs2_user_keys.vcfg", false)]
    [Arguments("remote/cfg/cs2_preferred_items.txt", false)]
    [Arguments("remote/cfg/workshop_saves/save_3808451248.txt", false)]
    [Arguments("local/cfg/cs2_video.txt.bak", false)]
    [Arguments("local/cfg/cs2_video_dupe.txt", false)]
    [Arguments("local/cfg/trustedlaunch.cfg", false)]
    [Arguments("local/socache.dt", false)]
    [Arguments("local/pet/53881994142/book/pet.bin", false)]
    [Arguments("remote/voice_ban.dt", false)]
    [Arguments("remote/socache.dt", false)]
    [Arguments("remotecache.vdf", false)]
    public async Task AccountSettings_AreTheVcfgsVideoAndCloudCfg(string relative, bool kept)
    {
        await Assert.That(KitBackup.IsAccountSetting(relative)).IsEqualTo(kept);
    }

    [Test]
    [Arguments("autoexec.cfg", false, true)]
    [Arguments("sd_prac.cfg", false, true)]
    [Arguments("practice/nades.cfg", false, true)]
    [Arguments("gamemode_competitive.cfg", false, false)]
    [Arguments("gamemode_competitive2v2_offline.cfg", false, false)]
    [Arguments("server.cfg", false, false)]
    [Arguments("gamemode_competitive_server.cfg", false, true)]
    [Arguments("gamemode_casual_server.cfg", false, true)]
    [Arguments("gamemode_competitive_tmm.cfg", false, false)]
    [Arguments("server_default.cfg", false, false)]
    [Arguments("server_prac.cfg", false, true)]
    [Arguments("servernades.cfg", false, true)]
    [Arguments("perftest.cfg", false, false)]
    [Arguments("boot.vcfg", false, false)]
    [Arguments("user_keys_default.vcfg", false, false)]
    [Arguments("devonly/anything.cfg", false, false)]
    [Arguments("gamestate_integration_logitech.cfg", false, false)]
    [Arguments("gamestate_integration_logitech.cfg", true, true)]
    public async Task PlayerCfgs_LeaveValvesOwnBehind(string relative, bool gsi, bool kept)
    {
        await Assert.That(KitBackup.IsPlayerCfg(relative, gsi)).IsEqualTo(kept);
    }

    /// <summary>
    ///     Capture reads the folder's own long names, so a player's nade~1.cfg is carried. A restore never writes a
    ///     name that could be an 8.3 alias of another file; one too long to be an alias is written.
    /// </summary>
    [Test]
    [Arguments("nade~1.cfg", true, false)]
    [Arguments("GAMEST~1.CFG", true, false)]
    [Arguments("PRACTI~1/nades.cfg", true, false)]
    [Arguments("practice~2.cfg", true, true)]
    [Arguments("practice/nades~2.config.cfg", true, true)]
    [Arguments("autoexec.cfg.", false, false)]
    public async Task ATildeAndADigit_AreCaptured_AndRestoredOnlyWhenTheNameCannotBeAnAlias(string relative, bool captured, bool restored)
    {
        await Assert.That(KitBackup.IsPlayerCfg(relative, false)).IsEqualTo(captured);
        await Assert.That(KitBackup.IsRestorablePath(Sections.Cs2Game, relative, false)).IsEqualTo(restored);
    }

    [Test]
    public async Task Capture_CarriesTheFileSections_AndNothingElseFromTheAccount()
    {
        using FakeSteam steam = new();
        using TempDir temp = new();

        RunReport report = Capture(steam, temp.Path);

        await Assert.That(report.Count("done")).IsEqualTo(3);
        KvNode manifest = KitBackup.ReadManifest(temp.Path);
        await Assert.That(manifest.Get("kind")).IsEqualTo("backup");
        await Assert.That(manifest.Path("cs2")!.Get("account")).IsEqualTo(FakeSteam.AccountId);
        await Assert.That(manifest.Path("launch")!.Get("value")).IsEqualTo("-novid -high");

        string[] accountFiles = [.. manifest.Path("cs2", "files")!.Children.Select(c => c.Key)];
        await Assert.That(accountFiles).IsEquivalentTo(ExpectedAccountFiles);
        string[] cfgFiles = [.. manifest.Path("cs2game", "files")!.Children.Select(c => c.Key)];
        await Assert.That(cfgFiles).IsEquivalentTo(ExpectedCfgFiles);

        foreach (KvNode file in manifest.Path("cs2", "files")!.Children)
        {
            string copy = Path.Combine(temp.Path, "files", "cs2", file.Key.Replace('/', Path.DirectorySeparatorChar));
            await Assert.That(KitBackup.Sha256(copy)).IsEqualTo(file.Value);
        }
    }

    [Test]
    public async Task Capture_RefusesAFolderThatIsNotEmpty()
    {
        using FakeSteam steam = new();
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        await File.WriteAllTextAsync(temp.Sub("keep.txt"), "mine");

        await Assert.That(() => Capture(steam, temp.Path)).Throws<IOException>();
        await Assert.That(File.Exists(temp.Sub("keep.txt"))).IsTrue();
    }

    [Test]
    public async Task Restore_WritesTheFiles_KeepsTheTargetsGpuIdentity_AndUndoPutsTheTargetBack()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(vendorId: "4098", launchOptions: "-target");
        using TempDir temp = new();
        Capture(source, temp.Path);

        // Something the backup has and the target lacks, and something the target has that the backup lacks.
        File.Delete(target.PathOf(FakeSteam.Cfg("sd_core.cfg")));
        File.Delete(target.PathOf(FakeSteam.User(FakeSteam.AccountId, "remote/cfg/cs2_loadout_favorites.txt")));
        target.Write(FakeSteam.User(FakeSteam.AccountId, "remote/cs2_user_convars.vcfg"), FakeSteam.Convars("cloud"));
        string remoteCache = target.Read(FakeSteam.User(FakeSteam.AccountId, "remotecache.vdf"));
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo target");
        target.Write(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"), FakeSteam.Convars("target"));
        string targetVideo = target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"));
        string targetLocalConfig = target.Read($"userdata/{FakeSteam.AccountId}/config/localconfig.vdf");

        RunReport report = Engine.Restore(temp.Path, Options(target));

        await Assert.That(report.Count("done")).IsEqualTo(3).Because(string.Join("; ", report.Results));
        string convars = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg");
        await Assert.That(target.Read(convars)).IsEqualTo(FakeSteam.Convars(FakeSteam.AccountId));
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo autoexec\nexec sd_core");
        await Assert.That(target.Exists(FakeSteam.Cfg("sd_core.cfg"))).IsTrue();
        await AssertSteamCloudUntouched(target, remoteCache);
        await Assert.That(target.Exists($"userdata/{FakeSteam.AccountId}/config/localconfig.vdf.acb-bak")).IsFalse()
            .Because("the safety copy of Steam's file goes into the undo snapshot, not beside it");

        KvNode video = KvNode.Load(target.PathOf(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt")));
        await Assert.That(video.Get("VendorID")).IsEqualTo("4098").Because("the target's own GPU stays named");
        await Assert.That(video.Get("setting.defaultres")).IsEqualTo("2560");

        KvNode localConfig = KvNode.Load(target.PathOf($"userdata/{FakeSteam.AccountId}/config/localconfig.vdf"));
        await Assert.That(localConfig.Path("Software", "Valve", "Steam", "apps", "730")!.Get("LaunchOptions")).IsEqualTo("-novid -high");
        await Assert.That(localConfig.Path("Software", "Valve", "Steam", "apps", "730")!.Get("Playtime")).IsEqualTo("100");
        await Assert.That(localConfig.Path("Software", "Valve", "Steam", "apps", "570")!.Get("LaunchOptions")).IsEqualTo("-dota");
        await Assert.That(localConfig.Path("friends")!.Get("name")).IsEqualTo("x");

        await Assert.That(report.UndoDir).IsNotNull();
        KvNode undo = KitBackup.ReadManifest(report.UndoDir!);
        await Assert.That(undo.Get("kind")).IsEqualTo("undo");
        await Assert.That(File.ReadAllText(Path.Combine(report.UndoDir!, "files", "launch", "localconfig.vdf"))).IsEqualTo(targetLocalConfig);

        RunReport undone = Engine.Undo(report.UndoDir!, Options(target));

        await Assert.That(undone.Count("failed")).IsEqualTo(0).Because(string.Join("; ", undone.Results));
        await Assert.That(undone.UndoDir).IsNull().Because("undoing an undo records nothing");
        await Assert.That(target.Read(convars)).IsEqualTo(FakeSteam.Convars("target"));
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo target");
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt"))).IsEqualTo(targetVideo);
        await Assert.That(target.Exists(FakeSteam.Cfg("sd_core.cfg")))
            .IsFalse().Because("a file the restore added is removed again");
        await AssertSteamCloudUntouched(target, remoteCache);
        await Assert.That(KvNode.Load(target.PathOf($"userdata/{FakeSteam.AccountId}/config/localconfig.vdf"))
                .Path("Software", "Valve", "Steam", "apps", "730")!.Get("LaunchOptions"))
            .IsEqualTo("-target");
    }

    private static async Task AssertSteamCloudUntouched(FakeSteam target, string remoteCache)
    {
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "remote/cs2_user_convars.vcfg"))).IsEqualTo(FakeSteam.Convars("cloud"))
            .Because("remote/ is Steam Cloud's folder; a restore that wrote it would be uploaded over the player's cloud copy");
        await Assert.That(target.Exists(FakeSteam.User(FakeSteam.AccountId, "remote/cfg/cs2_loadout_favorites.txt"))).IsFalse();
        await Assert.That(target.Read(FakeSteam.User(FakeSteam.AccountId, "remotecache.vdf"))).IsEqualTo(remoteCache);
    }

    [Test]
    public async Task Restore_NeverWritesIntoTheAccountThatSignedInLast_WhenTheBackupsAccountHasNotSignedInHere()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(accountId: "1234", launchOptions: "-stranger");
        using TempDir temp = new();
        Capture(source, temp.Path);
        string strangers = target.Read(FakeSteam.User("1234", "local/cfg/cs2_user_convars_0_slot0.vcfg"));

        SteamInstall install = SteamInstall.Locate(target.Root)!;
        await Assert.That(install.MostRecentAccountId()).IsEqualTo("1234");
        await Assert.That(KitBackup.ResolveAccount(install, null, FakeSteam.AccountId)).IsNull();

        RunReport report = Engine.Restore(temp.Path, Options(target));

        foreach (string section in new[] { Sections.Cs2, Sections.Launch })
        {
            SectionResult result = report.Results.Single(r => r.Section == section);
            await Assert.That(result.Outcome).IsEqualTo("deferred").Because(result.ToString());
            await Assert.That(result.Detail).Contains("has not signed in to Steam on this PC");
        }

        await Assert.That(target.Read(FakeSteam.User("1234", "local/cfg/cs2_user_convars_0_slot0.vcfg"))).IsEqualTo(strangers);
        await Assert.That(target.Exists(FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"))).IsFalse();
        await Assert.That(install.ReadLaunchOptions("1234")).IsEqualTo("-stranger");
    }

    [Test]
    public async Task Restore_ToAnotherAccount_OnlyWhenNamed_AndSaysSo()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(accountId: "1234");
        using TempDir temp = new();
        Capture(source, temp.Path);
        RestoreOptions options = Options(target);
        options.AccountId = "1234";

        RunReport report = Engine.Restore(temp.Path, options);

        await Assert.That(report.Count("done")).IsEqualTo(3).Because(string.Join("; ", report.Results));
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Detail).Contains("NOT the backup's account " + FakeSteam.AccountId);
        await Assert.That(target.Read(FakeSteam.User("1234", "local/cfg/cs2_user_convars_0_slot0.vcfg")))
            .IsEqualTo(FakeSteam.Convars(FakeSteam.AccountId));
    }

    [Test]
    public async Task Restore_KeepsTheTargetsOwnDevices_InTheMachineConvarsAndVideo()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        string machine = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_machine_convars.vcfg");
        source.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"fps_max\"\t\t\"400\"\n\t\t\"voice_device_override\"\t\t\"Home Mic\"\n\t\t\"speaker_config\"\t\t\"2\"\n\t}\n}\n");
        target.Write(machine, "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"fps_max\"\t\t\"0\"\n\t\t\"speaker_config\"\t\t\"-1\"\n\t}\n}\n");
        string video = FakeSteam.User(FakeSteam.AccountId, "local/cfg/cs2_video.txt");
        target.Write(video, FakeSteam.Video("4098", "1", "1920", "1080").Replace("}", "\t\"setting.monitor_index\"\t\t\"1\"\n}", StringComparison.Ordinal));
        using TempDir temp = new();
        Capture(source, temp.Path);

        Engine.Restore(temp.Path, Options(target));

        KvNode convars = KvNode.Load(target.PathOf(machine)).Child("convars")!;
        await Assert.That(convars.Get("fps_max")).IsEqualTo("400").Because("the player's own choice travels");
        await Assert.That(convars.Get("speaker_config")).IsEqualTo("-1").Because("the target's speakers stay its own");
        await Assert.That(convars.Get("voice_device_override")).IsNull().Because("the source's microphone is not on this PC");
        KvNode merged = KvNode.Load(target.PathOf(video));
        await Assert.That(merged.Get("setting.monitor_index")).IsEqualTo("1");
        await Assert.That(merged.Get("setting.defaultres")).IsEqualTo("2560");
    }

    [Test]
    public async Task Capture_CarriesAnEditedServerCfg_AndLeavesValvesStub()
    {
        using FakeSteam steam = new();
        steam.Write(FakeSteam.Cfg("server.cfg"), "// Defaults in server_default.cfg");
        steam.Write(FakeSteam.Cfg("gamemode_competitive_server.cfg"), "mp_roundtime 60");
        using TempDir stub = new();
        Capture(steam, stub.Path);
        string[] files = [.. KitBackup.ReadManifest(stub.Path).Path("cs2game", "files")!.Children.Select(c => c.Key)];
        await Assert.That(files).Contains("gamemode_competitive_server.cfg");
        await Assert.That(files).DoesNotContain("server.cfg");

        steam.Write(FakeSteam.Cfg("server.cfg"), "// practice\nsv_cheats 1\nsv_infinite_ammo 1");
        using TempDir edited = new();
        Capture(steam, edited.Path);
        string[] after = [.. KitBackup.ReadManifest(edited.Path).Path("cs2game", "files")!.Children.Select(c => c.Key)];
        await Assert.That(after).Contains("server.cfg");
    }

    /// <summary>
    ///     A backup from an earlier build, or a hand-edited one, can name files capture no longer carries. The
    ///     restore applies capture's rules again, so Steam Cloud's remote/ folder and Valve's own cfg files stay
    ///     as the target has them, and the report names each file it left alone.
    /// </summary>
    [Test]
    public async Task Restore_WritesOnlyWhatCaptureWouldCarry_WhateverTheManifestNames()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        Capture(source, temp.Path);
        string remoteKeys = FakeSteam.User(FakeSteam.AccountId, "remote/cs2_user_keys.vcfg");
        string targetKeys = target.Read(remoteKeys);
        string targetGameMode = target.Read(FakeSteam.Cfg("gamemode_competitive.cfg"));

        KvNode manifest = KitBackup.ReadManifest(temp.Path);
        AddFile(temp.Path, manifest, Sections.Cs2, "remote/cs2_user_keys.vcfg", "\"config\" { \"bindings\" { \"MOUSE5\" \"quit\" } }");
        AddFile(temp.Path, manifest, Sections.Cs2Game, "gamemode_competitive.cfg", "mp_maxrounds 1");
        manifest.Save(Path.Combine(temp.Path, KitBackup.ManifestName));

        RunReport report = Engine.Restore(temp.Path, Options(target));

        await Assert.That(report.Count("failed")).IsEqualTo(0).Because(string.Join("; ", report.Results));
        await Assert.That(target.Read(remoteKeys)).IsEqualTo(targetKeys).Because("remote/ is Steam Cloud's folder");
        await Assert.That(target.Read(FakeSteam.Cfg("gamemode_competitive.cfg"))).IsEqualTo(targetGameMode).Because("Valve's file, shared by everyone");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Detail).Contains("remote/cs2_user_keys.vcfg");
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2Game).Detail).Contains("gamemode_competitive.cfg");
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo autoexec\nexec sd_core");

        KvNode undo = KitBackup.ReadManifest(report.UndoDir!);
        await Assert.That(undo.Path("cs2", "files")!.Child("remote/cs2_user_keys.vcfg")).IsNull()
            .Because("a file the restore left alone has nothing to undo");
    }

    private static void AddFile(string backupDir, KvNode manifest, string section, string relative, string text)
    {
        string copy = Path.Combine(backupDir, "files", section, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, text);
        manifest.Path(section, "files")!.Set(relative, KitBackup.Sha256(copy));
    }

    [Test]
    public async Task Restore_AsksForTheAccount_WhenThePcHasSeveralAndNoneIsClearlyYours()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(accountId: null);
        target.AddAccount("111");
        target.AddAccount("222");
        using TempDir temp = new();
        Capture(source, temp.Path);

        SteamInstall install = SteamInstall.Locate(target.Root)!;
        await Assert.That(KitBackup.ResolveAccount(install, null, FakeSteam.AccountId)).IsNull();
        await Assert.That(KitBackup.ResolveAccount(install, "222", FakeSteam.AccountId)).IsEqualTo("222");

        RunReport report = Engine.Restore(temp.Path, Options(target));
        await Assert.That(report.Results.Single(r => r.Section == Sections.Cs2).Outcome).IsEqualTo("deferred");
    }

    [Test]
    public async Task Restore_RefusesADamagedCopy()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        Capture(source, temp.Path);
        await File.AppendAllTextAsync(Path.Combine(temp.Path, "files", "cs2game", "autoexec.cfg"), "\nquit");

        RunReport report = Engine.Restore(temp.Path, Options(target));

        SectionResult cfg = report.Results.Single(r => r.Section == Sections.Cs2Game);
        await Assert.That(cfg.Outcome).IsEqualTo("failed");
        await Assert.That(cfg.Detail).Contains("autoexec.cfg");
    }

    [Test]
    public async Task DryRun_ChangesNothing()
    {
        using FakeSteam source = new();
        using FakeSteam target = new(launchOptions: "-target");
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo target");
        using TempDir temp = new();
        Capture(source, temp.Path);
        RestoreOptions options = Options(target);
        options.DryRun = true;

        RunReport report = Engine.Restore(temp.Path, options);

        await Assert.That(report.Count("done")).IsEqualTo(3);
        await Assert.That(report.UndoDir).IsNull();
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo target");
        await Assert.That(target.Exists($"userdata/{FakeSteam.AccountId}/config/localconfig.vdf.acb-bak")).IsFalse();
    }

    [Test]
    [Arguments("../escape.cfg")]
    [Arguments("a/../../escape.cfg")]
    [Arguments("/etc/passwd")]
    [Arguments("C:/Windows/win.ini")]
    [Arguments("a//b")]
    [Arguments("")]
    public async Task ARestoreNeverWritesOutsideItsFolder(string path)
    {
        await Assert.That(() => KitBackup.SafeRelative(path)).Throws<FormatException>();
    }

    [Test]
    public async Task ABackupCannotNameARegistryValueOutsideTheCarriedList()
    {
        await Assert.That(KitBackup.IsCarriedRegistryValue(@"Software\Microsoft\GameBar", "AutoGameModeEnabled")).IsTrue();
        await Assert.That(KitBackup.IsCarriedRegistryValue(@"Control Panel\Mouse", "SmoothMouseXCurve")).IsFalse()
            .Because("pointer curves do not reach CS2, which reads raw input");
        await Assert.That(KitBackup.IsCarriedRegistryValue(@"Software\Microsoft\DirectX\UserGpuPreferences", RegistryItem.Cs2ExeToken)).IsTrue();
        await Assert.That(KitBackup.IsCarriedRegistryValue(@"Software\Microsoft\Windows\CurrentVersion\Run", "evil")).IsFalse();
        await Assert.That(KitBackup.IsCarriedRegistryValue(@"Control Panel\Mouse", "Other")).IsFalse();
    }

    [Test]
    public async Task ANewerRecordFormatIsRefused()
    {
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        await File.WriteAllTextAsync(temp.Sub(KitBackup.ManifestName), "\"ConfigBackup\" { \"format\" \"2\" }");

        FormatException? ex = await Assert.That(() => KitBackup.ReadManifest(temp.Path)).Throws<FormatException>();
        await Assert.That(ex!.Message).Contains("this version reads format 1");
    }

    internal static RunReport Capture(FakeSteam steam, string outDir)
    {
        CaptureOptions options = new() { SteamRoot = steam.Root, Creator = "test" };
        options.Include.Clear();
        options.Include.AddRange(FileSections);
        return KitBackup.Capture(options, outDir, null);
    }

    /// <summary>The file sections into <paramref name="target" />, with the undo store in a folder of its own inside it.</summary>
    internal static RestoreOptions Options(FakeSteam target) => new()
    {
        SteamRoot = target.Root,
        Only = FileSections,
        SkipProcessChecks = true,
        UndoRoot = Path.Combine(target.Root, "acb-undo")
    };

    /// <summary>
    ///     In a test assembly the engine refuses to write a Windows or driver section, before the first read or write,
    ///     so a test can never change this PC. The only entry is one the carried list refuses as well.
    /// </summary>
    [Test]
    public async Task ATestRun_NeverWritesAWindowsSection()
    {
        await Assert.That(KitBackup.MachineWritesRefused).IsTrue();
        using FakeSteam target = new();
        using TempDir temp = new();
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, KitBackup.ManifestName), """
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"input"
            	{
            		"Software\\acb-test-never-written"		"0"
            	}
            }
            """);
        RestoreOptions options = Options(target);
        options.Only = [Sections.Input];
        options.WriteUndo = true;

        SectionResult result = Engine.Restore(temp.Path, options).Results.Single();

        await Assert.That(result.Outcome).IsEqualTo(OperatingSystem.IsWindows() ? Outcomes.Failed : Outcomes.Skipped).Because(result.ToString());
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(result.Detail).Contains("a test run never writes this PC's Windows or driver settings");
        }
    }

    /// <summary>
    ///     In a test assembly an engine restore that names no Steam folder never looks for this PC's own Steam, so it
    ///     writes nothing there.
    /// </summary>
    [Test]
    public async Task ATestRun_NeverFindsThisPcsSteam()
    {
        using FakeSteam source = new();
        source.Write(FakeSteam.Cfg("autoexec.cfg"), "sensitivity 9");
        using TempDir capture = new();
        CaptureOptions capturing = new() { SteamRoot = source.Root };
        capturing.Include.Clear();
        capturing.Include.Add(Sections.Cs2Game);
        KitBackup.Capture(capturing, capture.Path, TextWriter.Null);
        using FakeSteam target = new();
        RestoreOptions options = Options(target);
        options.SteamRoot = null;
        options.Only = [Sections.Cs2Game];

        await Assert.That(() => Engine.Restore(capture.Path, options)).Throws<InvalidOperationException>()
            .WithMessageContaining("never looks for this PC's own Steam");
        await Assert.That(() => SteamInstall.Locate(null)).Throws<InvalidOperationException>();
        await Assert.That(SteamInstall.Locate(target.Root)).IsNotNull();
    }

    /// <summary>
    ///     A record's part is stranded only when what its restore wrote through is gone: the recorded Steam folder,
    ///     the account's folder (cs2, launch) or the CS2 install (cs2game, cs2exe). Otherwise it can still be put back.
    /// </summary>
    [Test]
    public async Task StrandedReason_IsGivenOnlyWhenTheRecordedSteamAccountOrInstallIsGone()
    {
        using FakeSteam target = new();
        KvNode manifest = new(KitBackup.RootKey);
        manifest.Set("steam", target.Root);
        manifest.Section(Sections.Cs2).Set("account", FakeSteam.AccountId);
        manifest.Section(Sections.Launch).Set("account", FakeSteam.AccountId);

        foreach (string part in new[] { Sections.Cs2, Sections.Launch, Sections.Cs2Game, Sections.Cs2Exe })
        {
            await Assert.That(KitBackup.StrandedReason(manifest, part)).IsNull().Because(part);
        }

        Directory.Delete(target.PathOf("steamapps/common/Counter-Strike Global Offensive"), recursive: true);
        await Assert.That(KitBackup.StrandedReason(manifest, Sections.Cs2Game)).Contains("CS2 is not installed");
        await Assert.That(KitBackup.StrandedReason(manifest, Sections.Cs2Exe)).Contains("CS2 is not installed");
        await Assert.That(KitBackup.StrandedReason(manifest, Sections.Cs2)).IsNull();

        Directory.Delete(target.PathOf("userdata/" + FakeSteam.AccountId), recursive: true);
        await Assert.That(KitBackup.StrandedReason(manifest, Sections.Cs2)).Contains("account " + FakeSteam.AccountId + " is gone");
        await Assert.That(KitBackup.StrandedReason(manifest, Sections.Launch)).Contains("is gone");

        KvNode moved = new(KitBackup.RootKey);
        moved.Set("steam", Path.Combine(target.Root, "no-such-steam"));
        await Assert.That(KitBackup.StrandedReason(moved, Sections.Cs2Game)).Contains("Steam is gone from");

        KvNode unnamed = new(KitBackup.RootKey);
        await Assert.That(KitBackup.StrandedReason(unnamed, Sections.Cs2Game)).IsNull().Because("a record that names no Steam is not known to be stranded");
    }

    /// <summary>Nothing restores without its record: a restore that changes anything and names no undo store is refused before its first change.</summary>
    [Test]
    public async Task ARestoreWithoutAnUndoRecord_IsRefusedBeforeAnyChange()
    {
        using FakeSteam source = new();
        using FakeSteam target = new();
        using TempDir temp = new();
        Capture(source, temp.Path);
        target.Write(FakeSteam.Cfg("autoexec.cfg"), "echo target");

        RestoreOptions noStore = Options(target);
        noStore.UndoRoot = null;
        RestoreOptions noRecord = Options(target);
        noRecord.WriteUndo = false;

        await Assert.That(() => Engine.Restore(temp.Path, noStore)).Throws<ArgumentException>();
        await Assert.That(() => Engine.Restore(temp.Path, noRecord)).Throws<ArgumentException>();
        await Assert.That(target.Read(FakeSteam.Cfg("autoexec.cfg"))).IsEqualTo("echo target");
    }
}
