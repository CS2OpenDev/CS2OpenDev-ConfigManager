using System.Reflection;
using System.Text.Json;

namespace CS2OpenDev.ConfigManager.DemoViewer.Tests;

/// <summary>The manifest the build puts beside the plugin's DLL, which is what the DemoViewer.NET host reads.</summary>
public class PackagingTests
{
    [Test]
    public async Task TheCopyBesideTheDll_IsTheEmbeddedManifest_ForThisPack_AtTheAssemblysVersion()
    {
        string copy = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "extension.json"));
        Assembly assembly = typeof(ConfigBackupPack).Assembly;
        using Stream embeddedStream = assembly.GetManifestResourceStream("extension.json")
                                      ?? throw new InvalidOperationException("The plugin embeds no extension.json.");
        using StreamReader reader = new(embeddedStream);
        string embedded = await reader.ReadToEndAsync();

        using JsonDocument manifest = JsonDocument.Parse(copy);
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        await Assert.That(copy).IsEqualTo(embedded).Because("the host checks the embedded copy against the one on disk");
        await Assert.That(manifest.RootElement.GetProperty("id").GetString()).IsEqualTo(ConfigBackupPack.PackId);
        await Assert.That(manifest.RootElement.GetProperty("assembly").GetString()).IsEqualTo(Path.GetFileName(assembly.Location));
        await Assert.That(manifest.RootElement.GetProperty("entryType").GetString()).IsEqualTo(typeof(ConfigBackupPack).FullName);
        await Assert.That(informational).IsNotNull();
        await Assert.That(informational!.Split('+')[0]).StartsWith(manifest.RootElement.GetProperty("version").GetString()!.Split('-')[0])
            .Because("the manifest version and the assembly version come from the same version.json");
    }

    /// <summary>
    ///     The plugin's own build folder (what ships) holds the Argon2 assemblies sealed backups need, and no copy of
    ///     an assembly the host loads itself, which the host would refuse if it differed from its own.
    /// </summary>
    [Test]
    public async Task ThePluginShips_CoresOwnPackages_AndNoneOfTheHosts()
    {
        string configuration = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string ships = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "CS2OpenDev.ConfigManager.DemoViewer", configuration));
        string[] files = [.. Directory.EnumerateFiles(ships, "*.dll").Select(Path.GetFileName).OfType<string>()];

        await Assert.That(files).Contains("Konscious.Security.Cryptography.Argon2.dll");
        await Assert.That(files).Contains("Konscious.Security.Cryptography.Blake2.dll");
        await Assert.That(files).Contains("CS2OpenDev.ConfigManager.Core.dll");
        await Assert.That(files.Where(f => f.StartsWith("Avalonia", StringComparison.Ordinal) || f.StartsWith("DemoViewer.", StringComparison.Ordinal)
                                           || f.StartsWith("CommunityToolkit", StringComparison.Ordinal) || f.StartsWith("Microsoft.", StringComparison.Ordinal)))
            .IsEmpty().Because(ships);
    }

    /// <summary>
    ///     A plugin that is not bundled with DemoViewer.NET is held to the third-party naming rules: an id outside
    ///     the reserved net.demoviewer. prefix, and contribution ids under its own id.
    /// </summary>
    [Test]
    public async Task TheIds_FollowTheRulesForAPluginThatIsNotBundled()
    {
        await Assert.That(ConfigBackupPack.PackId).DoesNotStartWith("net.demoviewer.");
        await Assert.That(ConfigBackupPack.PageId).StartsWith(ConfigBackupPack.PackId + ".");
    }
}
