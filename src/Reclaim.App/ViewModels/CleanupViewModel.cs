using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Reclaim.App.Services;
using Reclaim.Core.Cleaners;
using Reclaim.Core.Infrastructure;

namespace Reclaim.App.ViewModels;

public sealed partial class CleanerItemViewModel(CleanerItem item) : ObservableObject
{
    public CleanerItem Item { get; } = item;
    public string Group => Item.Group;
    public string Name => Item.Name;
    public string? Description => Item.Description;
    public string SizeText => Item.SizeText;

    public bool Selected
    {
        get => Item.Selected;
        set
        {
            if (Item.Selected == value) return;
            Item.Selected = value;
            OnPropertyChanged();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Selected));
    }
}

public sealed partial class DriveOption : ObservableObject
{
    public required string Root { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _selected;
}

public sealed partial class JunkFileViewModel(JunkFile file) : ObservableObject
{
    public JunkFile File { get; } = file;
    public string Path => File.Path;
    public string SizeText => File.SizeText;
    public string Pattern => File.Pattern;

    public bool Selected
    {
        get => File.Selected;
        set
        {
            File.Selected = value;
            OnPropertyChanged();
        }
    }
}

public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly UiService _ui;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _section = "Junk";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    // Trace cleaners
    public ObservableCollection<CleanerItemViewModel> WindowsItems { get; } = [];
    public ObservableCollection<CleanerItemViewModel> BrowserItems { get; } = [];
    public ObservableCollection<CleanerItemViewModel> OfficeItems { get; } = [];
    [ObservableProperty] private string _windowsTotal = "";
    [ObservableProperty] private string _browserTotal = "";
    [ObservableProperty] private string _officeTotal = "";

    // Junk
    public ObservableCollection<DriveOption> Drives { get; } = [];
    public ObservableCollection<JunkFileViewModel> JunkFiles { get; } = [];
    [ObservableProperty] private string _junkSummary = "Choose drives and scan for leftover temporary files.";
    [ObservableProperty] private bool _junkToRecycleBin = true;

    // Evidence remover
    [ObservableProperty] private DriveOption? _evidenceDrive;
    [ObservableProperty] private double _evidencePasses = 1;
    [ObservableProperty] private double _evidenceProgress;
    [ObservableProperty] private bool _evidenceIsSsd;

    // Shredder
    public ObservableCollection<string> ShredList { get; } = [];
    [ObservableProperty] private WipeMethod _wipeMethod = WipeMethod.DoD3;
    public IReadOnlyList<WipeMethod> WipeMethods { get; } = [WipeMethod.Quick, WipeMethod.DoD3, WipeMethod.Thorough7];

