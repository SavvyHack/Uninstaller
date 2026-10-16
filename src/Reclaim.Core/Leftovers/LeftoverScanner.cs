using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Reclaim.Core.SystemTools;

namespace Reclaim.Core.Leftovers;

/// <summary>
/// Finds files, folders, registry data and system objects an app left behind.
/// Every candidate goes through <see cref="ProtectionGuard"/> and gets a confidence score with reasons.
/// </summary>
public sealed partial class LeftoverScanner
{
    private readonly AppFingerprint _fp;
    private readonly ProtectionGuard _guard;
    private readonly Dictionary<string, LeftoverItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedClsids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removedProgIds = new(StringComparer.OrdinalIgnoreCase);
    private IProgress<string>? _progress;
    private CancellationToken _ct;

    public LeftoverScanner(AppFingerprint fingerprint, ProtectionGuard guard)
    {
        _fp = fingerprint;
        _guard = guard;
    }

    /// <summary>Convenience: fingerprint + guard from an app and the rest of the catalog.</summary>
    public static LeftoverScanner For(InstalledApp app, IEnumerable<InstalledApp> allApps, AppFingerprint? capturedBeforeUninstall = null) =>
        new(capturedBeforeUninstall ?? AppFingerprint.FromApp(app), new ProtectionGuard(allApps.Where(a => !ReferenceEquals(a, app) && a.Id != app.Id)));

    public async Task<List<LeftoverItem>> ScanAsync(ScanMode mode, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        _progress = progress;
        _ct = ct;
        _items.Clear();

        await Task.Run(() =>
        {
            Step("Checking the program's own folder and registration", ScanDirect);
            Step("Checking shortcuts", ScanShortcuts);
            Step("Checking services, scheduled tasks and startup entries", ScanSystemObjects);
            Step("Checking Windows Installer data", ScanMsiData);
            if (mode >= ScanMode.Moderate)
            {
                Step("Scanning program and data folders", () => ScanFolders(deep: mode == ScanMode.Advanced));
                Step("Scanning registry software keys", ScanSoftwareKeys);
                Step("Checking file associations and app registrations", ScanAssociations);
                Step("Checking usage traces and environment", ScanTraces);
            }
            if (mode == ScanMode.Advanced)
            {
                Step("Checking COM registrations and shell extensions", ScanCom);
                Step("Checking drivers", ScanDrivers);
                Step("Deep registry reference sweep", ReferenceSweep);
            }
        }, ct);

        var result = Consolidate();
        Step("Measuring sizes", () =>
        {
            foreach (var i in result.Where(i => i.Kind is LeftoverKind.Directory or LeftoverKind.File))
            {
                ct.ThrowIfCancellationRequested();
                i.SizeBytes = FileOps.SizeOf(i.Path);
            }
        });

        var s = SettingsStore.Current;
        foreach (var i in result)
            i.Selected = (i.Bucket == ConfidenceBucket.Certain && s.PreselectCertainLeftovers)
                         || (i.Bucket == ConfidenceBucket.Likely && s.PreselectLikelyLeftovers);
        Log.Info($"Leftover scan ({mode}) for {_fp.DisplayName}: {result.Count} items");
        return result;
    }

