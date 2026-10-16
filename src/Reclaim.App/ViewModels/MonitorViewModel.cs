using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Reclaim.App.Services;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Reclaim.Core.Monitor;

namespace Reclaim.App.ViewModels;

public enum TraceState { Idle, Preparing, Installing, Finishing }

public sealed class TraceItemViewModel(TraceLog log)
{
    public TraceLog Log { get; } = log;
    public string Name => Log.Name;
    public string When => $"{Log.Started:d MMM yyyy, HH:mm}{(Log.Imported ? " · imported" : "")}";
    public string Summary =>
        $"{Log.Directories.Count + Log.Files.Count} files/folders · {Log.RegistryKeys.Count + Log.RegistryValues.Count} registry entries" +
        (Log.Services.Count + Log.ScheduledTasks.Count > 0 ? $" · {Log.Services.Count + Log.ScheduledTasks.Count} services/tasks" : "");
    public string Method => Log.Method;
    public IEnumerable<string> Details =>
        Log.Directories.Select(d => "📁 " + d)
            .Concat(Log.Files.Select(f => "📄 " + f))
            .Concat(Log.RegistryKeys.Select(k => "🔑 " + k))
            .Concat(Log.RegistryValues.Select(v => $"🔹 {v.Key} → {v.Name}"))
            .Concat(Log.Services.Select(s => "⚙ Service: " + s))
            .Concat(Log.ScheduledTasks.Select(t => "🕒 Task: " + t))
            .Concat(Log.FirewallRules.Select(r => "🛡 Firewall: " + r.Split('|')[0]))
            .Take(500);
}

public sealed partial class MonitorViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;
    private InstallTracer? _tracer;

    public ObservableCollection<TraceItemViewModel> Traces { get; } = [];

    [ObservableProperty] private TraceState _state = TraceState.Idle;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _installerName = "";
    [ObservableProperty] private bool _installerFinished;
    [ObservableProperty] private bool _realTime;

    public MonitorViewModel(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        Reload();
    }

    private void Reload()
    {
        Traces.Clear();
        foreach (var t in TraceStore.List()) Traces.Add(new TraceItemViewModel(t));
    }

    [RelayCommand]
    private async Task StartTraceAsync()
    {
        var dlg = new OpenFileDialog { Title = "Choose an installer to trace", Filter = "Installers|*.exe;*.msi|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        InstallerName = Path.GetFileName(dlg.FileName);
        InstallerFinished = false;
        State = TraceState.Preparing;
        _tracer = new InstallTracer();
        _tracer.InstallerExited += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            InstallerFinished = true;
            StatusText = "The installer has closed. Click Finish once the program is set up the way you want.";
        });
        try
        {
            await _tracer.StartAsync(dlg.FileName, null, new Progress<string>(s => StatusText = s), CancellationToken.None);
            RealTime = _tracer.UsingRealTime;
            State = TraceState.Installing;
            StatusText = "Install the program as usual. Reclaim is recording every change it makes.";
        }
        catch (Exception ex)
        {
            _tracer.Dispose();
            _tracer = null;
            State = TraceState.Idle;
            _ui.Error("Couldn't start tracing", ex.Message);
        }
    }

    [RelayCommand]
    private async Task FinishTraceAsync()
    {
        if (_tracer is null) return;
        State = TraceState.Finishing;
        try
        {
            var log = await _tracer.FinishAsync(new Progress<string>(s => StatusText = s), CancellationToken.None);
            Traces.Insert(0, new TraceItemViewModel(log));
            _ui.Success("Installation recorded", $"{log.Name}: {log.TotalChanges} changes saved.");
            await _catalog.LoadAsync();
        }
        catch (Exception ex)
        {
            _ui.Error("Tracing failed", ex.Message);
        }
        finally
        {
            _tracer.Dispose();
            _tracer = null;
            State = TraceState.Idle;
        }
    }

    [RelayCommand]
    private void CancelTrace()
    {
        _tracer?.Dispose();
        _tracer = null;
        State = TraceState.Idle;
    }

    [RelayCommand]
    private void Uninstall(TraceItemViewModel? item)
    {
        if (item is null) return;
        var app = _catalog.Models.FirstOrDefault(a => a.RegistryKeyPath is not null
                      && item.Log.LinkedUninstallKeys.Contains(a.RegistryKeyPath, StringComparer.OrdinalIgnoreCase))
                  ?? new InstalledApp { Id = "trace:" + item.Log.Id, Name = item.Log.Name, Publisher = item.Log.Publisher, Version = item.Log.Version, Source = AppSource.Traced };
        _ui.ShowSheet(new UninstallWizardViewModel([app], WizardMode.Traced, _catalog, _ui, item.Log));
    }

    [RelayCommand]
    private void Export(TraceItemViewModel? item)
    {
        if (item is null) return;
        var dlg = new SaveFileDialog { Filter = "Reclaim trace|*.reclaimtrace.json", FileName = $"{item.Name}.reclaimtrace.json" };
        if (dlg.ShowDialog() != true) return;
        TraceStore.Export(item.Log, dlg.FileName);
        ProcessRunner.RevealInExplorer(dlg.FileName);
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog { Filter = "Reclaim trace|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var log = TraceStore.Import(dlg.FileName);
            Traces.Insert(0, new TraceItemViewModel(log));
            _ui.Success("Trace imported", log.Name);
        }
        catch (Exception ex)
        {
            _ui.Error("Import failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(TraceItemViewModel? item)
    {
        if (item is null) return;
        if (!await _ui.ConfirmAsync("Delete this trace?", $"The recording of {item.Name} will be deleted. The program itself is not affected.", "Delete")) return;
        TraceStore.Delete(item.Log.Id);
        Traces.Remove(item);
    }
}
