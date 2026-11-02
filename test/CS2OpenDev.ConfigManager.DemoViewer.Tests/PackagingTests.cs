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