    private void Step(string label, Action action)
    {
        _ct.ThrowIfCancellationRequested();
        _progress?.Report(label);
        try
        {
            action();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"Scan step '{label}' failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ adding candidates

    private void AddPath(LeftoverKind kind, string path, string category, int confidence, string reason, bool bypassGuard = false)
    {
        if (confidence < ConfidenceScorer.ReportThreshold) return;
        if (kind == LeftoverKind.Directory ? !Directory.Exists(path) : !File.Exists(path)) return;
        if (!bypassGuard && _guard.WhyProtectedPath(path) is not null) return;
        Merge(new LeftoverItem { Kind = kind, Path = path.TrimEnd('\\'), Category = category }, confidence, reason);
    }

    private void AddKey(string keyPath, string category, int confidence, string reason)
    {
        if (confidence < ConfidenceScorer.ReportThreshold) return;
        if (_guard.WhyProtectedKey(keyPath) is not null) return;
        if (!RegistryUtil.KeyExists(keyPath)) return;
        Merge(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = keyPath, Category = category }, confidence, reason);
    }

    private void AddValue(string keyPath, string valueName, string category, int confidence, string reason)
    {
        if (confidence < ConfidenceScorer.ReportThreshold) return;
        // Values may live in core keys (Run, SharedDLLs...) - deleting the value is fine there. Block only
        // values in another app's key or on the exclusion list.
        if (!ProtectionGuard.IsProtectedKey(keyPath) && _guard.WhyProtectedKey(keyPath) is not null) return;
        if (SettingsStore.Current.Exclusions.Any(r => r.Matches(keyPath + "\\" + valueName))) return;
        Merge(new LeftoverItem { Kind = LeftoverKind.RegistryValue, Path = keyPath, ValueName = valueName, Category = category }, confidence, reason);
    }

    private void AddObject(LeftoverKind kind, string id, string category, int confidence, string reason, string? detail = null)
    {
        if (confidence < ConfidenceScorer.ReportThreshold) return;
        Merge(new LeftoverItem { Kind = kind, Path = id, ValueName = detail, Category = category }, confidence, reason);
    }

    private void Merge(LeftoverItem candidate, int confidence, string reason)
    {
        if (_items.TryGetValue(candidate.Key, out var existing))
        {
            existing.Raise(confidence, reason);
            return;
        }
        candidate.Raise(confidence, reason);
        _items[candidate.Key] = candidate;
    }

    /// <summary>Drops children already covered by a reported parent with equal or higher confidence.</summary>
    private List<LeftoverItem> Consolidate()
    {
        var containers = _items.Values
            .Where(i => i.Kind is LeftoverKind.Directory or LeftoverKind.RegistryKey)
            .OrderBy(i => i.Path.Length)
            .ToList();
        var result = new List<LeftoverItem>();
        foreach (var item in _items.Values.OrderByDescending(i => i.Confidence).ThenBy(i => i.Path))
        {
            var covered = containers.Any(c => !ReferenceEquals(c, item)
                && c.Confidence >= item.Confidence
                && SameFamily(c.Kind, item.Kind)
                && PathUtil.IsUnder(item.Path, c.Path)
                && (item.Kind == LeftoverKind.RegistryValue || !PathUtil.SamePath(item.Path, c.Path)));
            if (!covered) result.Add(item);
        }
        return result;
    }

    private static bool SameFamily(LeftoverKind container, LeftoverKind item) =>
        container == LeftoverKind.Directory ? item is LeftoverKind.Directory or LeftoverKind.File
        : container == LeftoverKind.RegistryKey && item is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue;

    // ------------------------------------------------------------------ phase 1: direct evidence

    private void ScanDirect()
    {
        if (_fp.InstallDir is { } dir)
            AddPath(LeftoverKind.Directory, dir, "Program folder", 100, "The program's install folder still exists");
        foreach (var extra in _fp.ExtraPaths)
            AddPath(Directory.Exists(extra) ? LeftoverKind.Directory : LeftoverKind.File, extra, "Program folder", 95, "Recorded during install tracing");
        if (_fp.UninstallKeyPath is { } key)
            AddKey(key, "Uninstall registration", 100, "The program's uninstall entry is still registered");

        if (_fp.PackageFamilyName is { } pfn)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            AddPath(LeftoverKind.Directory, Path.Combine(local, "Packages", pfn), "App data", 95, "Windows app data folder");
            AddKey($@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\{pfn.ToLowerInvariant()}",
                "App data", 85, "Windows app container storage");
        }
    }

