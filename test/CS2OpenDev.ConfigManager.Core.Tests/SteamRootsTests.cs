using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.Tests;

/// <summary>Where Steam is found on Linux, under a home folder of the test's own.</summary>
public class SteamRootsTests
{
    private static string MakeSteam(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        return root;
    }

    [Test]
    public async Task NoSteamUnderTheHome_FindsNone()
    {
        using TempDir home = new();
        Directory.CreateDirectory(home.Path);

        await Assert.That(SteamInstall.LocateUnderHome(home.Path)).IsNull();
    }

    /// <summary>The native install is found before Flatpak's, and Flatpak's alone is found too.</summary>
    [Test]
    public async Task TheNativeInstall_ComesBeforeFlatpaks()
    {
        using TempDir home = new();
        string flatpak = MakeSteam(Path.Combine(home.Path, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));

        await Assert.That(SteamInstall.LocateUnderHome(home.Path)!.Root).IsEqualTo(Path.GetFullPath(flatpak));

        string native = MakeSteam(Path.Combine(home.Path, ".local", "share", "Steam"));
        await Assert.That(SteamInstall.LocateUnderHome(home.Path)!.Root).IsEqualTo(Path.GetFullPath(native));
    }

    /// <summary>~/.steam/steam is a link Steam makes; the install is named by the folder it points to, so one Steam has one path.</summary>
    [Test]
    public async Task TheSteamLink_IsNamedByTheFolderItPointsTo()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("~/.steam/steam is a Linux link");
            return;
        }

        using TempDir home = new();
        string native = MakeSteam(Path.Combine(home.Path, ".local", "share", "Steam"));
        Directory.CreateDirectory(Path.Combine(home.Path, ".steam"));
        Directory.CreateSymbolicLink(Path.Combine(home.Path, ".steam", "steam"), native);

        await Assert.That(SteamInstall.LocateUnderHome(home.Path)!.Root).IsEqualTo(Path.GetFullPath(native));
    }
}
