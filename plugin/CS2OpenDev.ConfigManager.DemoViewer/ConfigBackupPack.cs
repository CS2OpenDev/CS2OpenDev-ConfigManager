using CS2OpenDev.ConfigManager.DemoViewer.ViewModels;
using CS2OpenDev.ConfigManager.DemoViewer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CS2OpenDev.ConfigManager.DemoViewer;

/// <summary>
///     Advanced Config Backup: saves a player's CS2 settings, and the Windows and NVIDIA settings that change how
///     CS2 feels, into one zip that the acb restorer applies on another PC and later takes off again.
///     For players who move between PCs at internet cafes, LANs and tournaments. It adds one Settings page and
///     runs nothing in the background.
/// </summary>
public sealed class ConfigBackupPack : IExtension
{
    public const string PackId = "dev.cs2opendev.configmanager";
    public const string MasterSwitch = PackId;
    public const string PageId = PackId + ".page";

    public string Id => PackId;

    public string FeatureId => MasterSwitch;

    public IEnumerable<ExtensionFeature> Features =>
    [
        new(MasterSwitch, ExtensionFeatureKind.Extension, "Advanced Config Backup",
            "Save your CS2, Windows and NVIDIA settings to a zip that sets up another PC.", null, AudienceDefaults.Everyone)
    ];

    public void Register(IServiceCollection services)
    {
    }

    public void Contribute(IExtensionContributions contributions, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        IExtensionContext context = contributions.Context;

        // A fresh view model per Settings open: it looks for Steam and the GPU when it is built.
        contributions.SettingsPage(new SettingsPageContribution(
            PageId,
            "ADVANCED CONFIG BACKUP",
            0,
            "backup config cfg settings binds crosshair autoexec launch options nvidia vibrance windows mouse lan cafe tournament restore",
            () => new ConfigBackupPageViewModel(context),
            () => new ConfigBackupPageView()));
    }
}