    private void ScanShortcuts()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(roaming, @"Microsoft\Internet Explorer\Quick Launch"),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var lnk in SafeFiles(root, "*.lnk", recurse: true))
            {
                _ct.ThrowIfCancellationRequested();
                var target = ShortcutResolver.GetTarget(lnk);
                if (ConfidenceScorer.ReferencesInstallDir(target, _fp))
                {
                    AddPath(LeftoverKind.File, lnk, "Shortcuts", 95, "Shortcut points into the program folder", bypassGuard: true);
                    continue;
                }
                if (target is not null && !File.Exists(target) && !Directory.Exists(target)
                    && _fp.ExeNames.Contains(Path.GetFileName(target)))
                {
                    AddPath(LeftoverKind.File, lnk, "Shortcuts", 85, "Broken shortcut to one of the program's executables", bypassGuard: true);
                    continue;
                }
                var score = ConfidenceScorer.ScoreName(Path.GetFileNameWithoutExtension(lnk), _fp);
                if (score.Value >= 70 && (target is null || (!File.Exists(target) && !Directory.Exists(target))))
                    AddPath(LeftoverKind.File, lnk, "Shortcuts", score.Value, "Broken shortcut named after the program", bypassGuard: true);
            }

            // Start Menu folders named after the program.
            if (!root.Contains("Desktop", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dir in SafeDirs(root))
                {
                    var score = ConfidenceScorer.ScoreName(Path.GetFileName(dir), _fp);
                    if (score.Value >= 55) AddPath(LeftoverKind.Directory, dir, "Shortcuts", score.Value + 5, "Start menu folder: " + score.Reason);
                }
            }
        }
    }

    private void ScanSystemObjects()
    {
        foreach (var svc in Services.List())
        {
            _ct.ThrowIfCancellationRequested();
            if (svc.Type is 1 or 2) continue; // drivers are handled in Advanced
            if (ConfidenceScorer.ReferencesInstallDir(svc.ImagePath, _fp))
                AddObject(LeftoverKind.Service, svc.Name, "Services", 95, $"Service runs from the program folder ({svc.DisplayName})", svc.DisplayName);
            else
            {
                var exe = PathUtil.ExecutableFromCommand(svc.ImagePath);
                var score = ConfidenceScorer.ScoreName(svc.Name, _fp);
                if (score.Value >= 70 && exe is not null && !File.Exists(exe))
                    AddObject(LeftoverKind.Service, svc.Name, "Services", score.Value, $"Service's program file is missing; {score.Reason}", svc.DisplayName);
            }
        }

        foreach (var task in ScheduledTasks.List())
        {
            _ct.ThrowIfCancellationRequested();
            if (ConfidenceScorer.ReferencesInstallDir(task.Command, _fp))
                AddObject(LeftoverKind.ScheduledTask, task.Path, "Scheduled tasks", 95, "Task runs a program from the install folder");
            else
            {
                var score = ConfidenceScorer.ScoreName(task.Name, _fp);
                var missing = task.Command is not null && Path.IsPathRooted(task.Command) && !File.Exists(task.Command);
                if (score.Value >= 55) AddObject(LeftoverKind.ScheduledTask, task.Path, "Scheduled tasks", missing ? score.Value + 15 : score.Value - 10,
                    missing ? $"Task's program is missing; {score.Reason}" : score.Reason);
            }
        }

        var runKeys = new[]
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunOnce",
        };
        var removedRunNames = new List<string>();
        foreach (var keyPath in runKeys)
        {
            using var k = RegistryUtil.Open(keyPath);
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                var data = RegistryUtil.GetString(k, name);
                if (ConfidenceScorer.ReferencesInstallDir(data, _fp))
                {
                    AddValue(keyPath, name, "Startup entries", 95, "Starts a program from the install folder");
                    removedRunNames.Add(name);
                    continue;
                }
                var exe = PathUtil.ExecutableFromCommand(data);
                var score = ConfidenceScorer.ScoreName(name, _fp);
                if (exe is not null && !File.Exists(exe) && (score.Value >= 55 || _fp.ExeNames.Contains(Path.GetFileName(exe))))
                {
                    AddValue(keyPath, name, "Startup entries", Math.Max(score.Value, 80), "Startup entry for a missing program file");
                    removedRunNames.Add(name);
                }
            }
        }
        foreach (var approved in new[]
                 {
                     @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
                 })
        {
            using var k = RegistryUtil.Open(approved);
            if (k is null) continue;
            foreach (var name in k.GetValueNames().Where(n => removedRunNames.Contains(n, StringComparer.OrdinalIgnoreCase)))
                AddValue(approved, name, "Startup entries", 90, "Startup approval record for a removed entry");
        }

        if (_fp.InstallDir is not null)
        {
            foreach (var rule in FirewallRules.List().Where(r => ConfidenceScorer.ReferencesInstallDir(r.ApplicationPath, _fp)))
                AddObject(LeftoverKind.FirewallRule, rule.Name, "Firewall rules", 95, "Firewall rule for a program in the install folder", rule.ApplicationPath);
        }
    }

    private void ScanMsiData()
    {
        if (_fp.PackedProductCode is not { } packed) return;
        var keys = new[]
        {
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Products\{packed}",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\Features\{packed}",
            $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{packed}",
            $@"HKEY_CURRENT_USER\Software\Microsoft\Installer\Products\{packed}",
            $@"HKEY_CURRENT_USER\Software\Microsoft\Installer\Features\{packed}",
        };
        foreach (var k in keys) AddKey(k, "Windows Installer data", 95, "Windows Installer record for this product");

        foreach (var upgradeRoot in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Installer\UpgradeCodes", @"HKEY_CURRENT_USER\Software\Microsoft\Installer\UpgradeCodes" })
        {
            using var root = RegistryUtil.Open(upgradeRoot);
            if (root is null) continue;
            foreach (var code in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(code);
                if (k is null) continue;
                var names = k.GetValueNames();
                if (!names.Contains(packed, StringComparer.OrdinalIgnoreCase)) continue;
                if (names.Length == 1) AddKey($@"{upgradeRoot}\{code}", "Windows Installer data", 95, "Upgrade code used only by this product");
                else AddValue($@"{upgradeRoot}\{code}", packed, "Windows Installer data", 90, "Upgrade code reference");
            }
        }
    }

    // ------------------------------------------------------------------ enumeration helpers

    internal static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return []; }
    }

    internal static IEnumerable<string> SafeFiles(string root, string pattern, bool recurse)
    {
        try
        {
            return Directory.GetFiles(root, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = recurse, IgnoreInaccessible = true, MaxRecursionDepth = 6,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch { return []; }
    }
}
