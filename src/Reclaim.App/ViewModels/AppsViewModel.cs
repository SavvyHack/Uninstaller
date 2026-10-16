using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Reclaim.App.Services;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Reclaim.Core.Monitor;
using Reclaim.Core.SystemTools;
using Reclaim.Core.Uninstall;
using Reclaim.Core.Updates;

namespace Reclaim.App.ViewModels;

public enum AppFilter { All, Programs, WindowsApps, Extensions, Leftovers, UpdateAvailable, Recent, Large, Unsigned, Bundleware, Traced, WindowsUpdates }

public sealed partial class AppsViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;
    private readonly DispatcherTimer _searchDebounce;
    private Regex? _regex;

    public CatalogService Catalog => _catalog;
    public ListCollectionView View { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _useRegex;
    [ObservableProperty] private bool _regexInvalid;
    [ObservableProperty] private AppFilter _filter = AppFilter.All;
    [ObservableProperty] private string _sortBy = "Name";
    [ObservableProperty] private AppItemViewModel? _selected;
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private string _checkedSize = "";
    [ObservableProperty] private bool _quiet;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private int _visibleCount;

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "Size", "Install date", "Publisher"];

    public AppsViewModel(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        View = new ListCollectionView(catalog.Apps) { Filter = Matches };
        ApplySort();
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Refresh();
        };
        catalog.Apps.CollectionChanged += OnAppsChanged;
        catalog.Loaded += (_, _) => Refresh();
        foreach (var a in catalog.Apps) a.PropertyChanged += OnItemChanged;
    }

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (AppItemViewModel a in e.NewItems) a.PropertyChanged += OnItemChanged;
        if (e.OldItems is not null) foreach (AppItemViewModel a in e.OldItems) a.PropertyChanged -= OnItemChanged;
        if (Selected is not null && !_catalog.Apps.Contains(Selected)) Selected = null;
        UpdateChecked();
        UpdateSummary();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItemViewModel.IsChecked)) UpdateChecked();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnUseRegexChanged(bool value) => Refresh();
    partial void OnFilterChanged(AppFilter value) => Refresh();
    partial void OnSortByChanged(string value) => ApplySort();

    public void SetFilter(AppFilter filter) => Filter = filter;

    private void Refresh()
    {
        _regex = null;
        RegexInvalid = false;
        if (UseRegex && !string.IsNullOrWhiteSpace(SearchText))
        {
            try { _regex = new Regex(SearchText, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch { RegexInvalid = true; }
        }
        View.Refresh();
        UpdateSummary();
    }

    private void ApplySort()
    {
        View.SortDescriptions.Clear();
        View.CustomSort = null;
        switch (SortBy)
        {
            case "Size": View.SortDescriptions.Add(new SortDescription(nameof(AppItemViewModel.SizeBytes), ListSortDirection.Descending)); break;
            case "Install date": View.SortDescriptions.Add(new SortDescription(nameof(AppItemViewModel.InstallDate), ListSortDirection.Descending)); break;
            case "Publisher": View.SortDescriptions.Add(new SortDescription(nameof(AppItemViewModel.Publisher), ListSortDirection.Ascending)); break;
        }
        View.SortDescriptions.Add(new SortDescription(nameof(AppItemViewModel.Name), ListSortDirection.Ascending));
    }

    private bool Matches(object o)
    {
        if (o is not AppItemViewModel vm) return false;
        var m = vm.Model;
        var pass = Filter switch
        {
            AppFilter.All => m.Source is not AppSource.WindowsUpdate,
            AppFilter.Programs => m.Source is AppSource.Win32 or AppSource.Steam or AppSource.Scoop or AppSource.Chocolatey,
            AppFilter.WindowsApps => m.Source == AppSource.Store,
            AppFilter.Extensions => m.Source == AppSource.BrowserExtension,
            AppFilter.Leftovers => m.Source == AppSource.Orphan || m.Has(AppFlags.Orphaned),
            AppFilter.UpdateAvailable => m.Has(AppFlags.UpdateAvailable),
            AppFilter.Recent => m.InstallDate >= DateTime.Now.AddDays(-30),
            AppFilter.Large => m.SizeBytes >= 500L * 1024 * 1024,
            AppFilter.Unsigned => m.Has(AppFlags.Unsigned),
            AppFilter.Bundleware => m.Has(AppFlags.Bundleware) || m.Extra.ContainsKey("InstalledAlongside"),
            AppFilter.Traced => m.Has(AppFlags.Traced),
            AppFilter.WindowsUpdates => m.Source == AppSource.WindowsUpdate,
            _ => true,
        };
        if (!pass) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        if (_regex is not null) return _regex.IsMatch(m.Name) || (m.Publisher is not null && _regex.IsMatch(m.Publisher));
        if (UseRegex && RegexInvalid) return true;
        return m.Name.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)
               || (m.Publisher?.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void UpdateChecked()
    {
        var c = _catalog.Apps.Where(a => a.IsChecked).ToList();
        CheckedCount = c.Count;
        CheckedSize = Format.Bytes(c.Sum(a => a.SizeBytes));
    }

    private void UpdateSummary()
    {
        VisibleCount = View.Count;
        var programs = _catalog.Apps.Count(a => a.Model.Source is not (AppSource.WindowsUpdate or AppSource.Orphan or AppSource.BrowserExtension));
        Summary = $"{programs} installed · {Format.Bytes(_catalog.Apps.Sum(a => a.SizeBytes))} on disk · showing {VisibleCount}";
    }

    private IEnumerable<AppItemViewModel> Checked => _catalog.Apps.Where(a => a.IsChecked);

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private Task RefreshAsync() => _catalog.LoadAsync();

    [RelayCommand]
    private void Uninstall(AppItemViewModel? item)
    {
        item ??= Selected;
        if (item is null) return;
        var mode = item.Model.Source == AppSource.Orphan ? WizardMode.Forced : WizardMode.Normal;
        var trace = item.Model.Has(AppFlags.Traced) ? TraceStore.FindFor(item.Model) : null;
        if (trace is not null) mode = WizardMode.Traced;
        _ui.ShowSheet(new UninstallWizardViewModel([item.Model], mode, _catalog, _ui, trace));
    }

    [RelayCommand]
    private void UninstallChecked()
    {
        var apps = Checked.Select(a => a.Model).ToList();
        if (apps.Count == 0) return;
        _ui.ShowSheet(new UninstallWizardViewModel(apps, WizardMode.Normal, _catalog, _ui) { Quiet = Quiet });
    }

    [RelayCommand]
    private void ForceRemove()
    {
        if (Selected is null) return;
        _ui.ShowSheet(new UninstallWizardViewModel([Selected.Model], WizardMode.Forced, _catalog, _ui));
    }

    [RelayCommand]
    private async Task ForcedMsiAsync()
    {
        if (Selected is null) return;
        if (!await _ui.ConfirmAsync("Forced MSI uninstall",
                "Use this when the program's uninstaller only offers Change or Repair. Reclaim will ask Windows Installer to remove the product directly.", "Remove"))
            return;
        var outcome = await new UninstallRunner().RunAsync(new UninstallRequest { App = Selected.Model, ForcedMsi = true }, null, CancellationToken.None);
        if (outcome.IsSuccess) _ui.ShowSheet(new UninstallWizardViewModel([Selected.Model], WizardMode.ScanOnly, _catalog, _ui));
        else _ui.Error("Forced MSI uninstall", outcome.Message);
    }

    [RelayCommand]
    private void ClearChecks()
    {
        foreach (var a in Checked.ToList()) a.IsChecked = false;
    }

    [RelayCommand]
    private void CheckVisible()
    {
        foreach (AppItemViewModel a in View) a.IsChecked = true;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var loc = Selected?.Model.InstallLocation ?? Path.GetDirectoryName(PathUtil.ExecutableFromCommand(Selected?.Model.UninstallString) ?? "");
        if (!string.IsNullOrEmpty(loc)) ProcessRunner.RevealInExplorer(loc);
    }

    [RelayCommand]
    private void OpenRegistry()
    {
        if (Selected?.Model.RegistryKeyPath is { } k) RegistryUtil.OpenInRegedit(k);
    }

    [RelayCommand]
    private void SearchOnline()
    {
        if (Selected is null) return;
        ProcessRunner.SearchOnline($"{Selected.Model.Name} {Selected.Model.Publisher}".Trim());
    }

    [RelayCommand]
    private static void OpenLink(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url)) ProcessRunner.OpenShell(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url);
    }

    [RelayCommand]
    private void ModifyRepair()
    {
        if (Selected?.Model.ModifyPath is { } m)
        {
            var (exe, args) = CommandLine.Split(Environment.ExpandEnvironmentVariables(m));
            ProcessRunner.OpenShell(exe, args);
        }
    }

    [RelayCommand]
    private void Relocate()
    {
        if (Selected is null) return;
        ToolsViewModel.PendingRelocation = Selected.Model;
        _ui.Navigate(typeof(Views.Pages.ToolsPage));
    }

    [RelayCommand]
    private async Task UpdateAppAsync()
    {
        if (Selected?.Model.WingetId is not { } id) return;
        _ui.Toast("Updating", $"Installing the latest {Selected.Name}…", Wpf.Ui.Controls.ControlAppearance.Info, Wpf.Ui.Controls.SymbolRegular.ArrowSync24);
        var r = await SoftwareUpdater.UpgradeAsync(id, CancellationToken.None);
        if (r.ExitCode == 0) _ui.Success("Updated", $"{Selected?.Name} is up to date.");
        else _ui.Error("Update failed", string.IsNullOrWhiteSpace(r.StdErr) ? $"winget exited with {r.ExitCode}" : r.StdErr);
        await _catalog.LoadAsync();
    }

    [RelayCommand]
    private void Export(string format)
    {
        var apps = View.Cast<AppItemViewModel>().Select(a => a.Model).ToList();
        if (format is "ps1" or "bat")
        {
            var selection = Checked.Select(a => a.Model).ToList();
            if (selection.Count == 0 && Selected is not null) selection = [Selected.Model];
            if (selection.Count == 0)
            {
                _ui.Toast("Nothing selected", "Tick the programs to put in the script first.");
                return;
            }
            var dlg = new SaveFileDialog { Filter = format == "ps1" ? "PowerShell script|*.ps1" : "Batch file|*.bat", FileName = "uninstall-apps." + format };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, ScriptExporter.Generate(selection, format == "ps1" ? ScriptKind.PowerShell : ScriptKind.Batch));
            _ui.Success("Script saved", dlg.FileName);
            return;
        }
        var fmt = Enum.Parse<ExportFormat>(format, true);
        var ext = fmt switch { ExportFormat.Csv => "csv", ExportFormat.Json => "json", ExportFormat.Text => "txt", _ => "html" };
        var save = new SaveFileDialog { Filter = $"{fmt}|*.{ext}", FileName = $"Installed programs.{ext}" };
        if (save.ShowDialog() != true) return;
        File.WriteAllText(save.FileName, ExportService.ExportApps(apps, fmt));
        ProcessRunner.RevealInExplorer(save.FileName);
    }

    [RelayCommand]
    private void ShowProperties()
    {
        var exe = Selected?.Model.MainExecutable ?? PathUtil.ExecutableFromCommand(Selected?.Model.UninstallString);
        if (exe is null || !File.Exists(exe)) return;
        var info = new SHELLEXECUTEINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHELLEXECUTEINFO>(), lpVerb = "properties", lpFile = exe, nShow = 5, fMask = 12 };
        ShellExecuteEx(ref info);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp, lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon, hProcess;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
}
