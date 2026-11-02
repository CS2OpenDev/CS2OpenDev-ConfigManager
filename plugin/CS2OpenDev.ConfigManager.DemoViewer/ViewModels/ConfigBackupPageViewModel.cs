using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2OpenDev.ConfigManager.Archive;
using CS2OpenDev.ConfigManager.Kit;

namespace CS2OpenDev.ConfigManager.DemoViewer.ViewModels;

/// <summary>One part of the backup the user can leave out.</summary>
public sealed partial class SectionChoice : ObservableObject
{
    private readonly Action<SectionChoice> _changed;

    [ObservableProperty]
    private bool _isChecked;

    internal SectionChoice(string id, bool isChecked, Action<SectionChoice> changed)
    {
        Id = id;
        Label = Sections.Label(id);
        _isChecked = isChecked;
        _changed = changed;
    }

    public string Id { get; }

    public string Label { get; }

    public bool IsMachineWide => Sections.IsMachineWide(Id);

    partial void OnIsCheckedChanged(bool value) => _changed(this);
}

/// <summary>A Steam account with CS2 settings on this PC.</summary>
public sealed record AccountChoice(string AccountId, string Label);

/// <summary>
///     The Settings page: what was found on this PC, which parts to save, where the zip goes, and the button.
///     The capture runs as a job in the app's queue; the page shows its outcome per part.
/// </summary>
public sealed partial class ConfigBackupPageViewModel : ObservableObject
{
    internal const string OutputFolderKey = "output.folder";
    internal const string GameStateIntegrationKey = "cs2game.gsi";
    internal const string SectionKeyPrefix = "section.";

    private readonly IExtensionContext _context;

    [ObservableProperty]
    private string _detected = "Looking for Steam, CS2 and an NVIDIA driver...";

    [ObservableProperty]
    private AccountChoice? _selectedAccount;

    [ObservableProperty]
    private string _outputFolder;

