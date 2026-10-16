using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Reclaim.App.Services;
using Reclaim.Core.Models;
using Reclaim.Core.Uninstall;

namespace Reclaim.App.ViewModels;

public sealed partial class ForceViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;

    [ObservableProperty] private string _target = "";

    public ObservableCollection<AppItemViewModel> Leftovers { get; } = [];

    public ForceViewModel(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        catalog.Loaded += (_, _) => Reload();
        Reload();
    }

    private void Reload()
    {
        Leftovers.Clear();
        foreach (var a in _catalog.Apps.Where(a => a.Model.Source == AppSource.Orphan || a.Model.Has(AppFlags.Orphaned)))
        {
            a.EnsureIcon();
            Leftovers.Add(a);
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Choose the program's folder" };
        if (dlg.ShowDialog() == true) Target = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var dlg = new OpenFileDialog { Title = "Choose one of the program's files", Filter = "Programs|*.exe;*.lnk|All files|*.*" };
        if (dlg.ShowDialog() == true) Target = dlg.FileName;
    }

    [RelayCommand]
    private void Scan()
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            _ui.Toast("What should be removed?", "Type the program's name or choose its folder or file.");
            return;
        }
        var app = ForcedUninstall.Create(Target, _catalog.Models);
        _ui.ShowSheet(new UninstallWizardViewModel([app], WizardMode.Forced, _catalog, _ui));
    }

    [RelayCommand]
    private void Clean(AppItemViewModel? item)
    {
        if (item is null) return;
        _ui.ShowSheet(new UninstallWizardViewModel([item.Model], WizardMode.Forced, _catalog, _ui));
    }
}
