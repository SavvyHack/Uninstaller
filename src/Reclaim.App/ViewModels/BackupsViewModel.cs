using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reclaim.App.Services;
using Reclaim.Core.Backup;
using Reclaim.Core.Infrastructure;

namespace Reclaim.App.ViewModels;

public sealed partial class BackupsViewModel(UiService ui, CatalogService catalog) : ObservableObject
{
    public ObservableCollection<BackupSummary> Sessions { get; } = [];
    public ObservableCollection<RestorePointInfo> RestorePoints { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _totalSize = "";

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var (sessions, points) = await Task.Run(() => (BackupManager.List(), BackupManager.ListRestorePoints()));
            Sessions.Clear();
            foreach (var s in sessions) Sessions.Add(s);
            RestorePoints.Clear();
            foreach (var p in points) RestorePoints.Add(p);
            TotalSize = Format.Bytes(sessions.Sum(s => s.SizeBytes));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupSummary? s)
    {
        if (s is null) return;
        if (!await ui.ConfirmAsync("Restore this backup?", $"Everything removed during \"{s.Title}\" will be put back where it was.", "Restore")) return;
        var session = BackupSession.Open(s.Folder);
        if (session is null)
        {
            ui.Error("Can't restore", "This backup has no restore information. Full registry backups can be imported by double-clicking the .reg files.");
            ProcessRunner.RevealInExplorer(s.Folder);
            return;
        }
        IsBusy = true;
        var (ok, failed) = await Task.Run(() => session.Restore(new Progress<string>(m => Status = m)));
        IsBusy = false;
        Status = "";
        ui.Success("Restored", $"{ok} item(s) restored{(failed > 0 ? $", {failed} couldn't be" : "")}.");
        await LoadAsync();
        await catalog.LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupSummary? s)
    {
        if (s is null) return;
        if (!await ui.ConfirmAsync("Delete backup?", $"\"{s.Title}\" ({Format.Bytes(s.SizeBytes)}) can no longer be undone afterwards.", "Delete")) return;
        BackupManager.Delete(s.Folder);
        Sessions.Remove(s);
    }

    [RelayCommand]
    private static void Open(BackupSummary? s)
    {
        if (s is not null) ProcessRunner.RevealInExplorer(s.Folder);
    }

    [RelayCommand]
    private async Task CreateRestorePointAsync()
    {
        IsBusy = true;
        Status = "Creating a restore point…";
        var (ok, msg) = await BackupManager.CreateRestorePointAsync("Reclaim manual restore point");
        IsBusy = false;
        Status = "";
        if (ok) ui.Success("Restore point created", "Windows may skip it if one was made in the last 24 hours.");
        else ui.Error("No restore point", msg);
        await LoadAsync();
    }

    [RelayCommand]
    private static void OpenSystemRestore() => BackupManager.OpenSystemRestore();

    [RelayCommand]
    private async Task FullRegistryBackupAsync()
    {
        IsBusy = true;
        try
        {
            var (ok, folder) = await BackupManager.FullRegistryBackupAsync(new Progress<string>(s => Status = s), CancellationToken.None);
            if (ok) ui.Success("Registry backed up", folder);
            else ui.Error("Registry backup incomplete", "Some hives couldn't be exported.");
        }
        finally
        {
            IsBusy = false;
            Status = "";
        }
        await LoadAsync();
    }
}
