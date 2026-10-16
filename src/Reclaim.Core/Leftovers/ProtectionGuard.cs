using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Leftovers;

/// <summary>User-editable exclusion (wildcards * and ?). Matching paths are never reported or deleted.</summary>
public sealed class ExclusionRule
{
    public string Pattern { get; set; } = "";
    public bool IncludeChildren { get; set; } = true;
    public bool Enabled { get; set; } = true;

    public bool Matches(string path)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Pattern)) return false;
        var p = Pattern.TrimEnd('\\');
        if (Wildcard.IsMatch(path.TrimEnd('\\'), p)) return true;
        return IncludeChildren && Wildcard.IsMatch(path, p + "\\*");
    }

    public static List<ExclusionRule> Defaults() =>
    [
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\*", IncludeChildren = false },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", IncludeChildren = true },
        new() { Pattern = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft", IncludeChildren = true },
        new() { Pattern = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", IncludeChildren = true },
        new() { Pattern = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", IncludeChildren = true },
        new() { Pattern = @"*\Microsoft\Windows\CurrentVersion\Component Based Servicing*", IncludeChildren = true },
        new() { Pattern = @"*\Microsoft\Windows\CurrentVersion\SideBySide*", IncludeChildren = true },
        new() { Pattern = @"*\Microsoft\Windows Defender*", IncludeChildren = true },
        new() { Pattern = @"*\Microsoft\OneDrive", IncludeChildren = false },
    ];
}

/// <summary>
/// The last line of defence before anything is listed or deleted: system locations, shared roots,
/// other installed apps' folders and keys, and the user's exclusion list.
/// </summary>
public sealed class ProtectionGuard
{
    private readonly List<string> _otherAppDirs;
    private readonly HashSet<string> _otherAppKeys;
    private readonly IReadOnlyList<ExclusionRule> _rules;

    public ProtectionGuard(IEnumerable<InstalledApp> otherApps, IReadOnlyList<ExclusionRule>? rules = null)
    {
        _otherAppDirs = [];
        _otherAppKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in otherApps)
        {
            if (a.Source is AppSource.Orphan or AppSource.WindowsUpdate or AppSource.Forced) continue;
            var dir = AppFingerprint.ResolveInstallDir(a);
            if (dir is { Length: > 3 } && Directory.Exists(dir) && !IsProtectedPath(dir)) _otherAppDirs.Add(dir);
            if (a.RegistryKeyPath is not null) _otherAppKeys.Add(a.RegistryKeyPath);
        }
        _rules = rules ?? SettingsStore.Current.Exclusions;
    }

    /// <summary>Returns a human-readable reason if the path must not be touched, else null.</summary>
    public string? WhyProtectedPath(string path)
    {
        if (IsProtectedPath(path)) return "System or shared location";
        foreach (var other in _otherAppDirs)
        {
            if (PathUtil.IsUnder(path, other)) return $"Belongs to another installed program ({other})";
            if (PathUtil.IsUnder(other, path)) return $"Contains another installed program ({other})";
        }
        if (_rules.Any(r => r.Matches(path))) return "On the exclusion list";
        return null;
    }

    public string? WhyProtectedKey(string keyPath)
    {
        if (IsProtectedKey(keyPath)) return "Core Windows registry location";
        foreach (var other in _otherAppKeys)
        {
            if (keyPath.Equals(other, StringComparison.OrdinalIgnoreCase) || PathUtil.IsUnder(other, keyPath))
                return "Holds another installed program's registration";
        }
        if (_rules.Any(r => r.Matches(keyPath))) return "On the exclusion list";
        return null;
    }

    // ------------------------------------------------------------------ static system lists

    private static readonly Lazy<HashSet<string>> ProtectedDirs = new(() =>
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) set.Add(p.TrimEnd('\\')); }
        foreach (Environment.SpecialFolder f in Enum.GetValues<Environment.SpecialFolder>())
        {
            try { Add(Environment.GetFolderPath(f)); } catch { }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(Path.Combine(Directory.GetParent(local)!.FullName, "LocalLow"));
        Add(Directory.GetParent(local)!.FullName); // AppData
        Add(Path.GetTempPath());
        Add(Path.Combine(local, "Temp"));
        Add(Path.Combine(local, "Microsoft"));
        Add(Path.Combine(local, "Packages"));
        Add(Path.Combine(local, "Programs"));
        Add(Path.Combine(roaming, "Microsoft"));
        Add(Path.Combine(programData, "Microsoft"));
        Add(Path.Combine(programData, "Package Cache"));
        Add(Path.Combine(programData, "Packages"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Common Files"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Common Files"));
        Add(Path.Combine(profile, "Downloads"));
        Add(Path.Combine(profile, "OneDrive"));
        Add(Directory.GetParent(profile)!.FullName); // C:\Users
        Add(@"C:\Users\Public");
        Add(AppPaths.DataRoot);
        Add(AppPaths.ExeDirectory);
        foreach (var d in DriveInfo.GetDrives()) Add(d.RootDirectory.FullName);
        return set;
    });

    private static readonly string[] ProtectedTrees =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Recent"),
        @"C:\System Volume Information",
        @"C:\$Recycle.Bin",
        @"C:\Recovery",
        @"C:\Boot",
    ];

    public static bool IsProtectedPath(string path)
    {
        var p = path.TrimEnd('\\');
        if (p.Length <= 3) return true;
        if (ProtectedDirs.Value.Contains(p)) return true;
        foreach (var tree in ProtectedTrees)
        {
            if (string.IsNullOrEmpty(tree)) continue;
            if (PathUtil.IsUnder(p, tree)) return true;
        }
        // Start Menu\Programs, Startup and the Desktop themselves are protected (their children are not).
        var name = Path.GetFileName(p);
        if (name.Equals("Programs", StringComparison.OrdinalIgnoreCase) && p.Contains("Start Menu", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static readonly string[] ProtectedKeyExact =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies", @"HKEY_LOCAL_MACHINE\SOFTWARE\Clients", @"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\ODBC", @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Classes", @"HKEY_LOCAL_MACHINE\SYSTEM",
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet", @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services",
        @"HKEY_CURRENT_USER\Software", @"HKEY_CURRENT_USER\Software\Microsoft", @"HKEY_CURRENT_USER\Software\Classes",
        @"HKEY_CURRENT_USER\Software\Policies", @"HKEY_CURRENT_USER\Software\WOW6432Node", @"HKEY_CLASSES_ROOT",
        @"HKEY_CLASSES_ROOT\CLSID", @"HKEY_CLASSES_ROOT\TypeLib", @"HKEY_CLASSES_ROOT\Interface", @"HKEY_CLASSES_ROOT\Installer",
        @"HKEY_LOCAL_MACHINE\" + Inventory.RegistryUninstallProvider.UninstallSubKey,
        @"HKEY_LOCAL_MACHINE\" + Inventory.RegistryUninstallProvider.UninstallSubKeyWow,
        @"HKEY_CURRENT_USER\" + Inventory.RegistryUninstallProvider.UninstallSubKey,
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
    ];

    private static readonly string[] ProtectedKeyTrees =
    [
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control",
        @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum",
        @"HKEY_LOCAL_MACHINE\SYSTEM\Setup",
        @"HKEY_LOCAL_MACHINE\SECURITY",
        @"HKEY_LOCAL_MACHINE\SAM",
        @"HKEY_LOCAL_MACHINE\BCD00000000",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel",
    ];

    public static bool IsProtectedKey(string keyPath)
    {
        var k = keyPath.TrimEnd('\\');
        if (ProtectedKeyExact.Any(p => p.Equals(k, StringComparison.OrdinalIgnoreCase))) return true;
        if (ProtectedKeyTrees.Any(t => PathUtil.IsUnder(k, t))) return true;
        // Never delete a hive-level vendor key such as HKLM\SOFTWARE\Microsoft\<anything> that Windows owns.
        var parts = k.Split('\\');
        if (parts.Length <= 2) return true;
        if (parts.Length == 3 && parts[1].Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase) && parts[2].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
