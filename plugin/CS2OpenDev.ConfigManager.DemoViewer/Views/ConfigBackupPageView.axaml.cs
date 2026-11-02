using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CS2OpenDev.ConfigManager.DemoViewer.ViewModels;

namespace CS2OpenDev.ConfigManager.DemoViewer.Views;

public partial class ConfigBackupPageView : UserControl
{
    public ConfigBackupPageView()
    {
        InitializeComponent();
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigBackupPageViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save config backups to",
            AllowMultiple = false
        }).ConfigureAwait(true);
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            vm.OutputFolder = path;
        }
    }

    private async void OnCheck(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigBackupPageViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Backup to check",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Config backup") { Patterns = ["*.zip"] }]
        }).ConfigureAwait(true);
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            vm.CheckBackup(path);
        }
    }
}
