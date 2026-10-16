using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Reclaim.App.Services;
using Reclaim.Core.Backup;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;
using Reclaim.Core.Models;
using Reclaim.Core.Monitor;
using Reclaim.Core.SystemTools;
using Reclaim.Core.Uninstall;

namespace Reclaim.App.ViewModels;

public enum WizardStep { Prepare, Uninstalling, Scanning, Review, Done }

public enum WizardMode
{
    /// <summary>Run the built-in uninstaller(s), then scan.</summary>
    Normal,
    /// <summary>No uninstaller: scan for leftovers of a broken/removed program.</summary>
    Forced,
    /// <summary>Uninstall using an install trace log plus a normal scan.</summary>
    Traced,
    /// <summary>The program was already removed outside Reclaim; scan only.</summary>
    ScanOnly,
}

public sealed partial class WizardAppStatus : ObservableObject
{
    public required InstalledApp App { get; init; }
    [ObservableProperty] private string _status = "Waiting";
    [ObservableProperty] private string _tone = "Neutral";
    [ObservableProperty] private ImageSource? _icon;
}

/// <summary>Drives the uninstall side sheet: prepare → uninstall → scan → review leftovers → done.</summary>
public sealed partial class UninstallWizardViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;
    private readonly List<InstalledApp> _apps;
    private readonly Dictionary<string, AppFingerprint> _fingerprints = [];
    private readonly TraceLog? _trace;
    private CancellationTokenSource _cts = new();
    private List<LeftoverItem> _leftovers = [];

    public WizardMode Mode { get; }
    public ObservableCollection<WizardAppStatus> AppStatuses { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<LeftoverNodeViewModel> Tree { get; } = [];

    [ObservableProperty] private WizardStep _step = WizardStep.Prepare;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _fullRegistryBackup;
    [ObservableProperty] private bool _quiet;
    [ObservableProperty] private bool _storeAllUsers;
    [ObservableProperty] private ScanMode _scanMode;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectedSize = "";
    [ObservableProperty] private int _certainCount;
    [ObservableProperty] private int _likelyCount;
    [ObservableProperty] private int _possibleCount;
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneMessage = "";
    [ObservableProperty] private string? _backupFolder;
    [ObservableProperty] private bool _undone;
    [ObservableProperty] private ImageSource? _headerIcon;
    [ObservableProperty] private bool _rebootRequired;
    public ObservableCollection<string> Errors { get; } = [];

    public UninstallWizardViewModel(IEnumerable<InstalledApp> apps, WizardMode mode, CatalogService catalog, UiService ui, TraceLog? trace = null)
    {
        _apps = apps.ToList();
        Mode = mode;
        _catalog = catalog;
        _ui = ui;
        _trace = trace;
        var s = SettingsStore.Current;
        CreateRestorePoint = s.CreateRestorePoint && mode is WizardMode.Normal or WizardMode.Traced;
        FullRegistryBackup = s.FullRegistryBackupBeforeStoreUninstall && _apps.Any(a => a.Source == AppSource.Store);
        ScanMode = mode is WizardMode.Forced or WizardMode.ScanOnly ? ScanMode.Advanced : s.DefaultScanMode;
        Quiet = _apps.Count > 1 && CanQuiet;
        foreach (var a in _apps)
        {
            var status = new WizardAppStatus { App = a };
            AppStatuses.Add(status);
            _ = LoadIcon(status);
        }
    }

    private async Task LoadIcon(WizardAppStatus s)
    {
        s.Icon = await IconService.LoadAsync(s.App.DisplayIcon, s.App.MainExecutable ?? PathUtil.ExecutableFromCommand(s.App.UninstallString));
        if (ReferenceEquals(s, AppStatuses[0])) HeaderIcon = s.Icon;
    }

    public string Title => _apps.Count == 1 ? _apps[0].Name : $"{_apps.Count} programs";

    public string Subtitle => Mode switch
    {
        WizardMode.Forced => "Forced uninstall: scan for everything it left behind",
        WizardMode.Traced => "Uninstall with the recorded installation log",
        WizardMode.ScanOnly => "Already uninstalled: clean up what's left",
        _ => _apps.Count == 1 ? $"{_apps[0].Publisher} · {_apps[0].Version}".Trim(' ', '·') : string.Join(", ", _apps.Take(4).Select(a => a.Name)) + (_apps.Count > 4 ? "…" : ""),
    };

    public bool IsBatch => _apps.Count > 1;
    public bool RunsUninstaller => Mode is WizardMode.Normal or WizardMode.Traced;
    public bool CanQuiet => _apps.Any(a => SilentArgs.Build(a) is not null || a.Source is AppSource.Store or AppSource.BrowserExtension);
    public bool HasStoreApps => _apps.Any(a => a.Source == AppSource.Store);
    public bool HasBrowserExtensions => _apps.Any(a => a.Source == AppSource.BrowserExtension && a.Extra.GetValueOrDefault("Type") != "Policy");
    public string StartButtonText => RunsUninstaller ? (IsBatch ? $"Uninstall {_apps.Count} programs" : "Uninstall") : "Scan for leftovers";

    public int StepIndex => (int)Step;
    partial void OnStepChanged(WizardStep value) => OnPropertyChanged(nameof(StepIndex));

    // ------------------------------------------------------------------ flow

    [RelayCommand]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsBusy = true;
        Problem = null;
        try
        {
            if (HasBrowserExtensions && _apps.Where(a => a.Source == AppSource.BrowserExtension).Any(BrowserExtensionRemover.IsBrowserRunning))
            {
                if (!await _ui.ConfirmAsync("Close the browser?", "The browser has to be closed to remove its extensions. Unsaved tabs may be lost.", "Close browser"))
                {
                    IsBusy = false;
                    return;
                }
                foreach (var e in _apps.Where(a => a.Source == AppSource.BrowserExtension)) BrowserExtensionRemover.CloseBrowser(e);
            }

            Step = RunsUninstaller ? WizardStep.Uninstalling : WizardStep.Scanning;

            // Capture what we know about each app before its uninstaller deletes the evidence.
            Report("Analyzing the program before removal…");
            foreach (var app in _apps)
                _fingerprints[app.Id] = await Task.Run(() => AppFingerprint.FromApp(app), ct);
            if (_trace is not null)
            {
                var fp = _fingerprints[_apps[0].Id];
                _fingerprints[_apps[0].Id] = new AppFingerprint
                {
                    DisplayName = fp.DisplayName, NameKey = fp.NameKey, PublisherKey = fp.PublisherKey, InstallDir = fp.InstallDir,
                    InstallDirLeafKey = fp.InstallDirLeafKey, NameTokens = fp.NameTokens, ExeNames = fp.ExeNames, ProductCode = fp.ProductCode,
                    PackedProductCode = fp.PackedProductCode, UninstallKeyPath = fp.UninstallKeyPath, PackageFamilyName = fp.PackageFamilyName,
                    IdentityKeys = fp.IdentityKeys, ExtraPaths = _trace.Directories,
                };
            }

            if (RunsUninstaller)
            {
                if (CreateRestorePoint)
                {
                    Report("Creating a System Restore point…");
                    var (ok, msg) = await BackupManager.CreateRestorePointAsync($"Reclaim: uninstall {Title}");
                    Report(msg);
                }
                if (FullRegistryBackup)
                {
                    Report("Backing up the registry…");
                    await BackupManager.FullRegistryBackupAsync(new Progress<string>(Report), ct);
                }
                foreach (var a in _apps) _catalog.Watcher.Expect(a);

                var anyFailed = await RunUninstallersAsync(ct);
                if (anyFailed && !IsBatch)
                {
                    IsBusy = false;
                    return; // user decides: scan anyway or close (see ScanAnywayCommand)
                }
            }
            await ScanAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.");
            Problem = "Cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("Wizard failed", ex);
            Problem = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> RunUninstallersAsync(CancellationToken ct)
    {
        var log = new Progress<string>(Report);
        if (!IsBatch)
        {
            var status = AppStatuses[0];
            status.Status = Quiet ? "Removing quietly…" : "Running the program's uninstaller…";
            status.Tone = "Accent";
            StatusText = Quiet ? "Removing quietly…" : "Finish the program's own uninstaller. It may open behind this window.";
            var outcome = await new UninstallRunner().RunAsync(new UninstallRequest
            {
                App = _apps[0],
                Quiet = Quiet,
                StoreAllUsers = StoreAllUsers,
                Timeout = TimeSpan.FromMinutes(SettingsStore.Current.UninstallTimeoutMinutes),
            }, log, ct);
            Apply(status, outcome);
            if (outcome.IsSuccess) return false;
            Problem = outcome.Message + " You can still scan for leftovers, which is useful if the program is broken.";
            return true;
        }

        var queue = new BatchUninstallQueue(_apps, Quiet);
        queue.ItemChanged += item => Application.Current.Dispatcher.Invoke(() =>
        {
            var status = AppStatuses.First(s => s.App.Id == item.App.Id);
            if (item.State == BatchItemState.Running)
            {
                status.Status = "Uninstalling…";
                status.Tone = "Accent";
                StatusText = $"Uninstalling {item.App.Name}…";
            }
            else if (item.Outcome is not null) Apply(status, item.Outcome);
            else status.Status = item.State.ToString();
        });
        StatusText = Quiet ? "Removing programs quietly…" : "Uninstalling one program at a time…";
        await queue.RunAsync(log, ct);
        return queue.Items.Any(i => i.State == BatchItemState.Failed);
    }

    private void Apply(WizardAppStatus status, UninstallOutcome outcome)
    {
        status.Status = outcome.Status switch
        {
            UninstallStatus.Succeeded => "Uninstalled",
            UninstallStatus.RebootRequired => "Uninstalled (restart needed)",
            UninstallStatus.NothingToRun => "No uninstaller: leftovers only",
            UninstallStatus.Cancelled => "Cancelled",
            UninstallStatus.StillInstalled => "Still installed",
            UninstallStatus.TimedOut => "Timed out",
            _ => "Failed",
        };
        status.Tone = outcome.IsSuccess ? "Success" : outcome.Status is UninstallStatus.Cancelled or UninstallStatus.StillInstalled ? "Caution" : "Critical";
        if (outcome.Status == UninstallStatus.RebootRequired) RebootRequired = true;
        Report(outcome.Message);
    }

    [RelayCommand]
    private async Task ScanAnywayAsync()
    {
        Problem = null;
        IsBusy = true;
        try { await ScanAsync(_cts.Token); }
        catch (OperationCanceledException) { Problem = "Cancelled."; }
        finally { IsBusy = false; }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        Step = WizardStep.Scanning;
        var all = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);
        var others = _catalog.Models.Where(m => _apps.All(a => a.Id != m.Id)).ToList();
        var guard = new ProtectionGuard(others);
        foreach (var app in _apps)
        {
            if (app.Source is AppSource.WindowsUpdate) continue;
            if (app.Source == AppSource.BrowserExtension) continue; // the remover already cleaned its data folders
            var scanner = new LeftoverScanner(_fingerprints.GetValueOrDefault(app.Id) ?? AppFingerprint.FromApp(app), guard);
            var progress = new Progress<string>(s => StatusText = IsBatch ? $"{app.Name}: {s}" : s);
            foreach (var item in await scanner.ScanAsync(ScanMode, progress, ct))
            {
                if (IsBatch && !item.Category.StartsWith(app.Name)) item.Reasons.Insert(0, app.Name);
                all.TryAdd(item.Key, item);
            }
        }
        if (_trace is not null)
        {
            foreach (var item in TraceStore.ToLeftovers(_trace, guard))
            {
                item.Selected = true;
                if (all.TryGetValue(item.Key, out var existing)) existing.Raise(95, "Created during installation");
                else all[item.Key] = item;
            }
        }
        _leftovers = all.Values.ToList();
        BuildTree();
        _catalog.Remove(_apps.Where(a => a.RegistryKeyPath is null ? RunsUninstaller : !RegistryUtil.KeyExists(a.RegistryKeyPath)));

        if (_leftovers.Count == 0)
        {
            DoneTitle = RunsUninstaller ? "All clean" : "Nothing left behind";
            DoneMessage = RunsUninstaller ? $"{Title} was removed and no leftovers were found." : "No leftovers were found for this program.";
            Step = WizardStep.Done;
            return;
        }
        Step = WizardStep.Review;
    }

    private void BuildTree()
    {
        Tree.Clear();
        foreach (var bucket in new[] { ConfidenceBucket.Certain, ConfidenceBucket.Likely, ConfidenceBucket.Possible })
        {
            var items = _leftovers.Where(i => i.Bucket == bucket).ToList();
            if (items.Count == 0) continue;
            var root = new LeftoverNodeViewModel(bucket switch
            {
                ConfidenceBucket.Certain => "Certain: safe to remove",
                ConfidenceBucket.Likely => "Likely: review recommended",
                _ => "Possible: check before removing",
            }) { Tone = bucket.ToString(), IsExpanded = bucket != ConfidenceBucket.Possible };
            foreach (var group in items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                var cat = new LeftoverNodeViewModel(group.Key) { IsExpanded = bucket == ConfidenceBucket.Certain && items.Count < 40 };
                foreach (var item in group.OrderByDescending(i => i.Confidence).ThenBy(i => i.Path))
                    cat.Add(new LeftoverNodeViewModel(item.Display, item));
                root.Add(cat);
            }
            root.SyncFromChildren();
            root.BubbleTo(UpdateTotals);
            Tree.Add(root);
        }
        CertainCount = _leftovers.Count(i => i.Bucket == ConfidenceBucket.Certain);
        LikelyCount = _leftovers.Count(i => i.Bucket == ConfidenceBucket.Likely);
        PossibleCount = _leftovers.Count(i => i.Bucket == ConfidenceBucket.Possible);
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var sel = _leftovers.Where(i => i.Selected).ToList();
        SelectedCount = sel.Count;
        SelectedSize = Format.Bytes(sel.Sum(i => i.SizeBytes));
    }

    [RelayCommand]
    private void Select(string which)
    {
        foreach (var root in Tree)
        {
            var on = which switch
            {
                "all" => true,
                "none" => false,
                "certain" => root.Tone == nameof(ConfidenceBucket.Certain),
                "likely" => root.Tone is nameof(ConfidenceBucket.Certain) or nameof(ConfidenceBucket.Likely),
                _ => false,
            };
            root.IsChecked = on;
        }
        UpdateTotals();
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var selected = _leftovers.Where(i => i.Selected).ToList();
        if (selected.Count == 0) return;
        if (selected.Any(i => i.Bucket == ConfidenceBucket.Possible)
            && !await _ui.ConfirmAsync("Remove uncertain items?",
                "Some selected items are only possible matches. They could belong to another program or contain your own files. Everything is backed up so you can undo this.", "Remove"))
            return;

        IsBusy = true;
        Step = WizardStep.Scanning;
        try
        {
            var result = await LeftoverCleaner.CleanAsync(selected, $"Uninstall {Title}", new Progress<string>(s => StatusText = s), _cts.Token);
            BackupFolder = result.BackupFolder;
            foreach (var e in result.Errors) Errors.Add(e);
            DoneTitle = result.Failed == 0 ? "All clean" : "Mostly clean";
            DoneMessage = $"Removed {result.Removed} leftover{(result.Removed == 1 ? "" : "s")}{(result.BytesFreed > 0 ? $" and freed {Format.Bytes(result.BytesFreed)}" : "")}."
                          + (result.Failed > 0 ? $" {result.Failed} item(s) were in use and will be removed after a restart." : "");
            Step = WizardStep.Done;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SkipCleanup()
    {
        DoneTitle = "Done";
        DoneMessage = "Leftovers were kept. You can scan again any time from Force uninstall.";
        Step = WizardStep.Done;
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (BackupFolder is null) return;
        var session = BackupSession.Open(BackupFolder);
        if (session is null) return;
        IsBusy = true;
        var (restored, failed) = await Task.Run(() => session.Restore());
        IsBusy = false;
        Undone = true;
        DoneMessage = $"Restored {restored} item(s){(failed > 0 ? $", {failed} could not be restored" : "")}.";
    }

    [RelayCommand]
    private void ExportLeftovers()
    {
        var dlg = new SaveFileDialog { Filter = "Web page|*.html", FileName = $"Leftovers - {Title}.html" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, ExportService.ExportLeftovers(Title, _leftovers));
        ProcessRunner.OpenShell(dlg.FileName);
    }

    [RelayCommand]
    private static void OpenItem(LeftoverNodeViewModel? node)
    {
        if (node?.Item is not { } i) return;
        switch (i.Kind)
        {
            case LeftoverKind.Directory or LeftoverKind.File: ProcessRunner.RevealInExplorer(i.Path); break;
            case LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.EnvironmentPath: RegistryUtil.OpenInRegedit(i.Path); break;
            case LeftoverKind.Service: ProcessRunner.OpenShell("services.msc"); break;
            case LeftoverKind.ScheduledTask: ProcessRunner.OpenShell("taskschd.msc"); break;
            case LeftoverKind.FirewallRule: ProcessRunner.OpenShell("wf.msc"); break;
        }
    }

    [RelayCommand]
    private void ExcludeItem(LeftoverNodeViewModel? node)
    {
        if (node?.Item is not { } i) return;
        SettingsStore.Current.Exclusions.Add(new ExclusionRule { Pattern = i.Path, IncludeChildren = true });
        SettingsStore.Save();
        node.IsChecked = false;
        _ui.Toast("Excluded", $"{i.Path} will never be offered for removal again.");
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            _cts.Cancel();
            return;
        }
        Close();
    }

    [RelayCommand]
    private void Close()
    {
        _ui.HideSheet();
        if (Step is WizardStep.Done or WizardStep.Review) _ = _catalog.LoadAsync();
    }

    [RelayCommand]
    private static void Restart() => ProcessRunner.OpenShell("shutdown.exe", "/r /t 5");

    private void Report(string line) => Application.Current.Dispatcher.Invoke(() =>
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        StatusText = line;
    });
}
