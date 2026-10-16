using Reclaim.Core.Inventory;

namespace Reclaim.Core.Cleaners;

/// <summary>Definitions of what each history/trace cleaner removes.</summary>
public static class CleanerCatalog
{
    private const string Explorer = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer";

    public static List<CleanerItem> Windows()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        const string g = "Windows";
        return
        [
            new() { Group = g, Name = "Recycle Bin", Description = "Empty the Recycle Bin on all drives", Targets = [new(TargetKind.RecycleBin, "")] },
            new() { Group = g, Name = "Temporary files (user)", Targets = [new(TargetKind.FolderContents, Path.GetTempPath())] },
            new() { Group = g, Name = "Temporary files (Windows)", Targets = [new(TargetKind.FolderContents, Path.Combine(win, "Temp"))] },
            new() { Group = g, Name = "Recent documents history", Targets = [new(TargetKind.FilePattern, recent, "*.lnk"), new(TargetKind.RegistrySubKeys, $@"{Explorer}\RecentDocs"), new(TargetKind.RegistryValues, $@"{Explorer}\RecentDocs")] },
            new() { Group = g, Name = "Jump lists", Targets = [new(TargetKind.FolderContents, Path.Combine(recent, "AutomaticDestinations")), new(TargetKind.FolderContents, Path.Combine(recent, "CustomDestinations"))] },
            new() { Group = g, Name = "Run dialog history", Targets = [new(TargetKind.RegistryValues, $@"{Explorer}\RunMRU")] },
            new() { Group = g, Name = "File search history", Targets = [new(TargetKind.RegistryValues, $@"{Explorer}\WordWheelQuery"), new(TargetKind.RegistryValues, $@"{Explorer}\TypedPaths")] },
            new() { Group = g, Name = "Open/Save dialog history", Targets = [new(TargetKind.RegistrySubKeys, $@"{Explorer}\ComDlg32\OpenSavePidlMRU"), new(TargetKind.RegistryValues, $@"{Explorer}\ComDlg32\LastVisitedPidlMRU"), new(TargetKind.RegistryValues, $@"{Explorer}\ComDlg32\LastVisitedPidlMRULegacy")] },
            new() { Group = g, Name = "Start menu usage tracking", Description = "Program launch counters (UserAssist)", Selected = false, Targets = UserAssist() },
            new() { Group = g, Name = "Registry Editor last key", Targets = [new(TargetKind.RegistryValue, @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey")] },
            new() { Group = g, Name = "Paint recent files", Targets = [new(TargetKind.RegistryValues, @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Paint\Recent File List")] },
            new() { Group = g, Name = "WordPad recent files", Targets = [new(TargetKind.RegistryValues, @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Applets\Wordpad\Recent File List")] },
            new() { Group = g, Name = "Clipboard", Targets = [new(TargetKind.Clipboard, "")] },
            new() { Group = g, Name = "Thumbnail cache", Description = "Explorer rebuilds it as needed", Targets = [new(TargetKind.FilePattern, Path.Combine(local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db")] },
            new() { Group = g, Name = "Memory dumps", Targets = [new(TargetKind.FolderContents, Path.Combine(win, "Minidump")), new(TargetKind.Path, Path.Combine(win, "MEMORY.DMP")), new(TargetKind.FolderContents, Path.Combine(local, "CrashDumps"))] },
            new() { Group = g, Name = "Error reports", Targets = [new(TargetKind.FolderContents, Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive")), new(TargetKind.FolderContents, Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue")), new(TargetKind.FolderContents, Path.Combine(local, @"Microsoft\Windows\WER"))] },
            new() { Group = g, Name = "Check Disk file fragments", Targets = ChkdskFragments() },
            new() { Group = g, Name = "Windows Update download cache", Description = "Already-installed update downloads", StopServiceFirst = true, ServiceName = "wuauserv", Targets = [new(TargetKind.FolderContents, Path.Combine(win, @"SoftwareDistribution\Download"))] },
            new() { Group = g, Name = "Delivery Optimization cache", Targets = [new(TargetKind.FolderContents, Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))] },
            new() { Group = g, Name = "DirectX shader cache", Targets = [new(TargetKind.FolderContents, Path.Combine(local, "D3DSCache"))] },
            new() { Group = g, Name = "Windows log files", Selected = false, Targets = [new(TargetKind.FilePattern, Path.Combine(win, "Logs"), "*.log", true), new(TargetKind.FilePattern, win, "*.log")] },
        ];
    }

    private static List<CleanTarget> UserAssist()
    {
        var list = new List<CleanTarget>();
        using var k = Infrastructure.RegistryUtil.Open($@"{Explorer}\UserAssist");
        if (k is null) return list;
        foreach (var guid in k.GetSubKeyNames())
            list.Add(new CleanTarget(TargetKind.RegistryValues, $@"{Explorer}\UserAssist\{guid}\Count"));
        return list;
    }

    private static List<CleanTarget> ChkdskFragments()
    {
        var list = new List<CleanTarget>();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            list.Add(new CleanTarget(TargetKind.FilePattern, d.RootDirectory.FullName, "*.chk"));
            try
            {
                foreach (var found in Directory.EnumerateDirectories(d.RootDirectory.FullName, "FOUND.???"))
                    list.Add(new CleanTarget(TargetKind.Path, found));
            }
            catch { }
        }
        return list;
    }

    public static List<CleanerItem> Browsers()
    {
        var items = new List<CleanerItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var (browser, profile, profileName) in BrowserExtensionProvider.Profiles())
        {
            var group = $"{browser.Name} · {profileName}";
            if (browser.IsFirefox)
            {
                var cacheProfile = Path.Combine(local, @"Mozilla\Firefox\Profiles", Path.GetFileName(profile));
                items.Add(new() { Group = group, Name = "Cache", ProcessName = browser.ProcessName, Targets = [new(TargetKind.FolderContents, Path.Combine(cacheProfile, "cache2")), new(TargetKind.FolderContents, Path.Combine(cacheProfile, "startupCache"))] });
                items.Add(new() { Group = group, Name = "Cookies", Selected = false, ProcessName = browser.ProcessName, Targets = [new(TargetKind.Path, Path.Combine(profile, "cookies.sqlite")), new(TargetKind.Path, Path.Combine(profile, "cookies.sqlite-wal"))] });
                items.Add(new() { Group = group, Name = "Form history", ProcessName = browser.ProcessName, Targets = [new(TargetKind.Path, Path.Combine(profile, "formhistory.sqlite"))] });
                items.Add(new() { Group = group, Name = "Session", Selected = false, ProcessName = browser.ProcessName, Targets = [new(TargetKind.FilePattern, profile, "sessionstore*"), new(TargetKind.FolderContents, Path.Combine(profile, "sessionstore-backups"))] });
                continue;
            }
            var network = Path.Combine(profile, "Network");
            items.Add(new() { Group = group, Name = "Cache", ProcessName = browser.ProcessName, Targets =
            [
                new(TargetKind.FolderContents, Path.Combine(profile, "Cache")), new(TargetKind.FolderContents, Path.Combine(profile, "Code Cache")),
                new(TargetKind.FolderContents, Path.Combine(profile, "GPUCache")), new(TargetKind.FolderContents, Path.Combine(profile, @"Service Worker\CacheStorage")),
                new(TargetKind.FolderContents, Path.Combine(profile, @"Service Worker\ScriptCache")),
            ] });
            items.Add(new() { Group = group, Name = "History", ProcessName = browser.ProcessName, Targets =
            [
                new(TargetKind.Path, Path.Combine(profile, "History")), new(TargetKind.Path, Path.Combine(profile, "History-journal")),
                new(TargetKind.Path, Path.Combine(profile, "Visited Links")), new(TargetKind.Path, Path.Combine(profile, "Top Sites")),
                new(TargetKind.Path, Path.Combine(profile, "Shortcuts")),
            ] });
            items.Add(new() { Group = group, Name = "Cookies", Description = "Signs you out of websites", Selected = false, ProcessName = browser.ProcessName, Targets =
            [
                new(TargetKind.Path, Path.Combine(network, "Cookies")), new(TargetKind.Path, Path.Combine(network, "Cookies-journal")),
                new(TargetKind.Path, Path.Combine(profile, "Cookies")),
            ] });
            items.Add(new() { Group = group, Name = "Session", Selected = false, ProcessName = browser.ProcessName, Targets = [new(TargetKind.FolderContents, Path.Combine(profile, "Sessions"))] });
        }
        return items;
    }

    public static List<CleanerItem> Office()
    {
        var items = new List<CleanerItem>();
        const string root = @"HKEY_CURRENT_USER\Software\Microsoft\Office";
        using var office = Infrastructure.RegistryUtil.Open(root);
        if (office is null) return items;
        foreach (var version in office.GetSubKeyNames().Where(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            foreach (var app in new[] { "Word", "Excel", "PowerPoint", "Access", "Publisher", "Visio" })
            {
                var targets = new List<CleanTarget>();
                var appKey = $@"{root}\{version}\{app}";
                targets.Add(new CleanTarget(TargetKind.RegistryValues, $@"{appKey}\File MRU", "Item *"));
                targets.Add(new CleanTarget(TargetKind.RegistryValues, $@"{appKey}\Place MRU", "Item *"));
                using var userMru = Infrastructure.RegistryUtil.Open($@"{appKey}\User MRU");
                if (userMru is not null)
                    foreach (var id in userMru.GetSubKeyNames())
                    {
                        targets.Add(new CleanTarget(TargetKind.RegistryValues, $@"{appKey}\User MRU\{id}\File MRU", "Item *"));
                        targets.Add(new CleanTarget(TargetKind.RegistryValues, $@"{appKey}\User MRU\{id}\Place MRU", "Item *"));
                    }
                if (!Infrastructure.RegistryUtil.KeyExists(appKey)) continue;
                items.Add(new CleanerItem
                {
                    Group = $"Microsoft Office {OfficeName(version)}",
                    Name = $"{app} recent files",
                    ProcessName = app switch { "Word" => "WINWORD", "Excel" => "EXCEL", "PowerPoint" => "POWERPNT", "Access" => "MSACCESS", "Publisher" => "MSPUB", _ => "VISIO" },
                    Targets = targets,
                });
            }
        }
        return items;
    }

    private static string OfficeName(string v) => v switch
    {
        "16.0" => "2016 / 2019 / 2021 / 365",
        "15.0" => "2013",
        "14.0" => "2010",
        "12.0" => "2007",
        "11.0" => "2003",
        _ => v,
    };
}