    [ObservableProperty]
    private bool _includeGameStateIntegration;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBackupCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealArchiveCommand))]
    private string? _lastArchive;

    public ConfigBackupPageViewModel(IExtensionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _outputFolder = context.Settings.Get(OutputFolderKey, DefaultOutputFolder());
        _includeGameStateIntegration = context.Settings.Get(GameStateIntegrationKey, false);
        foreach (string id in Sections.All)
        {
            Choices.Add(new SectionChoice(id, context.Settings.Get(SectionKeyPrefix + id, true),
                choice => _context.Settings.Set(SectionKeyPrefix + choice.Id, choice.IsChecked)));
        }

        if (IsAvailable)
        {
            _ = Task.Run(Detect);
        }
        else
        {
            Detected = "Backups are made by the desktop app.";
        }
    }

    /// <summary>The browser build has no Steam, registry or driver to read.</summary>
    public static bool IsAvailable => !OperatingSystem.IsBrowser();

    public ObservableCollection<SectionChoice> Choices { get; } = [];

    public ObservableCollection<AccountChoice> Accounts { get; } = [];

    public ObservableCollection<string> Results { get; } = [];

    public bool HasSeveralAccounts => Accounts.Count > 1;

    public static string DefaultOutputFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Advanced Config Backup");

    partial void OnOutputFolderChanged(string value) => _context.Settings.Set(OutputFolderKey, value);

    partial void OnIncludeGameStateIntegrationChanged(bool value) => _context.Settings.Set(GameStateIntegrationKey, value);

    private void Detect()
    {
        List<string> lines = [];
        List<AccountChoice> accounts = [];
        try
        {
            SteamInstall? steam = SteamInstall.Locate(null);
            if (steam is null)
            {
                lines.Add("Steam: not found");
            }
            else
            {
                lines.Add($"Steam: {steam.Root}");
                lines.Add($"CS2: {steam.Cs2InstallDir() ?? "not installed"}");
                foreach (SteamAccount account in steam.Accounts())
                {
                    string when = account.LastWriteUtc == DateTime.MinValue
                        ? ""
                        : ", settings saved " + account.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
                    accounts.Add(new AccountChoice(account.AccountId,
                        $"{account.AccountId}{(account.MostRecent ? " (signed in last)" : "")}{when}"));
                }

                if (accounts.Count == 0)
                {
                    lines.Add("No Steam account on this PC has CS2 settings yet.");
                }
            }

            if (OperatingSystem.IsWindows())
            {
                // Looked for on disk only: the driver library is loaded when a backup is saved, never by opening Settings.
                lines.Add(File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"))
                    ? "NVIDIA driver: found"
                    : "NVIDIA: no NVIDIA driver is installed. GPU settings are saved for NVIDIA only; AMD and Intel settings are not carried.");
            }
            else
            {
                lines.Add("Windows and NVIDIA settings are saved only on Windows.");
            }
        }
#pragma warning disable CA1031 // Detection is informational; any failure is shown on the page instead.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            lines.Add("Could not look: " + ex.Message);
        }

        _context.Post(() =>
        {
            Accounts.Clear();
            foreach (AccountChoice account in accounts)
            {
                Accounts.Add(account);
            }

            SelectedAccount = Accounts.FirstOrDefault();
            OnPropertyChanged(nameof(HasSeveralAccounts));
            Detected = string.Join(Environment.NewLine, lines);
        });
    }

    private bool CanCreateBackup() => IsAvailable && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreateBackup))]
    private void CreateBackup()
    {
        CaptureOptions options = new()
        {
            AccountId = SelectedAccount?.AccountId,
            IncludeGameStateIntegration = IncludeGameStateIntegration,
            Creator = "Advanced Config Backup (DemoViewer.NET plugin) " + ExtensionVersion()
        };
        options.Include.Clear();
        options.Include.AddRange(Choices.Where(c => c.IsChecked).Select(c => c.Id));
        if (options.Include.Count == 0)
        {
            Status = "Pick at least one thing to save.";
            return;
        }

        string folder = string.IsNullOrWhiteSpace(OutputFolder) ? DefaultOutputFolder() : OutputFolder;
        ArchiveTool tool = new("Advanced Config Backup (DemoViewer.NET plugin)", ExtensionVersion());
        string zip = Path.Combine(folder, BackupBuilder.DefaultFileName(DateTime.Now));
        IsBusy = true;
        Status = "Saving...";
        Results.Clear();
        RunReport? report = null;
        Exception? failure = null;
        IJobHandle handle = _context.Jobs.Enqueue(new JobRequest("Advanced Config Backup: save settings", job =>
        {
            try
            {
                report = BackupBuilder.Create(options, zip, tool, DateTimeOffset.Now, job.CancellationToken);
            }
#pragma warning disable CA1031 // Reported on the page and in a notification rather than as a faulted job.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }

            return Task.CompletedTask;
        }, new JobOptions(BuiltInJobKinds.Save, JobPriority.UserRequested)));

        handle.Completed += _ => Finish(zip, report, failure);
    }

    private void Finish(string zip, RunReport? report, Exception? failure)
    {
        IsBusy = false;
        if (report is null)
        {
            Status = failure is null or OperationCanceledException ? "The backup was cancelled." : "The backup failed: " + failure.Message;
            _context.Notifications.Post(new Notification("configbackup.saved", NotificationSeverity.Warning,
                "Config backup not saved", Status));
            return;
        }

        foreach (SectionResult result in report.Results)
        {
            Results.Add(result.ToString());
        }

        if (!BackupBuilder.HoldsAnything(report))
        {
            Status = "Nothing was saved: no part could be captured, so no zip was written. The reasons are below.";
            _context.Notifications.Post(new Notification("configbackup.saved", NotificationSeverity.Warning,
                "Config backup not saved", Status));
            return;
        }

        LastArchive = zip;
        int saved = report.Count("done");
        List<string> missing = [.. report.Results.Where(r => r.Outcome != "done").Select(r => Sections.Label(r.Section))];
        string notIn = missing.Count == 0 ? "" : $" Not in it: {string.Join(", ", missing)}.";
        Status = $"Saved {saved} of {report.Results.Count} parts to {zip}.{notIn}";
        _context.Notifications.Post(new Notification("configbackup.saved",
            missing.Count == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning,
            missing.Count == 0 ? "Config backup saved" : "Config backup saved without some parts",
            $"{saved} parts in {Path.GetFileName(zip)}.{notIn} On the other PC, restore it with acb.")
        {
            Action = new NotificationAction("Show", () => _context.Shell.RevealInFileManager(zip)),
            TimeToLive = TimeSpan.FromSeconds(30)
        });
    }

    private static string ExtensionVersion()
    {
        string? informational = typeof(ConfigBackupPack).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return informational?.Split('+')[0] ?? "";
    }

    /// <summary>
    ///     Checks a backup with the restorer's own reader: every file against manifest.json, manifest.json against
    ///     the signature, nothing added or missing. It reads any platform, since checking is not restoring.
    /// </summary>
    public void CheckBackup(string zipPath)
    {
        ArgumentNullException.ThrowIfNull(zipPath);
        Results.Clear();
        VerifiedArchive archive;
        try
        {
            archive = ArchiveReader.Open(zipPath, new ArchiveReadOptions { RequirePlatform = null });
        }
        catch (ArchiveRejectedException ex)
        {
            Status = $"Do not restore {Path.GetFileName(zipPath)}: {ex.Message}";
            return;
        }
#pragma warning disable CA1031 // A file that cannot be read is reported on the page.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = "Could not check " + zipPath + ": " + ex.Message;
            return;
        }

        ArchiveManifest manifest = archive.Manifest;
        Results.Add("Trust level: " + ArchiveReader.Name(archive.Level)
                    + (archive.Level == TrustLevel.Hash ? " (unsigned: it proves the files are unchanged since it was made, not who made it)" : ""));
        Results.Add(string.Create(CultureInfo.CurrentCulture,
            $"Made {manifest.Created.ToLocalTime():yyyy-MM-dd HH:mm} by {manifest.Tool.Name} {manifest.Tool.Version}, for {manifest.Platform}"));
        foreach (ArchiveSection section in manifest.Sections)
        {
            Results.Add(section.Label + (section.Notes is { Count: > 0 } notes ? " (" + string.Join("; ", notes) + ")" : ""));
        }

        Status = $"{Path.GetFileName(zipPath)} is unchanged since it was made: {manifest.Entries.Count} files, each matching its manifest.";
    }

    private bool CanRevealArchive() => LastArchive is not null;

    [RelayCommand(CanExecute = nameof(CanRevealArchive))]
    private void RevealArchive()
    {
        if (LastArchive is { } path)
        {
            _context.Shell.RevealInFileManager(path);
        }
    }
}
