using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Reclaim.App.ViewModels;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Models;
using Reclaim.Core.Monitor;

namespace Reclaim.App.Services;

/// <summary>Holds the shared list of installed apps for every page, loads it, enriches it and watches for outside uninstalls.</summary>
public sealed partial class CatalogService : ObservableObject, IDisposable
{
    private readonly AppCatalog _catalog = new();
    private readonly UninstallWatcher _watcher = new();
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isEnriching;
    [ObservableProperty] private DateTime? _lastLoaded;

    public event EventHandler? Loaded;
    public event Action<InstalledApp>? ExternalUninstall;

    public UninstallWatcher Watcher => _watcher;

    public CatalogService()
    {
        _watcher.ExternalUninstallDetected += app => Application.Current?.Dispatcher.BeginInvoke(() => ExternalUninstall?.Invoke(app));
        if (SettingsStore.Current.WatchExternalUninstalls) _watcher.Start();
    }

    public async Task LoadAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var apps = await _catalog.LoadAsync(CatalogOptions.FromSettings(SettingsStore.Current), cts.Token);
            if (cts.IsCancellationRequested) return;
            var checkedIds = Apps.Where(a => a.IsChecked).Select(a => a.Model.Id).ToHashSet();
            Apps.Clear();
            foreach (var a in apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                Apps.Add(new AppItemViewModel(a) { IsChecked = checkedIds.Contains(a.Id) });
            _watcher.UpdateKnown(apps);
            LastLoaded = DateTime.Now;
            Loaded?.Invoke(this, EventArgs.Empty);
            _ = EnrichAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Loading apps failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    /// <summary>Sizes and signatures in the background, refreshing rows as they complete.</summary>
    private async Task EnrichAsync(CancellationToken ct)
    {
        IsEnriching = true;
        try
        {
            var items = Apps.ToList();
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (item, token) =>
            {
                AppEnricher.Enrich(item.Model, token);
                await Application.Current.Dispatcher.InvokeAsync(item.Refresh);
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsEnriching = false;
        }
    }

    public IEnumerable<InstalledApp> Models => Apps.Select(a => a.Model);

    public void Remove(IEnumerable<InstalledApp> removed)
    {
        var ids = removed.Select(r => r.Id).ToHashSet();
        foreach (var vm in Apps.Where(a => ids.Contains(a.Model.Id)).ToList()) Apps.Remove(vm);
    }

    public void Dispose() => _watcher.Dispose();
}
