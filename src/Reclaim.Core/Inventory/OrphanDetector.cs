using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

/// <summary>
/// Finds broken registrations (uninstaller and install folder both gone) and program folders that no
/// installed app claims (left behind by an earlier uninstall).
/// </summary>
public static class OrphanDetector
{
    private static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "Internet Explorer", "Microsoft", "Microsoft.NET", "ModifiableWindowsApps", "MSBuild",
        "Reference Assemblies", "Uninstall Information", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows Multimedia Platform", "Windows NT", "Windows Photo Viewer",
        "Windows Portable Devices", "Windows Security", "Windows Sidebar", "WindowsApps", "WindowsPowerShell",
        "dotnet", "PackageManagement", "Microsoft Update Health Tools", "Microsoft Office", "Microsoft Office 15",
        "Microsoft Visual Studio", "Microsoft SDKs", "Windows Kits", "Microsoft SQL Server", "Microsoft Analysis Services",
        "Application Verifier", "IIS", "IIS Express", "Hyper-V", "Npcap", "Git", "nodejs", "Reclaim", "desktop.ini",
    };

    /// <summary>Marks registry entries whose uninstaller and install location no longer exist.</summary>
    public static void MarkBrokenEntries(IEnumerable<InstalledApp> apps)
    {
        foreach (var app in apps.Where(a => a.Source == AppSource.Win32))
        {
            if (app.Has(AppFlags.Msi)) continue; // msiexec is always present; MSI health is checked by Windows Installer
            var exe = PathUtil.ExecutableFromCommand(app.UninstallString);
            if (exe is null || !Path.IsPathRooted(exe)) continue;
            var uninstallerMissing = !File.Exists(exe);
            var folderMissing = app.InstallLocation is null || !Directory.Exists(app.InstallLocation);
            if (uninstallerMissing && folderMissing) app.Flags |= AppFlags.Orphaned;
        }
    }

    /// <summary>Program Files / ProgramData folders with executables but no owning app.</summary>
    public static List<InstalledApp> FindUnclaimedFolders(IReadOnlyCollection<InstalledApp> apps, CancellationToken ct = default)
    {
        var claimed = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps)
        {
            if (a.InstallLocation is { } l) claimed.Add(l);
            if (PathUtil.ExecutableFromCommand(a.UninstallString) is { } u && Path.GetDirectoryName(u) is { } ud) claimed.Add(ud);
            if (PathUtil.Clean(a.DisplayIcon) is { } i && Path.GetDirectoryName(i) is { } id) claimed.Add(id);
            names.Add(PathUtil.Squash(a.Name));
            if (a.Publisher is not null) names.Add(PathUtil.Squash(a.Publisher));
        }
        // Something still running from or registered in a folder means it is in use, even without an Uninstall entry
        // (MSIX apps that also install to Program Files, anti-cheat services, machine-wide installers...).
        claimed.AddRange(ActiveReferences());

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

        var result = new List<InstalledApp>();
        foreach (var root in roots)
        {
            foreach (var dir in SafeDirs(root))
            {
                ct.ThrowIfCancellationRequested();
                var leaf = Path.GetFileName(dir);
                if (SystemFolders.Contains(leaf) || leaf.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
                // Claimed if any app lives in it or below it, or it lives inside an app folder (vendor folder holding several apps).
                if (claimed.Any(c => PathUtil.IsUnder(c, dir) || PathUtil.IsUnder(dir, c))) continue;
                var sq = PathUtil.Squash(leaf);
                if (sq.Length > 2 && names.Any(n => n.Length > 2 && (n.StartsWith(sq) || sq.StartsWith(n)))) continue;
                if (!ContainsExecutables(dir)) continue;
                if (IsMicrosoftSigned(dir)) continue;
                result.Add(new InstalledApp
                {
                    Id = "orphan:" + dir,
                    Name = leaf,
                    Publisher = "Unknown (leftover folder)",
                    InstallLocation = dir,
                    InstallDate = Directory.GetLastWriteTime(dir),
                    Source = AppSource.Orphan,
                    Flags = AppFlags.Orphaned,
                    Comments = "No installed program claims this folder. It is probably left over from an earlier uninstall.",
                });
            }
        }
        return result;
    }

    /// <summary>Folders referenced by services, App Paths, Run entries or scheduled tasks.</summary>
    private static IEnumerable<string> ActiveReferences()
    {
        var paths = new List<string?>();
        try { paths.AddRange(SystemTools.Services.List().Select(s => PathUtil.ExecutableFromCommand(s.ImagePath))); } catch { }
        try { paths.AddRange(SystemTools.ScheduledTasks.List().Select(t => t.Command)); } catch { }
        foreach (var keyPath in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                     @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
                 })
        {
            using var k = RegistryUtil.Open(keyPath);
            if (k is null) continue;
            foreach (var v in k.GetValueNames()) paths.Add(PathUtil.ExecutableFromCommand(RegistryUtil.GetString(k, v)));
        }
        foreach (var appPaths in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths" })
        {
            using var root = RegistryUtil.Open(appPaths);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k is not null) paths.Add(PathUtil.Clean(RegistryUtil.GetString(k, "")));
            }
        }
        foreach (var p in paths)
            if (!string.IsNullOrEmpty(p) && Path.GetDirectoryName(p) is { Length: > 3 } dir) yield return dir;
    }

    /// <summary>Folders whose programs are signed by Microsoft belong to Windows components or Microsoft apps.</summary>
    private static bool IsMicrosoftSigned(string dir)
    {
        try
        {
            var exe = Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 })
                .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            var sig = exe is null ? null : SignatureVerifier.Verify(exe);
            return sig?.Signer?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return []; }
    }

    private static bool ContainsExecutables(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.exe", new EnumerationOptions
            {
                RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3,
            }).Any();
        }
        catch { return false; }
    }
}