    public CleanupViewModel(UiService ui)
    {
        _ui = ui;
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            Drives.Add(new DriveOption
            {
                Root = d.RootDirectory.FullName,
                Label = $"{d.RootDirectory.FullName.TrimEnd('\\')}  {d.VolumeLabel}  ({Format.Bytes(d.AvailableFreeSpace)} free)",
                Selected = d.RootDirectory.FullName.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, StringComparison.OrdinalIgnoreCase),
            });
        EvidenceDrive = Drives.FirstOrDefault();
        JunkToRecycleBin = SettingsStore.Current.DeleteMode != DeleteMode.Permanent;
        Load(WindowsItems, CleanerCatalog.Windows());
        Load(BrowserItems, CleanerCatalog.Browsers());
        Load(OfficeItems, CleanerCatalog.Office());
    }

    partial void OnEvidenceDriveChanged(DriveOption? value)
    {
        if (value is null) return;
        Task.Run(() => SecureErase.IsSolidState(value.Root)).ContinueWith(t => EvidenceIsSsd = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static void Load(ObservableCollection<CleanerItemViewModel> target, IEnumerable<CleanerItem> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(new CleanerItemViewModel(i));
    }

    private ObservableCollection<CleanerItemViewModel> ItemsFor(string section) => section switch
    {
        "Browsers" => BrowserItems,
        "Office" => OfficeItems,
        _ => WindowsItems,
    };

    private void SetTotal(string section)
    {
        var total = Format.Bytes(ItemsFor(section).Where(i => i.Selected).Sum(i => i.Item.SizeBytes));
        switch (section)
        {
            case "Browsers": BrowserTotal = total; break;
            case "Office": OfficeTotal = total; break;
            default: WindowsTotal = total; break;
        }
    }

    [RelayCommand]
    private async Task AnalyzeAsync(string section)
    {
        IsBusy = true;
        Status = "Analyzing…";
        try
        {
            var items = ItemsFor(section);
            await TraceCleaner.AnalyzeAsync(items.Select(i => i.Item), CancellationToken.None);
            foreach (var i in items) i.Refresh();
            SetTotal(section);
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanAsync(string section)
    {
        var items = ItemsFor(section).Where(i => i.Selected).ToList();
        if (items.Count == 0) return;
        if (!await _ui.ConfirmAsync("Clean now?", $"{items.Count} selected item(s) will be cleaned. History and caches can't be restored afterwards.", "Clean")) return;
        IsBusy = true;
        try
        {
            var r = await TraceCleaner.CleanAsync(items.Select(i => i.Item), new Progress<string>(s => Status = s), CancellationToken.None);
            _ui.Success("Cleaned", $"{r.ItemsCleaned} item(s) cleaned, {Format.Bytes(r.BytesFreed)} freed." + (r.Skipped.Count > 0 ? " " + string.Join(" ", r.Skipped) : ""));
            Status = "";
        }
        finally
        {
            IsBusy = false;
        }
        await AnalyzeAsync(section);
    }

    // ------------------------------------------------------------------ junk

    [RelayCommand]
    private async Task ScanJunkAsync()
    {
        var drives = Drives.Where(d => d.Selected).Select(d => d.Root).ToList();
        if (drives.Count == 0) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        JunkFiles.Clear();
        try
        {
            var found = await JunkFilesCleaner.ScanAsync(drives, new Progress<string>(s => Status = s), _cts.Token);
            foreach (var f in found.OrderByDescending(f => f.SizeBytes)) JunkFiles.Add(new JunkFileViewModel(f));
            JunkSummary = found.Count == 0 ? "No junk files found." : $"{found.Count} junk files · {Format.Bytes(found.Sum(f => f.SizeBytes))}";
        }
        catch (OperationCanceledException)
        {
            JunkSummary = "Scan cancelled.";
        }
        finally
        {
            IsBusy = false;
            Status = "";
        }
    }

    [RelayCommand]
    private async Task DeleteJunkAsync()
    {
        var selected = JunkFiles.Where(j => j.Selected).Select(j => j.File).ToList();
        if (selected.Count == 0) return;
        IsBusy = true;
        try
        {
            var (deleted, freed) = await JunkFilesCleaner.DeleteAsync(selected, JunkToRecycleBin, CancellationToken.None);
            _ui.Success("Junk removed", $"{deleted} files deleted, {Format.Bytes(freed)} freed{(JunkToRecycleBin ? " (in the Recycle Bin)" : "")}.");
            foreach (var j in JunkFiles.Where(j => j.Selected && !File.Exists(j.Path)).ToList()) JunkFiles.Remove(j);
            JunkSummary = $"{JunkFiles.Count} junk files left";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SelectAllJunk(string on)
    {
        foreach (var j in JunkFiles) j.Selected = on == "true";
    }

    [RelayCommand]
    private void CancelWork() => _cts?.Cancel();

    // ------------------------------------------------------------------ evidence remover

    [RelayCommand]
    private async Task WipeFreeSpaceAsync()
    {
        if (EvidenceDrive is null) return;
        var msg = $"Reclaim will fill all free space on {EvidenceDrive.Root} with random data, then delete it. This can take a long time and the drive will look full while it runs."
                  + (EvidenceIsSsd ? "\n\nThis is a solid-state drive. Because of wear-levelling, overwriting free space can't guarantee old data is gone. Windows' built-in TRIM usually handles this already." : "");
        if (!await _ui.ConfirmAsync("Erase deleted files for good?", msg, "Start")) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            await SecureErase.WipeFreeSpaceAsync(EvidenceDrive.Root, (int)Math.Clamp(EvidencePasses, 1, 7), new Progress<(string Message, double Percent)>(p =>
            {
                Status = p.Message;
                EvidenceProgress = p.Percent;
            }), _cts.Token);
            _ui.Success("Free space wiped", $"Deleted files on {EvidenceDrive.Root} can no longer be recovered.");
        }
        catch (OperationCanceledException)
        {
            _ui.Toast("Stopped", "Free-space wipe cancelled. The temporary file was removed.");
        }
        finally
        {
            IsBusy = false;
            Status = "";
            EvidenceProgress = 0;
        }
    }

    // ------------------------------------------------------------------ shredder

    [RelayCommand]
    private void AddShredFiles()
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Files to erase permanently" };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames.Where(f => !ShredList.Contains(f))) ShredList.Add(f);
    }

    [RelayCommand]
    private void AddShredFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Folder to erase permanently" };
        if (dlg.ShowDialog() == true && !ShredList.Contains(dlg.FolderName)) ShredList.Add(dlg.FolderName);
    }

    public void AddShredPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths.Where(p => !ShredList.Contains(p))) ShredList.Add(p);
    }

    [RelayCommand]
    private void RemoveShred(string? path)
    {
        if (path is not null) ShredList.Remove(path);
    }

    [RelayCommand]
    private async Task ShredAsync()
    {
        if (ShredList.Count == 0) return;
        var risky = ShredList.FirstOrDefault(p => Core.Leftovers.ProtectionGuard.IsProtectedPath(p));
        if (risky is not null)
        {
            _ui.Error("Not allowed", $"{risky} is a system or shared location and can't be shredded.");
            return;
        }
        if (!await _ui.ConfirmAsync("Erase forever?", $"{ShredList.Count} item(s) will be overwritten and deleted. This cannot be undone. There is no backup and no Recycle Bin.", "Erase forever")) return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var (done, failed) = await SecureErase.ShredAsync(ShredList.ToList(), WipeMethod, new Progress<string>(s => Status = s), _cts.Token);
            _ui.Success("Erased", $"{done} file(s) destroyed{(failed > 0 ? $", {failed} could not be erased (in use?)" : "")}.");
            foreach (var p in ShredList.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList()) ShredList.Remove(p);
        }
        catch (OperationCanceledException)
        {
            _ui.Toast("Stopped", "Shredding cancelled. Files erased so far are gone.");
        }
        finally
        {
            IsBusy = false;
            Status = "";
        }
    }
}
