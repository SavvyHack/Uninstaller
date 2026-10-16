using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Reclaim.App.Services;
using Reclaim.Core.Cleaners;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;
using Reclaim.Core.SystemTools;

namespace Reclaim.App.ViewModels;

/// <summary>Settings bind straight to <see cref="ReclaimSettings"/> and save on every change.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly CatalogService _catalog;
    private readonly UiService _ui;
    private ReclaimSettings S => SettingsStore.Current;

    public ObservableCollection<ExclusionRule> Exclusions { get; }
    public ObservableCollection<string> JunkPatterns { get; }
    public ObservableCollection<string> JunkExcludes { get; }

    public SettingsViewModel(CatalogService catalog, UiService ui)
    {
        _catalog = catalog;
        _ui = ui;
        Exclusions = new ObservableCollection<ExclusionRule>(S.Exclusions);
        JunkPatterns = new ObservableCollection<string>(S.JunkPatterns);
        JunkExcludes = new ObservableCollection<string>(S.JunkExcludedPaths);
    }

    private void Save([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        SettingsStore.Save();
        OnPropertyChanged(name);
    }

    public ThemePreference Theme
    {
        get => S.Theme;
        set
        {
            S.Theme = value;
            Save();
            UiService.ApplyTheme(value, Application.Current.MainWindow);
        }
    }

    public ScanMode DefaultScanMode { get => S.DefaultScanMode; set { S.DefaultScanMode = value; Save(); } }
    public DeleteMode DeleteMode { get => S.DeleteMode; set { S.DeleteMode = value; Save(); } }
    public bool CreateRestorePoint { get => S.CreateRestorePoint; set { S.CreateRestorePoint = value; Save(); } }
    public bool PreselectCertain { get => S.PreselectCertainLeftovers; set { S.PreselectCertainLeftovers = value; Save(); } }
    public bool PreselectLikely { get => S.PreselectLikelyLeftovers; set { S.PreselectLikelyLeftovers = value; Save(); } }
    public bool ForceStop { get => S.ForceStopRunningExecutables; set { S.ForceStopRunningExecutables = value; Save(); } }
    public double BatchConcurrency { get => S.BatchConcurrency; set { S.BatchConcurrency = (int)Math.Clamp(value, 1, 8); Save(); } }
    public double TimeoutMinutes { get => S.UninstallTimeoutMinutes; set { S.UninstallTimeoutMinutes = (int)Math.Clamp(value, 1, 240); Save(); } }
    public bool JunkIgnoreRecent { get => S.JunkIgnoreRecentlyAccessed; set { S.JunkIgnoreRecentlyAccessed = value; Save(); } }
    public bool FullRegistryBackupForStoreApps { get => S.FullRegistryBackupBeforeStoreUninstall; set { S.FullRegistryBackupBeforeStoreUninstall = value; Save(); } }

    public bool ShowSystemComponents
    {
        get => S.ShowSystemComponents;
        set
        {
            S.ShowSystemComponents = value;
            Save();
            _ = _catalog.LoadAsync();
        }
    }

    public bool ShowWindowsUpdates
    {
        get => S.ShowWindowsUpdates;
        set
        {
            S.ShowWindowsUpdates = value;
            Save();
            _ = _catalog.LoadAsync();
        }
    }

    public bool WatchExternal { get => S.WatchExternalUninstalls; set { S.WatchExternalUninstalls = value; Save(); } }

    public bool ShellIntegrationEnabled
    {
        get => ShellIntegration.IsEnabled;
        set
        {
            if (value && Environment.ProcessPath is { } exe) ShellIntegration.Enable(exe);
            else ShellIntegration.Disable();
            S.ShellIntegration = value;
            Save();
        }
    }

    public IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();
    public IReadOnlyList<ScanMode> ScanModes { get; } = Enum.GetValues<ScanMode>();
    public IReadOnlyList<DeleteMode> DeleteModes { get; } = Enum.GetValues<DeleteMode>();

    public string DataFolder => AppPaths.DataRoot;
    public string Mode => AppPaths.IsPortable ? "Portable (settings are stored next to Reclaim.exe)" : "Installed (settings are stored in ProgramData)";
    public string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    // ------------------------------------------------------------------ lists

    [RelayCommand]
    private async Task AddExclusionAsync()
    {
        var pattern = await _ui.PromptAsync("Exclude from leftover scans", @"Path or registry key. * and ? are wildcards, e.g. HKEY_CURRENT_USER\Software\MyTools*");
        if (string.IsNullOrWhiteSpace(pattern)) return;
        var rule = new ExclusionRule { Pattern = pattern.Trim(), IncludeChildren = true };
        S.Exclusions.Add(rule);
        Exclusions.Add(rule);
        SettingsStore.Save();
    }

    [RelayCommand]
    private void RemoveExclusion(ExclusionRule? rule)
    {
        if (rule is null) return;
        S.Exclusions.Remove(rule);
        Exclusions.Remove(rule);
        SettingsStore.Save();
    }

    [RelayCommand]
    private void ResetExclusions()
    {
        S.Exclusions = ExclusionRule.Defaults();
        Exclusions.Clear();
        foreach (var r in S.Exclusions) Exclusions.Add(r);
        SettingsStore.Save();
    }

    [RelayCommand]
    private static void SaveRule() => SettingsStore.Save();

    [RelayCommand]
    private async Task AddJunkPatternAsync()
    {
        var p = await _ui.PromptAsync("Add junk pattern", "e.g. *.log");
        if (string.IsNullOrWhiteSpace(p)) return;
        S.JunkPatterns.Add(p.Trim());
        JunkPatterns.Add(p.Trim());
        SettingsStore.Save();
    }

    [RelayCommand]
    private void RemoveJunkPattern(string? p)
    {
        if (p is null) return;
        S.JunkPatterns.Remove(p);
        JunkPatterns.Remove(p);
        SettingsStore.Save();
    }

    [RelayCommand]
    private async Task AddJunkExcludeAsync()
    {
        var p = await _ui.PromptAsync("Skip folders when scanning for junk", @"e.g. D:\Projects\*");
        if (string.IsNullOrWhiteSpace(p)) return;
        S.JunkExcludedPaths.Add(p.Trim());
        JunkExcludes.Add(p.Trim());
        SettingsStore.Save();
    }

    [RelayCommand]
    private void RemoveJunkExclude(string? p)
    {
        if (p is null) return;
        S.JunkExcludedPaths.Remove(p);
        JunkExcludes.Remove(p);
        SettingsStore.Save();
    }

    [RelayCommand]
    private void ResetJunk()
    {
        S.JunkPatterns = JunkFilesCleaner.DefaultPatterns.ToList();
        S.JunkExcludedPaths = JunkFilesCleaner.DefaultExcludedPaths.ToList();
        JunkPatterns.Clear();
        foreach (var p in S.JunkPatterns) JunkPatterns.Add(p);
        JunkExcludes.Clear();
        foreach (var p in S.JunkExcludedPaths) JunkExcludes.Add(p);
        SettingsStore.Save();
    }

    [RelayCommand]
    private static void OpenDataFolder() => ProcessRunner.RevealInExplorer(AppPaths.DataRoot);

    [RelayCommand]
    private static void OpenLogs() => ProcessRunner.RevealInExplorer(AppPaths.Logs);
}
