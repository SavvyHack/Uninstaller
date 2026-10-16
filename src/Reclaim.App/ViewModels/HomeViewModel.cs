using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reclaim.App.Services;
using Reclaim.App.Views.Pages;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Reclaim.Core.SystemTools;
using Reclaim.Core.Updates;

namespace Reclaim.App.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;
    private readonly AppsViewModel _apps;

    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private int _appCount;
    [ObservableProperty] private string _appSize = "";
    [ObservableProperty] private int _leftoverCount;
    [ObservableProperty] private string _updatesText = "Not checked";
    [ObservableProperty] private bool _checkingUpdates;
    [ObservableProperty] private string _startupText = "…";
    [ObservableProperty] private string _junkText = "…";
    [ObservableProperty] private int _bundlewareCount;

    public ObservableCollection<AppItemViewModel> Recent { get; } = [];
    public ObservableCollection<AppItemViewModel> Largest { get; } = [];
    public CatalogService Catalog => _catalog;

    public HomeViewModel(CatalogService catalog, UiService ui, AppsViewModel apps)
    {
        _catalog = catalog;
        _ui = ui;
        _apps = apps;
        catalog.Loaded += (_, _) => Recompute();
        catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CatalogService.IsEnriching) && !catalog.IsEnriching) Recompute();
        };
        var hour = DateTime.Now.Hour;
        Greeting = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        _ = LoadSideStatsAsync();
    }

    private void Recompute()
    {
        var programs = _catalog.Apps.Where(a => a.Model.Source is not (AppSource.WindowsUpdate or AppSource.Orphan)).ToList();
        AppCount = programs.Count(a => a.Model.Source != AppSource.BrowserExtension);
        AppSize = Format.Bytes(programs.Sum(a => a.SizeBytes));
        LeftoverCount = _catalog.Apps.Count(a => a.Model.Source == AppSource.Orphan || a.Model.Has(AppFlags.Orphaned));
        BundlewareCount = _catalog.Apps.Count(a => a.Model.Has(AppFlags.Bundleware));
        Fill(Recent, programs.Where(a => a.InstallDate is not null).OrderByDescending(a => a.InstallDate).Take(6));
        Fill(Largest, programs.Where(a => a.SizeBytes > 0).OrderByDescending(a => a.SizeBytes).Take(6));
        if (UpdateIndex.Last.Count > 0) UpdatesText = $"{UpdateIndex.Last.Count} available";
    }

    private static void Fill(ObservableCollection<AppItemViewModel> target, IEnumerable<AppItemViewModel> items)
    {
        target.Clear();
        foreach (var i in items)
        {
            i.EnsureIcon();
            target.Add(i);
        }
    }

    private async Task LoadSideStatsAsync()
    {
        var (enabled, total) = await Task.Run(() =>
        {
            var list = AutorunManager.List(includeServices: false);
            return (list.Count(e => e.Enabled), list.Count);
        });
        StartupText = $"{enabled} of {total} enabled";
        var junk = await Task.Run(() => FileOps.DirectorySize(Path.GetTempPath()) + FileOps.DirectorySize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")));
        JunkText = Format.Bytes(junk);
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!SoftwareUpdater.IsAvailable)
        {
            UpdatesText = "winget not installed";
            return;
        }
        CheckingUpdates = true;
        UpdatesText = "Checking…";
        try
        {
            var updates = await SoftwareUpdater.CheckAsync(CancellationToken.None);
            UpdatesText = updates.Count == 0 ? "Everything is up to date" : $"{updates.Count} available";
            UpdateIndex.Apply(_catalog.Models);
            foreach (var a in _catalog.Apps) a.Refresh();
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void ShowApps(string filter)
    {
        _apps.SetFilter(Enum.Parse<AppFilter>(filter));
        _ui.Navigate(typeof(AppsPage));
    }

    [RelayCommand]
    private void Go(string page)
    {
        var type = page switch
        {
            "Hunter" => typeof(HunterPage),
            "Monitor" => typeof(MonitorPage),
            "Cleanup" => typeof(CleanupPage),
            "Startup" => typeof(StartupPage),
            "Force" => typeof(ForcePage),
            "Tools" => typeof(ToolsPage),
            _ => typeof(AppsPage),
        };
        _ui.Navigate(type);
    }

    [RelayCommand]
    private void Uninstall(AppItemViewModel? item)
    {
        if (item is null) return;
        _apps.UninstallCommand.Execute(item);
    }
}
