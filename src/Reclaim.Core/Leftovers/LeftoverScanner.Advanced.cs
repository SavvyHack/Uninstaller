using Microsoft.Win32;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.SystemTools;

namespace Reclaim.Core.Leftovers;

public sealed partial class LeftoverScanner
{
    private static readonly string[] ClassesRoots =
    [
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Classes",
        @"HKEY_CURRENT_USER\Software\Classes",
    ];

    // ------------------------------------------------------------------ COM / type libraries / shell extensions

    private void ScanCom()
    {
        if (_fp.InstallDir is null) return;
        foreach (var classes in ClassesRoots)
        {
            using var clsidRoot = RegistryUtil.Open(classes + @"\CLSID");
            if (clsidRoot is null) continue;
            foreach (var clsid in clsidRoot.GetSubKeyNames())
            {
                _ct.ThrowIfCancellationRequested();
                using var k = clsidRoot.OpenSubKey(clsid);
                if (k is null) continue;
                foreach (var server in new[] { "InprocServer32", "LocalServer32", "InprocHandler32" })
                {
                    using var s = k.OpenSubKey(server);
                    if (s is null) continue;
                    if (!ConfidenceScorer.ReferencesInstallDir(RegistryUtil.GetString(s, ""), _fp)
                        && !ConfidenceScorer.ReferencesInstallDir(RegistryUtil.GetString(s, "CodeBase"), _fp)) continue;
                    AddKey($@"{classes}\CLSID\{clsid}", "Registry: COM components", 95, $"COM class implemented by a file in the program folder ({server})");
                    _removedClsids.Add(clsid);
                    break;
                }
            }

            using var typeLibRoot = RegistryUtil.Open(classes + @"\TypeLib");
            var removedTypeLibs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (typeLibRoot is not null)
            {
                foreach (var lib in typeLibRoot.GetSubKeyNames())
                {
                    _ct.ThrowIfCancellationRequested();
                    if (TypeLibPointsIntoInstallDir(typeLibRoot, lib))
                    {
                        AddKey($@"{classes}\TypeLib\{lib}", "Registry: COM components", 90, "Type library stored in the program folder");
                        removedTypeLibs.Add(lib);
                    }
                }
            }

            if (_removedClsids.Count == 0 && removedTypeLibs.Count == 0) continue;
            using var ifaceRoot = RegistryUtil.Open(classes + @"\Interface");
            if (ifaceRoot is not null)
            {
                foreach (var iid in ifaceRoot.GetSubKeyNames())
                {
                    _ct.ThrowIfCancellationRequested();
                    using var k = ifaceRoot.OpenSubKey(iid);
                    if (k is null) continue;
                    using var ps = k.OpenSubKey("ProxyStubClsid32");
                    using var tl = k.OpenSubKey("TypeLib");
                    var proxy = ps is null ? null : RegistryUtil.GetString(ps, "");
                    var lib = tl is null ? null : RegistryUtil.GetString(tl, "");
                    if ((lib is not null && removedTypeLibs.Contains(lib)) || (proxy is not null && _removedClsids.Contains(proxy)))
                        AddKey($@"{classes}\Interface\{iid}", "Registry: COM components", 80, "Interface registered by the program's COM components");
                }
            }

            // AppIDs and ProgIDs that point to removed classes.
            using var root = RegistryUtil.Open(classes);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.StartsWith('.') || name.StartsWith('{')) continue;
                using var c = root.OpenSubKey(name + @"\CLSID");
                var target = c is null ? null : RegistryUtil.GetString(c, "");
                if (target is not null && _removedClsids.Contains(target))
                    AddKey($@"{classes}\{name}", "Registry: COM components", 85, "ProgID for one of the program's COM classes");
            }
        }
        ScanShellExtensions();
    }

    private bool TypeLibPointsIntoInstallDir(RegistryKey typeLibRoot, string lib)
    {
        using var libKey = typeLibRoot.OpenSubKey(lib);
        if (libKey is null) return false;
        foreach (var ver in libKey.GetSubKeyNames())
        {
            using var v = libKey.OpenSubKey(ver);
            if (v is null) continue;
            foreach (var lcid in v.GetSubKeyNames())
            {
                if (lcid is "FLAGS" or "HELPDIR") continue;
                using var l = v.OpenSubKey(lcid);
                if (l is null) continue;
                foreach (var plat in l.GetSubKeyNames())
                {
                    using var p = l.OpenSubKey(plat);
                    if (p is not null && ConfidenceScorer.ReferencesInstallDir(RegistryUtil.GetString(p, ""), _fp)) return true;
                }
            }
        }
        return false;
    }

    private void ScanShellExtensions()
    {
        if (_removedClsids.Count == 0) return;
        var handlerParents = new[]
        {
            @"*\shellex\ContextMenuHandlers", @"*\shellex\PropertySheetHandlers", @"Directory\shellex\ContextMenuHandlers",
            @"Directory\Background\shellex\ContextMenuHandlers", @"Folder\shellex\ContextMenuHandlers", @"Drive\shellex\ContextMenuHandlers",
            @"AllFilesystemObjects\shellex\ContextMenuHandlers", @"Directory\shellex\DragDropHandlers", @"Folder\shellex\DragDropHandlers",
            @"Directory\shellex\CopyHookHandlers",
        };
        foreach (var classes in ClassesRoots)
        {
            foreach (var parent in handlerParents)
            {
                var parentPath = $@"{classes}\{parent}";
                using var k = RegistryUtil.Open(parentPath);
                if (k is null) continue;
                foreach (var handler in k.GetSubKeyNames())
                {
                    using var h = k.OpenSubKey(handler);
                    var value = h is null ? null : RegistryUtil.GetString(h, "");
                    if (_removedClsids.Contains(handler) || (value is not null && _removedClsids.Contains(value)))
                        AddKey($@"{parentPath}\{handler}", "Registry: shell extensions", 90, "Explorer extension provided by the program");
                }
            }
        }

        foreach (var keyPath in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers",
                 })
        {
            using var k = RegistryUtil.Open(keyPath);
            if (k is null) continue;
            foreach (var name in k.GetSubKeyNames())
            {
                using var o = k.OpenSubKey(name);
                var value = o is null ? null : RegistryUtil.GetString(o, "");
                if (value is not null && _removedClsids.Contains(value))
                    AddKey($@"{keyPath}\{name}", "Registry: shell extensions", 90, "Explorer icon overlay provided by the program");
            }
        }

        foreach (var approved in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
                     @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached",
                 })
        {
            using var k = RegistryUtil.Open(approved);
            if (k is null) continue;
            foreach (var v in k.GetValueNames().Where(v => _removedClsids.Any(c => v.StartsWith(c, StringComparison.OrdinalIgnoreCase))))
                AddValue(approved, v, "Registry: shell extensions", 85, "Approval record for the program's Explorer extension");
        }
    }

    // ------------------------------------------------------------------ drivers

    private void ScanDrivers()
    {
        foreach (var svc in Services.List().Where(s => s.Type is 1 or 2))
        {
            _ct.ThrowIfCancellationRequested();
            var file = svc.ImagePath is null ? null : PathUtil.Clean(svc.ImagePath);
            var inInstallDir = ConfidenceScorer.ReferencesInstallDir(svc.ImagePath, _fp);
            var score = ConfidenceScorer.ScoreName(svc.Name, _fp);
            var fileScore = file is null ? Score.None : ConfidenceScorer.ScoreName(Path.GetFileNameWithoutExtension(file), _fp);
            var best = Math.Max(score.Value, fileScore.Value);
            if (inInstallDir)
                AddObject(LeftoverKind.Service, svc.Name, "Drivers", 90, "Driver loaded from the program folder", svc.DisplayName);
            else if (best >= 70 && (file is null || !File.Exists(file)))
                AddObject(LeftoverKind.Service, svc.Name, "Drivers", best, "Driver entry whose file is gone; " + (score.IsMatch ? score.Reason : fileScore.Reason), svc.DisplayName);
            else if (best >= 78 && file is not null && File.Exists(file))
            {
                AddObject(LeftoverKind.Service, svc.Name, "Drivers", 60, "Driver named after the program (it is still present, so review it)", svc.DisplayName);
            }
        }
    }

    // ------------------------------------------------------------------ deep sweep

    private static readonly string[] SweepSkip =
    [
        "Classes", @"Microsoft\Windows\CurrentVersion\Installer", @"Microsoft\Windows NT\CurrentVersion\Perflib",
        @"Microsoft\Windows\CurrentVersion\Component Based Servicing", @"Microsoft\Windows\CurrentVersion\Appx",
        @"Microsoft\Windows\CurrentVersion\AppModel", @"Microsoft\Windows\CurrentVersion\WINEVT", @"Microsoft\Windows\CurrentVersion\SideBySide",
        @"Microsoft\Windows Defender", @"Microsoft\Windows Search", @"Microsoft\Speech", @"Microsoft\Speech_OneCore",
        @"Microsoft\Windows\CurrentVersion\Reliability", @"Microsoft\Windows NT\CurrentVersion\Fonts", @"Microsoft\SystemCertificates",
        @"Microsoft\EnterpriseCertificates", @"Microsoft\Cryptography", @"Microsoft\Windows\CurrentVersion\Setup",
        @"Microsoft\Windows\CurrentVersion\DeliveryOptimization", @"Microsoft\Windows NT\CurrentVersion\Superfetch",
        @"Microsoft\Windows\CurrentVersion\CloudStore", @"Microsoft\Windows\CurrentVersion\Explorer\Streams",
        @"Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", @"Microsoft\Windows\Shell\BagMRU", @"Microsoft\Windows\Shell\Bags",
        @"Microsoft\Office\ClickToRun", @"Microsoft\Windows\CurrentVersion\Uninstall", @"Microsoft\Windows\CurrentVersion\Run",
        @"Microsoft\Windows\CurrentVersion\RunOnce", @"Microsoft\Windows\CurrentVersion\SharedDLLs", @"Microsoft\Windows\CurrentVersion\App Paths",
    ];

    /// <summary>
    /// Walks HKLM\SOFTWARE and HKCU\Software looking for any value that points into the program folder.
    /// This is what catches settings stored under unrelated-looking keys.
    /// </summary>
    private void ReferenceSweep()
    {
        if (_fp.InstallDir is null || _fp.InstallDir.Length < 8) return;
        var roots = new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node", @"HKEY_CURRENT_USER\Software" };
        var visited = 0;
        foreach (var rootPath in roots)
        {
            using var root = RegistryUtil.Open(rootPath);
            if (root is null) continue;
            Sweep(root, rootPath, "", 0, ref visited);
        }
    }

    private void Sweep(RegistryKey key, string rootPath, string relative, int depth, ref int visited)
    {
        _ct.ThrowIfCancellationRequested();
        if (++visited % 5000 == 0) _progress?.Report($"Deep registry reference sweep ({visited:N0} keys checked)");
        var fullPath = relative.Length == 0 ? rootPath : $@"{rootPath}\{relative}";

        string[] valueNames;
        try { valueNames = key.GetValueNames(); } catch { return; }
        var hits = new List<string>();
        foreach (var v in valueNames)
        {
            object? data;
            try { data = key.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames); } catch { continue; }
            var text = data switch { string s => s, string[] arr => string.Join("|", arr), _ => null };
            if (text is not null && text.Length >= _fp.InstallDir!.Length && ConfidenceScorer.ReferencesInstallDir(text, _fp)) hits.Add(v);
            else if (ConfidenceScorer.ReferencesInstallDir(v, _fp)) hits.Add(v);
        }
        if (hits.Count > 0 && relative.Length > 0)
        {
            var leaf = relative[(relative.LastIndexOf('\\') + 1)..];
            var nameScore = ConfidenceScorer.ScoreName(leaf, _fp);
            // Windows' own bookkeeping (tile backups, shell history...) only ever loses individual values.
            var windowsOwned = relative.StartsWith(@"Microsoft\", StringComparison.OrdinalIgnoreCase)
                               || relative.StartsWith(@"WOW6432Node\Microsoft\", StringComparison.OrdinalIgnoreCase);
            if (windowsOwned)
                foreach (var h in hits) AddValue(fullPath, h, "Usage traces", 60, "Windows record that points into the program folder");
            else if (nameScore.Value >= 55 || hits.Count == valueNames.Length && key.SubKeyCount == 0)
                AddKey(fullPath, "Registry: references", Math.Max(80, nameScore.Value), "Key whose data points into the program folder");
            else
                foreach (var h in hits) AddValue(fullPath, h, "Registry: references", 75, "Value points into the program folder");
        }

        if (depth >= 10) return;
        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { return; }
        foreach (var sub in subs)
        {
            var rel = relative.Length == 0 ? sub : $@"{relative}\{sub}";
            if (depth <= 4 && SweepSkip.Any(s => rel.Equals(s, StringComparison.OrdinalIgnoreCase))) continue;
            if (depth == 0 && rootPath.EndsWith("SOFTWARE") && sub.Equals("WOW6432Node", StringComparison.OrdinalIgnoreCase)) continue;
            RegistryKey? child;
            try { child = key.OpenSubKey(sub); } catch { continue; }
            if (child is null) continue;
            using (child) Sweep(child, rootPath, rel, depth + 1, ref visited);
        }
    }
}
