using CS2OpenDev.ConfigManager.Cli;
using CS2OpenDev.ConfigManager.Kit;
using CS2OpenDev.ConfigManager.Restoring;
using CS2OpenDev.ConfigManager.Tests;

namespace CS2OpenDev.ConfigManager.Cli.Tests;

/// <summary>
///     What a restore may reach, through acb's own command line: an archive asks for less than the rules allow,
///     never more, and an undo goes back exactly where its restore wrote.
/// </summary>
public class RestoreScopeTests
{
    private const string Autoexec = "autoexec.cfg";

    /// <summary>Capture records every file by its SHA-256. "absent" means delete, which only an undo record may say.</summary>
    [Test]
    [Arguments(Sections.Cs2Game, "autoexec.cfg")]
    [Arguments(Sections.Cs2, "local/cfg/cs2_video.txt")]
    public async Task AnArchiveThatSaysAFileIsAbsent_DeletesNothing(string section, string relative)
    {
        using TestHost host = new();
        using FakeSteam target = new();
        string targetPath = section == Sections.Cs2 ? FakeSteam.User(FakeSteam.AccountId, relative) : FakeSteam.Cfg(relative);
        string before = target.Read(targetPath);
        string zip = TestArchives.Craft(host.Temp, $$"""
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"{{section}}"
            	{
            {{(section == Sections.Cs2 ? $"\t\t\"account\"\t\t\"{FakeSteam.AccountId}\"\n" : "")}}		"files"
            		{
            			"{{relative}}"		"absent"
            		}
            	}
            }
            """);

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("is not a file's SHA-256");
        await Assert.That(target.Exists(targetPath)).IsTrue();
        await Assert.That(target.Read(targetPath)).IsEqualTo(before);
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>
    ///     A game state integration cfg sends live game data to the address it names for every account on the PC,
    ///     so a restore writes it only when asked, and names it either way; the plan file carries the choice.
    /// </summary>
    [Test]
    public async Task GameStateIntegrationFiles_AreWrittenOnlyWithGsi_AndNamed()
    {
        using TestHost host = new();
        using FakeSteam target = new();
        const string gsi = "\"x\" { \"uri\" \"https://example.invalid/gsi\" }";
        const string name = "gamestate_integration_x.cfg";
        string zip = TestArchives.Craft(host.Temp, $$"""
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"cs2game"
            	{
            		"files"
            		{
            			"{{name}}"		"{{TestArchives.Sha(gsi)}}"
            			"autoexec.cfg"		"{{TestArchives.Sha("echo mine")}}"
            		}
            	}
            }
            """, new Dictionary<string, string> { ["files/cs2game/" + name] = gsi, ["files/cs2game/autoexec.cfg"] = "echo mine" });

        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("restored only with --gsi: " + name);
        await Assert.That(target.Exists(FakeSteam.Cfg(name))).IsFalse();
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo mine");

        string plan = host.Temp.Sub("plan.json");
        await Assert.That(host.Run("plan", "--gsi", "--report", plan, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("game state integration files, which send live game data to the address they name: " + name);
        // The plan file is not signed: what it widens is said again on the command line, or nothing is written.
        await Assert.That(host.Run("restore", "--plan", plan, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("Give --gsi as well");
        await Assert.That(target.Exists(FakeSteam.Cfg(name))).IsFalse();
        await Assert.That(host.Run("restore", "--plan", plan, "--yes", "--gsi")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(name))).IsEqualTo(gsi);
    }

    /// <summary>
    ///     A restore records which Steam it wrote to, and a plain acb undo writes back there, not into whichever Steam
    ///     this PC would find first. If that Steam is gone, the parts wait and say where they looked.
    /// </summary>
    [Test]
    public async Task Undo_WithoutSteamPath_GoesBackIntoTheSteamTheRestoreWroteTo()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        source.Write(FakeSteam.Cfg(Autoexec), "echo restored");
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        // Checked before any undo runs without --steam-path, so a missing note can never send it to this PC's own Steam.
        await Assert.That(host.Records().Single().Manifest!.Get("steam")).IsEqualTo(Path.GetFullPath(target.Root));

        string moved = target.Root + "-moved";
        Directory.Move(target.Root, moved);
        int waiting;
        try
        {
            waiting = host.Run("undo", "--yes");
        }
        finally
        {
            Directory.Move(moved, target.Root);
        }

        await Assert.That(waiting).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("Steam was not found at " + Path.GetFullPath(target.Root));
        await Assert.That(host.Records()).IsNotEmpty();

        int code = host.Run("undo", "--yes");

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>
    ///     A backup can never name a registry value outside the carried list. The check runs over every entry before
    ///     the first is read or written, so the part fails and nothing is recorded. Only a plan is run: a restore takes
    ///     the same check, but if the check were ever lost it would write this PC's real registry.
    /// </summary>
    [Test]
    public async Task ARegistryValueOutsideTheCarriedList_FailsThePart_BeforeAnyRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("registry sections are Windows only");
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        // Only entries the pre-pass refuses: nothing here may ever reach this PC's registry.
        string zip = TestArchives.Craft(host.Temp, """
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"gamebar"
            	{
            		"registry"
            		{
            			"0"
            			{
            				"key"		"Software\\Microsoft\\Windows\\CurrentVersion\\Run"
            				"name"		"acb-test-never-written"
            				"kind"		"string"
            				"data"		"calc.exe"
            			}
            		}
            	}
            }
            """);

        int code = host.Run("plan", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("failed");
        await Assert.That(host.Output).Contains("does not carry");
        await Assert.That(host.Records()).IsEmpty();
        await Assert.That(Directory.Exists(host.UserStore) ? Directory.GetFileSystemEntries(host.UserStore) : []).IsEmpty();
    }

    /// <summary>
    ///     acb's tests restore Windows sections into a fake machine; one whose host has none is refused before it reads
    ///     or writes this PC. The archive's only entry is also one the carried list refuses, so even the fake is not
    ///     written.
    /// </summary>
    [Test]
    public async Task ATestRun_NeverReachesThisPcsWindowsSettings()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("registry sections are Windows only");
            return;
        }

        await Assert.That(ConfigBackup.MachineWritesRefused).IsTrue();
        using TestHost host = new();
        using FakeSteam target = new();
        string zip = TestArchives.Craft(host.Temp, """
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"gamebar"
            	{
            		"registry"
            		{
            			"0"
            			{
            				"key"		"Software\\acb-test-never-written"
            				"name"		"acb-test-never-written"
            				"kind"		"dword"
            				"data"		"0"
            			}
            		}
            	}
            }
            """);

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("does not carry");
        await Assert.That(host.Machine!.Writes).IsEmpty();
        await Assert.That(host.Records()).IsEmpty();

        // A carried value gets past the list to the machine, which without a fake refuses even to read.
        string carried = TestArchives.Craft(host.Temp, """
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"gamebar"
            	{
            		"registry"
            		{
            			"0"
            			{
            				"key"		"Software\\Microsoft\\GameBar"
            				"name"		"AutoGameModeEnabled"
            				"kind"		"dword"
            				"data"		"1"
            			}
            		}
            	}
            }
            """);
        host.Machine = null;
        await Assert.That(host.Run("plan", "--steam-path", target.Root, carried)).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains(RefusingMachine.Message);
    }

    /// <summary>
    ///     An archive that names one file twice would have the second entry recorded over the first, so the undo
    ///     record would keep the restore's own bytes and acb undo could never put the PC's file back. The part is
    ///     refused before anything is recorded or written.
    /// </summary>
    [Test]
    [Arguments("autoexec.cfg")]
    [Arguments("AUTOEXEC.CFG")]
    public async Task AnArchiveThatNamesAFileTwice_WritesNothingInThatPart(string second)
    {
        if (second != Autoexec && !OperatingSystem.IsWindows())
        {
            Skip.Test("names that differ only in case are one file only on Windows");
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        string zip = TestArchives.Craft(host.Temp, $$"""
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"cs2game"
            	{
            		"files"
            		{
            			"autoexec.cfg"		"{{TestArchives.Sha("bind x quit")}}"
            			"{{second}}"		"{{TestArchives.Sha("bind x quit")}}"
            		}
            	}
            }
            """, new Dictionary<string, string> { ["files/cs2game/autoexec.cfg"] = "bind x quit" });

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("twice");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>
    ///     Windows also opens a file by its 8.3 alias, so GAMEST~1.CFG could be gamestate_integration_x.cfg (or a
    ///     Valve gamemode_*.cfg) under a name the rules would pass. A name that could be an alias is never restored.
    /// </summary>
    [Test]
    [Arguments("GAMEST~1.CFG")]
    [Arguments("gamestate_integration_x.cfg.")]
    public async Task ANameThatCouldBeAnAlias_IsLeftAlone_AndTheFileItMightNameIsUntouched(string alias)
    {
        using TestHost host = new();
        using FakeSteam target = new();
        const string observer = "\"observer\" { \"uri\" \"http://127.0.0.1:3000\" }";
        target.Write(FakeSteam.Cfg("gamestate_integration_x.cfg"), observer);
        string zip = TestArchives.Craft(host.Temp, $$"""
            "ConfigBackup"
            {
            	"format"		"1"
            	"kind"		"backup"
            	"cs2game"
            	{
            		"files"
            		{
            			"{{alias}}"		"{{TestArchives.Sha("\"x\" { \"uri\" \"https://example.invalid/gsi\" }")}}"
            		}
            	}
            }
            """);

        int code = host.Run("restore", "--yes", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("left alone, as files this tool does not restore: " + alias);
        await Assert.That(target.Read(FakeSteam.Cfg("gamestate_integration_x.cfg"))).IsEqualTo(observer);
    }

    /// <summary>
    ///     One NVIDIA setting under two spellings, or one registry value twice, would be recorded twice, the second
    ///     time with the backup's value. Both are refused before the driver or the registry is read; only a plan is
    ///     run, so nothing here could reach this PC's settings.
    /// </summary>
    [Test]
    [Arguments("nvidia")]
    [Arguments("gamebar")]
    public async Task ASettingNamedTwice_FailsThePart_BeforeAnyRead(string section)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test("NVIDIA and registry sections are Windows only");
            return;
        }

        using TestHost host = new();
        using FakeSteam target = new();
        string body = section == "nvidia"
            ? """
              	"nvidia"
              	{
              		"cs2"
              		{
              			"settings"
              			{
              				"0x10835002" { "name" "Frame Rate Limiter" "type" "dword" "value" "0" }
              				"0x010835002" { "name" "Frame Rate Limiter" "type" "dword" "value" "0" }
              			}
              		}
              	}
              """
            : """
              	"gamebar"
              	{
              		"registry"
              		{
              			"0" { "key" "Software\\Microsoft\\GameBar" "name" "AllowAutoGameMode" "kind" "dword" "data" "1" }
              			"1" { "key" "software\\microsoft\\gamebar" "name" "allowautogamemode" "kind" "dword" "data" "1" }
              		}
              	}
              """;
        string zip = TestArchives.Craft(host.Temp, "\"ConfigBackup\"\n{\n\t\"format\"\t\t\"1\"\n\t\"kind\"\t\t\"backup\"\n" + body + "\n}\n");

        int code = host.Run("plan", "--steam-path", target.Root, zip);

        await Assert.That(code).IsEqualTo(ExitCodes.PartFailed).Because(host.All);
        await Assert.That(host.Output).Contains("twice");
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>
    ///     acb undo forgets the account's parts only when the account is gone from the Steam its restore wrote to.
    ///     Pointed at another folder (a library mistaken for Steam), the parts wait in the record and say where acb
    ///     looked, so a rerun with the right folder still puts them back.
    /// </summary>
    [Test]
    public async Task Undo_IntoAFolderWithoutTheAccount_KeepsTheAccountsParts()
    {
        using TestHost host = new();
        using FakeSteam source = new(launchOptions: "-novid -freq 360");
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2, Sections.Launch);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        string library = host.Temp.Sub("SteamLibrary");
        Directory.CreateDirectory(library);

        int code = host.Run("undo", "--yes", "--steam-path", library);

        await Assert.That(code).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
        await Assert.That(host.Output).Contains("run acb undo again with --steam-path");
        await Assert.That(host.Records().Single().Parts).IsEquivalentTo([Sections.Cs2, Sections.Launch]);

        await Assert.That(host.Run("undo", "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(target.Read("userdata/" + FakeSteam.AccountId + "/config/localconfig.vdf")).Contains("-novid -high");
        await Assert.That(host.Records()).IsEmpty();
    }

    /// <summary>
    ///     A wipe that takes the whole userdata folder leaves the account's parts waiting, and says so rather than
    ///     sending the person back to the same folder. Setting the record aside is the deliberate way out: asked
    ///     about, and naming what is never put back.
    /// </summary>
    [Test]
    public async Task Undo_AfterUserdataIsWiped_WaitsAndSaysSo_AndTheRecordCanBeSetAsideOnPurpose()
    {
        using TestHost host = new();
        using FakeSteam source = new(launchOptions: "-novid -freq 360");
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2, Sections.Launch);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        Directory.Delete(Path.Combine(target.Root, "userdata"), recursive: true);

        for (int run = 0; run < 2; run++)
        {
            await Assert.That(host.Run("undo", "--yes")).IsEqualTo(ExitCodes.PartsLeft).Because(host.All);
            await Assert.That(host.Output).Contains("which has no userdata folder at all");
            await Assert.That(host.Output).DoesNotContain("--steam-path naming that Steam folder");
        }

        string name = host.Records().Single().Name;
        await Assert.That(host.Run("undo", "--set-aside", name)).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("is never put back");
        await Assert.That(host.Records()).IsNotEmpty();

        await Assert.That(host.Run("undo", "--set-aside", name, "--dry-run", "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("a dry run sets nothing aside");
        await Assert.That(host.Records().Single().Name).IsEqualTo(name);

        await Assert.That(host.Run("undo", "--set-aside", name, "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Records()).IsEmpty();
        await Assert.That(Directory.Exists(Path.Combine(host.UserStore, UndoStore.SetAsideFolder, name))).IsTrue();
        await Assert.That(host.Run("undo", "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("Nothing to undo");
    }

    /// <summary>
    ///     A readable record whose Steam folder, account and CS2 install are all still there is not set aside, even
    ///     with --yes: its parts can still be put back (here they wait only for CS2 to close).
    /// </summary>
    [Test]
    public async Task SetAside_OfARecordWhoseSteamIsStillThere_IsRefused_AndKeepsTheRecord()
    {
        using TestHost host = new();
        using FakeSteam source = new(launchOptions: "-novid -freq 360");
        using FakeSteam target = new();
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2, Sections.Launch);
        await Assert.That(host.Run("restore", "--yes", "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        string name = host.Records().Single().Name;

        await Assert.That(host.Run("undo", "--set-aside", name, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output + host.Errors).Contains("can still be put back");
        await Assert.That(host.Records().Single().Name).IsEqualTo(name);
        await Assert.That(Directory.Exists(Path.Combine(host.UserStore, UndoStore.SetAsideFolder))).IsFalse();
    }

    /// <summary>
    ///     A plan file is not signed. The organiser can pin their own policy with --policy, and a plan made under
    ///     another policy, or none, is refused before anything changes.
    /// </summary>
    [Test]
    public async Task RestoreAPlan_WithTheOrganisersPolicy_RefusesAPlanMadeWithoutIt()
    {
        using TestHost host = new();
        using FakeSteam source = new();
        using FakeSteam target = new();
        source.Write(FakeSteam.Cfg(Autoexec), "echo restored");
        target.Write(FakeSteam.Cfg(Autoexec), "echo original");
        string zip = TestArchives.FromSteam(source, host.Temp, Sections.Cs2Game);
        string policy = host.Temp.Sub("policy.json");
        await File.WriteAllTextAsync(policy, """{ "sections": { "allow": ["cs2game"] } }""");
        string bare = host.Temp.Sub("bare.json");
        string pinned = host.Temp.Sub("pinned.json");
        await Assert.That(host.Run("plan", "--report", bare, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Run("plan", "--report", pinned, "--policy", policy, "--steam-path", target.Root, zip)).IsEqualTo(ExitCodes.Done).Because(host.All);

        await Assert.That(host.Run("restore", "--plan", bare, "--policy", policy, "--yes")).IsEqualTo(ExitCodes.Refused).Because(host.All);
        await Assert.That(host.Output).Contains("made without a policy");
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo original");

        await Assert.That(host.Run("restore", "--plan", pinned, "--policy", policy, "--yes")).IsEqualTo(ExitCodes.Done).Because(host.All);
        await Assert.That(host.Output).Contains("Policy:   " + Path.GetFullPath(policy));
        await Assert.That(target.Read(FakeSteam.Cfg(Autoexec))).IsEqualTo("echo restored");
    }
}
