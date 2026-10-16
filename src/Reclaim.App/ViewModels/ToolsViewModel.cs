using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reclaim.App.Services;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Reclaim.Core.SystemTools;
using Reclaim.Core.Updates;

namespace Reclaim.App.ViewModels;

public sealed partial class UpdateItemViewModel(AvailableUpdate update) : ObservableObject
{
    public AvailableUpdate Update { get; } = update;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _busy;
}

public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;

    /// <summary>Set by the Apps page when the user picks "Move to another drive".</summary>
    public static InstalledApp? PendingRelocation { get; set; }

    [ObservableProperty] private string _section = "Relocator";

    // Relocator
    public ObservableCollection<InstalledApp> MovableApps { get; } = [];
    public ObservableCollection<string> TargetDrives { get; } = [];
    public ObservableCollection<RelocationRecord> Relocations { get; } = [];
    [ObservableProperty] private InstalledApp? _relocateApp;
    [ObservableProperty] private string? _targetDrive;
    [ObservableProperty] private string _relocateStatus = "";
    [ObservableProperty] private bool _relocating;

    // Updater
    public ObservableCollection<UpdateItemViewModel> Updates { get; } = [];
    [ObservableProperty] private bool _checking;
    [ObservableProperty] private string _updateStatus = "Check which of your programs have newer versions.";

    public IReadOnlyList<WindowsTool> WindowsTools => WindowsToolsCatalog.All;

    public ToolsViewModel(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        catalog.Loaded += (_, _) => LoadMovable();
        LoadMovable();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            TargetDrives.Add(d.RootDirectory.FullName);
        TargetDrive = TargetDrives.FirstOrDefault(d => !d.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase));
        ReloadRelocations();
    }

    public void OnShown()
    {
        if (PendingRelocation is null) return;
        Section = "Relocator";
        RelocateApp = MovableApps.FirstOrDefault(a => a.Id == PendingRelocation.Id) ?? PendingRelocation;
        PendingRelocation = null;
    }

    private void LoadMovable()
    {
        var current = RelocateApp?.Id;
        MovableApps.Clear();
        foreach (var a in _catalog.Models.Where(a => SoftwareRelocator.CanRelocate(a) is null).OrderBy(a => a.Name)) MovableApps.Add(a);
        if (current is not null) RelocateApp = MovableApps.FirstOrDefault(a => a.Id == current);
    }

    private void ReloadRelocations()
    {
        Relocations.Clear();
        foreach (var r in SoftwareRelocator.List()) Relocations.Add(r);
    }

    [RelayCommand]
    private async Task MoveAsync()
    {
        if (RelocateApp is null || TargetDrive is null) return;
        var target = Path.Combine(TargetDrive, "Program Files (Moved)");
        if (!await _ui.ConfirmAsync($"Move {RelocateApp.Name}?",
                $"Its files move to {target}. A link is left at the old location so shortcuts, settings and the uninstaller keep working. You can move it back at any time.", "Move"))
            return;
        Relocating = true;
        try
        {
            Directory.CreateDirectory(target);
            await SoftwareRelocator.MoveAsync(RelocateApp, target, new Progress<string>(s => RelocateStatus = s), CancellationToken.None);
            _ui.Success("Moved", $"{RelocateApp.Name} now lives on {TargetDrive}.");
            RelocateStatus = "";
            ReloadRelocations();
            await _catalog.LoadAsync();
        }
        catch (Exception ex)
        {
            RelocateStatus = ex.Message;
            _ui.Error("Couldn't move it", ex.Message);
        }
        finally
        {
            Relocating = false;
        }
    }

    [RelayCommand]
    private async Task UndoMoveAsync(RelocationRecord? r)
    {
        if (r is null) return;
        if (!await _ui.ConfirmAsync($"Move {r.AppName} back?", $"Files return to {r.OriginalPath}.", "Move back")) return;
        Relocating = true;
        try
        {
            await SoftwareRelocator.UndoAsync(r, new Progress<string>(s => RelocateStatus = s), CancellationToken.None);
            _ui.Success("Moved back", r.AppName);
            ReloadRelocations();
        }
        catch (Exception ex)
        {
            _ui.Error("Couldn't move it back", ex.Message);
        }
        finally
        {
            Relocating = false;
            RelocateStatus = "";
        }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!SoftwareUpdater.IsAvailable)
        {
            UpdateStatus = "The Windows Package Manager (winget) isn't installed. Get \"App Installer\" from the Microsoft Store.";
            return;
        }
        Checking = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            var list = await SoftwareUpdater.CheckAsync(CancellationToken.None);
            Updates.Clear();
            foreach (var u in list) Updates.Add(new UpdateItemViewModel(u));
            UpdateStatus = list.Count == 0 ? "Everything is up to date." : $"{list.Count} update(s) available";
            UpdateIndex.Apply(_catalog.Models);
            foreach (var a in _catalog.Apps) a.Refresh();
        }
        finally
        {
            Checking = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAsync(UpdateItemViewModel? item)
    {
        if (item is null || item.Busy) return;
        item.Busy = true;
        item.State = "Updating…";
        var r = await SoftwareUpdater.UpgradeAsync(item.Update.Id, CancellationToken.None);
        item.Busy = false;
        item.State = r.ExitCode == 0 ? "Updated" : "Failed";
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        foreach (var u in Updates.Where(u => u.State != "Updated").ToList()) await UpdateAsync(u);
    }

    [RelayCommand]
    private static void Launch(WindowsTool? tool) => tool?.Launch();
}
