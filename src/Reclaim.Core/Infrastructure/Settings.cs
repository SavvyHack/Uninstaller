using System.Text.Json;
using System.Text.Json.Serialization;
using Reclaim.Core.Leftovers;

namespace Reclaim.Core.Infrastructure;

public enum ThemePreference { System, Light, Dark }

public enum DeleteMode
{
    /// <summary>Move leftovers into Reclaim's own restorable backup store.</summary>
    Quarantine,
    /// <summary>Send leftovers to the Windows Recycle Bin.</summary>
    RecycleBin,
    /// <summary>Delete permanently (registry is still exported first).</summary>
    Permanent,
}

public sealed class ReclaimSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public ScanMode DefaultScanMode { get; set; } = ScanMode.Moderate;
    public DeleteMode DeleteMode { get; set; } = DeleteMode.Quarantine;
    public bool CreateRestorePoint { get; set; } = true;
    public bool FullRegistryBackupBeforeStoreUninstall { get; set; }
    public bool PreselectCertainLeftovers { get; set; } = true;
    public bool PreselectLikelyLeftovers { get; set; }
    public bool ShowSystemComponents { get; set; }
    public bool ShowWindowsUpdates { get; set; }
    public bool ForceStopRunningExecutables { get; set; } = true;
    public bool WatchExternalUninstalls { get; set; } = true;
    public int BatchConcurrency { get; set; } = 2;
    public int UninstallTimeoutMinutes { get; set; } = 30;
    public bool JunkIgnoreRecentlyAccessed { get; set; } = true;
    public bool ShellIntegration { get; set; }
    public List<ExclusionRule> Exclusions { get; set; } = ExclusionRule.Defaults();
    public List<string> JunkPatterns { get; set; } = Cleaners.JunkFilesCleaner.DefaultPatterns.ToList();
    public List<string> JunkExcludedPaths { get; set; } = Cleaners.JunkFilesCleaner.DefaultExcludedPaths.ToList();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static ReclaimSettings? _current;
    private static readonly object Gate = new();

    public static ReclaimSettings Current
    {
        get
        {
            lock (Gate) return _current ??= Load();
        }
    }

    public static event EventHandler? Changed;

    public static ReclaimSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<ReclaimSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new ReclaimSettings();
    }

    public static void Save()
    {
        lock (Gate)
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, Json));
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static JsonSerializerOptions JsonOptions => Json;
}
