using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reclaim.App.Services;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.SystemTools;

namespace Reclaim.App.ViewModels;

public sealed partial class AutorunItemViewModel : ObservableObject
{
    private readonly UiService _ui;
    public AutorunEntry Entry { get; }

    [ObservableProperty] private System.Windows.Media.ImageSource? _icon;

    public AutorunItemViewModel(AutorunEntry entry, UiService ui)
    {
        Entry = entry;
        _ui = ui;
    }

    public string Name => Entry.Name;
    public string Publisher => string.IsNullOrWhiteSpace(Entry.Publisher) ? "Unknown publisher" : Entry.Publisher!;
    public string Command => Entry.Command ?? "";
    public string KindText => Entry.KindText + (Entry.AllUsers ? " · all users" : " · this user");
    public bool IsRunning => Entry.IsRunning;
    public bool IsInvalid => !Entry.IsValid;
    public bool CanToggle => Entry.Kind != AutorunKind.RegistryRunOnce;
    public string Initial => Entry.Name.Length > 0 ? Entry.Name[..1].ToUpperInvariant() : "?";

    public bool Enabled
    {
        get => Entry.Enabled;
        set
        {
            if (Entry.Enabled == value) return;
            try
            {
                AutorunManager.SetEnabled(Entry, value);
                _ui.Toast(value ? "Enabled" : "Disabled", $"{Name} will {(value ? "" : "no longer ")}start with Windows.");
            }
            catch (Exception ex)
            {
                _ui.Error("Couldn't change it", ex.Message);
            }
            OnPropertyChanged();
        }
    }

    public async void LoadIcon() => Icon ??= await IconService.LoadAsync(Entry.ExePath);

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Enabled));
    }
}

public sealed partial class StartupViewModel : ObservableObject
{
    private readonly UiService _ui;

    public ObservableCollection<AutorunItemViewModel> Items { get; } = [];
    public ListCollectionView View { get; }

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _kindFilter = "All";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _summary = "";

    public StartupViewModel(UiService ui)
    {
        _ui = ui;
        View = new ListCollectionView(Items) { Filter = Match };
        View.SortDescriptions.Add(new SortDescription(nameof(AutorunItemViewModel.Name), ListSortDirection.Ascending));
        _ = LoadAsync();
    }

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnKindFilterChanged(string value) => View.Refresh();

    private bool Match(object o)
    {
        if (o is not AutorunItemViewModel i) return false;
        var kindOk = KindFilter switch
        {
            "Registry" => i.Entry.Kind is AutorunKind.RegistryRun or AutorunKind.RegistryRunOnce,
            "Folder" => i.Entry.Kind == AutorunKind.StartupFolder,
            "Tasks" => i.Entry.Kind == AutorunKind.ScheduledTask,
            "Services" => i.Entry.Kind == AutorunKind.Service,
            "Disabled" => !i.Enabled,
            _ => true,
        };
        return kindOk && (string.IsNullOrWhiteSpace(Search)
                          || i.Name.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
                          || i.Publisher.Contains(Search, StringComparison.CurrentCultureIgnoreCase)
                          || i.Command.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => AutorunManager.List());
            Items.Clear();
            foreach (var e in entries)
            {
                var vm = new AutorunItemViewModel(e, _ui);
                vm.LoadIcon();
                Items.Add(vm);
            }
            Summary = $"{entries.Count(e => e.Enabled)} of {entries.Count} start automatically";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private static void Run(AutorunItemViewModel? i)
    {
        if (i is null) return;
        AutorunManager.Run(i.Entry);
    }

    [RelayCommand]
    private static void Kill(AutorunItemViewModel? i)
    {
        if (i is null) return;
        AutorunManager.Kill(i.Entry);
        i.Refresh();
    }

    [RelayCommand]
    private static void OpenFolder(AutorunItemViewModel? i)
    {
        if (i?.Entry.ExePath is { } p) ProcessRunner.RevealInExplorer(p);
    }

    [RelayCommand]
    private static void GoToLocation(AutorunItemViewModel? i)
    {
        if (i is null) return;
        switch (i.Entry.Kind)
        {
            case AutorunKind.RegistryRun or AutorunKind.RegistryRunOnce: RegistryUtil.OpenInRegedit(i.Entry.RegistryKey!); break;
            case AutorunKind.StartupFolder: ProcessRunner.RevealInExplorer(i.Entry.FilePath!); break;
            case AutorunKind.ScheduledTask: ProcessRunner.OpenShell("taskschd.msc"); break;
            case AutorunKind.Service: ProcessRunner.OpenShell("services.msc"); break;
        }
    }

    [RelayCommand]
    private static void SearchOnline(AutorunItemViewModel? i)
    {
        if (i is not null) ProcessRunner.SearchOnline($"{i.Name} {Path.GetFileName(i.Entry.ExePath ?? "")}");
    }

    [RelayCommand]
    private async Task DeleteAsync(AutorunItemViewModel? i)
    {
        if (i is null) return;
        if (i.Entry.Kind == AutorunKind.Service)
        {
            _ui.Toast("Services can't be deleted here", "Turn it off instead, or uninstall the program that owns it.");
            return;
        }
        if (!await _ui.ConfirmAsync("Remove startup entry?", $"{i.Name} will be removed from startup. A backup is kept on the Backups page.", "Remove")) return;
        if (AutorunManager.Delete(i.Entry)) Items.Remove(i);
        else _ui.Error("Couldn't remove it", "The entry may be protected.");
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Program to start with Windows", Filter = "Programs|*.exe;*.bat;*.cmd;*.lnk" };
        if (dlg.ShowDialog() != true) return;
        var name = await _ui.PromptAsync("Add to startup", "Name", Path.GetFileNameWithoutExtension(dlg.FileName), "Add");
        if (string.IsNullOrWhiteSpace(name)) return;
        AutorunManager.Add(name, CommandLine.Quote(dlg.FileName), AutorunKind.RegistryRun, allUsers: false);
        await LoadAsync();
    }
}
