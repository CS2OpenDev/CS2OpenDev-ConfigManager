using System.Text;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>
///     A Steam folder in temp shaped like a real one: libraryfolders.vdf, the CS2 app manifest and install, a
///     userdata account with CS2's settings files as CS2 itself names them, and loginusers.vdf. The file names
///     and the userdata layout are the ones a real install held when this was written.
/// </summary>
internal sealed class FakeSteam : IDisposable
{
    public const string AccountId = "69413322";
    public const long SteamId64 = 76561197960265728L + 69413322L;

    public FakeSteam(string? accountId = AccountId, string vendorId = "4318", string launchOptions = "-novid -high")
    {
        Root = Path.Combine(Path.GetTempPath(), "dv-acb-steam-" + Guid.NewGuid().ToString("N"));
        Write("steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{Root.Replace("\\", "\\\\", StringComparison.Ordinal)}}"
            		"apps"
            		{
            			"730"		"76050433771"
            		}
            	}
            }
            """);
        Write("steamapps/appmanifest_730.acf", """
            "AppState"
            {
            	"appid"		"730"
            	"installdir"		"Counter-Strike Global Offensive"
            }
            """);

        // Valve's own cfg files, which a backup must leave behind, beside the player's.
        Write(Cfg("gamemode_competitive.cfg"), "mp_maxrounds 24");
        Write(Cfg("server.cfg"), "");
        Write(Cfg("user_keys_default.vcfg"), "\"config\" { }");
        Write(Cfg("perftest.cfg"), "");
        Write(Cfg("boot.vcfg"), "");
        Write(Cfg("devonly/dev.cfg"), "");
        Write(Cfg("gamestate_integration_overlay.cfg"), "\"overlay\" { }");
        Write(Cfg("autoexec.cfg"), "echo autoexec\nexec sd_core");
        Write(Cfg("sd_core.cfg"), "bind mouse5 +lookatweapon");
        Write(Cfg("practice/nades.cfg"), "sv_cheats 1");

        Write("steamapps/common/Counter-Strike Global Offensive/game/bin/win64/cs2.exe", "");

        if (accountId is not null)
        {
            AddAccount(accountId, vendorId, launchOptions);
            Write("config/loginusers.vdf", $$"""
                "users"
                {
                	"{{76561197960265728L + long.Parse(accountId, System.Globalization.CultureInfo.InvariantCulture)}}"
                	{
                		"AccountName"		"someone"
                		"MostRecent"		"1"
                	}
                }
                """);
        }
    }

    public string Root { get; }

    public static string Cfg(string name) => "steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/" + name;

    public static string User(string accountId, string relative) => $"userdata/{accountId}/730/{relative}";

    public void AddAccount(string accountId, string vendorId = "4318", string launchOptions = "-novid -high")
    {
        Write(User(accountId, "local/cfg/cs2_user_convars_0_slot0.vcfg"), Convars(accountId));
        Write(User(accountId, "local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded"), Convars(accountId));
        Write(User(accountId, "local/cfg/cs2_user_keys_0_slot0.vcfg"), "\"config\"\n{\n\t\"bindings\"\n\t{\n\t\t\"MOUSE4\"\t\t\"slot7\"\n\t}\n}\n");
        Write(User(accountId, "local/cfg/cs2_machine_convars.vcfg"), "\"config\"\n{\n\t\"convars\"\n\t{\n\t\t\"volume\"\t\t\"0.3\"\n\t}\n}\n");
        Write(User(accountId, "local/cfg/cs2_video.txt"), Video(vendorId, "9989", "2560", "1440"));
        Write(User(accountId, "local/cfg/cs2_video.txt.bak"), "old");
        Write(User(accountId, "local/cfg/trustedlaunch.cfg"), "3,3,3856,0x1db1cda");
        Write(User(accountId, "local/socache.dt"), "cache");
        Write(User(accountId, "local/pet/1/library/shot.jpg"), "jpeg");
        Write(User(accountId, "remote/cs2_user_convars.vcfg"), Convars(accountId));
        Write(User(accountId, "remote/cs2_user_keys.vcfg"), "\"config\" { }");
        Write(User(accountId, "remote/cfg/cs2_loadout_favorites.txt"), "fav");
        Write(User(accountId, "remote/cfg/workshop_saves/save_1.txt"), "save");
        Write(User(accountId, "remote/voice_ban.dt"), "bans");
        Write(User(accountId, "remotecache.vdf"), "\"730\" { }");
        Write($"userdata/{accountId}/config/localconfig.vdf", $$"""
            "UserLocalConfigStore"
            {
            	"Software"
            	{
            		"Valve"
            		{
            			"Steam"
            			{
            				"apps"
            				{
            					"570"
            					{
            						"LaunchOptions"		"-dota"
            					}
            					"730"
            					{
            						"Playtime"		"100"
            						"LaunchOptions"		"{{launchOptions}}"
            					}
            				}
            			}
            		}
            	}
            	"friends"
            	{
            		"name"		"x"
            	}
            }
            """);
    }

    public static string Convars(string tag) =>
        $"\"config\"\n{{\n\t\"convars\"\n\t{{\n\t\t\"cl_crosshairsize\"\t\t\"1.5\"\n\t\t\"tag\"\t\t\"{tag}\"\n\t}}\n}}\n";

    public static string Video(string vendor, string device, string width, string height) =>
        "\"video.cfg\"\n{\n"
        + $"\t\"setting.defaultres\"\t\t\"{width}\"\n\t\"setting.defaultresheight\"\t\t\"{height}\"\n"
        + $"\t\"Version\"\t\t\"16\"\n\t\"VendorID\"\t\t\"{vendor}\"\n\t\"DeviceID\"\t\t\"{device}\"\n"
        + "\t\"setting.mat_vsync\"\t\t\"0\"\n}\n";

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Read(string relative) => File.ReadAllText(PathOf(relative), Encoding.UTF8);

    public bool Exists(string relative) => File.Exists(PathOf(relative));

    public void Delete(string relative) => File.Delete(PathOf(relative));

    public void Write(string relative, string text)
    {
        string path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>A temp folder deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dv-acb-" + Guid.NewGuid().ToString("N"));
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
