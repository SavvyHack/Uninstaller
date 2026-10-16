using Microsoft.Win32;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Leftovers;

public sealed partial class LeftoverScanner
{
    // ------------------------------------------------------------------ folders

    private void ScanFolders(bool deep)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new List<(string Path, string Category, int Cap)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program files", 100),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program files", 100),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "Program files", 90),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), "Program files", 90),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Shared app data", 100),
            (roaming, "User app data", 100),
            (local, "User app data", 100),
            (Path.Combine(Directory.GetParent(local)!.FullName, "LocalLow"), "User app data", 100),
            (Path.Combine(local, "Programs"), "Program files", 100),
            (Path.GetTempPath(), "Temporary files", 100),
        };
        if (deep)
        {
            roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents (review carefully)", 50));
            roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "Documents (review carefully)", 50));
            roots.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games"), "Documents (review carefully)", 50));
            roots.Add((Path.Combine(local, "VirtualStore", "Program Files"), "Program files", 95));
            roots.Add((Path.Combine(local, "VirtualStore", "Program Files (x86)"), "Program files", 95));
            roots.Add((Path.Combine(local, "VirtualStore", "ProgramData"), "Shared app data", 95));
        }

        foreach (var (root, category, cap) in roots.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in SafeDirs(root))
            {
                _ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(dir);
                var score = ConfidenceScorer.ScoreName(name, _fp);
                if (score.Value >= 55)
                {
                    AddPath(LeftoverKind.Directory, dir, category, Math.Min(cap, score.Value), score.Reason);
                    continue;
                }
                // Vendor folder: look one level down for the product, and offer the vendor folder too if it holds nothing else.
                var isPublisher = _fp.PublisherKey is { } pub && PathUtil.Squash(name) == pub;
                if (!isPublisher && !deep) continue;
                var children = SafeDirs(dir).ToList();
                var matched = 0;
                foreach (var child in children)
                {
                    var cs = ConfidenceScorer.ScoreName(Path.GetFileName(child), _fp);
                    if (cs.Value < 55) continue;
                    var bonus = isPublisher ? 10 : 0;
                    AddPath(LeftoverKind.Directory, child, category, Math.Min(cap, cs.Value + bonus), cs.Reason + (isPublisher ? " (inside the publisher's folder)" : ""));
                    matched++;
                }
                if (isPublisher && children.Count > 0 && matched == children.Count && SafeFiles(dir, "*", false).Count() <= 2)
                    AddPath(LeftoverKind.Directory, dir, category, Math.Min(cap, 70), "Publisher folder that contains only this program's data");
                else if (isPublisher && children.Count == 0 && !SafeFiles(dir, "*", true).Any())
                    AddPath(LeftoverKind.Directory, dir, category, Math.Min(cap, 65), "Empty publisher folder");
            }
        }

        // Prefetch traces are always safe to remove.
        var prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        foreach (var exe in _fp.ExeNames)
            foreach (var pf in SafeFiles(prefetch, Path.GetFileNameWithoutExtension(exe).ToUpperInvariant() + ".EXE-*.pf", false))
                AddPath(LeftoverKind.File, pf, "Usage traces", 85, "Windows Prefetch trace for the program", bypassGuard: true);
    }

    // ------------------------------------------------------------------ registry software keys

    private void ScanSoftwareKeys()
    {
        var roots = new[]
        {
            @"HKEY_LOCAL_MACHINE\SOFTWARE",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node",
            @"HKEY_CURRENT_USER\Software",
            @"HKEY_CURRENT_USER\Software\WOW6432Node",
            @"HKEY_CURRENT_USER\Software\AppDataLow\Software",
        };
        foreach (var rootPath in roots)
        {
            using var root = RegistryUtil.Open(rootPath);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                _ct.ThrowIfCancellationRequested();
                if (name is "Microsoft" or "Classes" or "Policies" or "WOW6432Node" or "Clients" or "RegisteredApplications") continue;
                var keyPath = $@"{rootPath}\{name}";
                var score = ConfidenceScorer.ScoreName(name, _fp);
                if (score.Value >= 55)
                {
                    AddKey(keyPath, "Registry: software settings", score.Value, score.Reason);
                    continue;
                }
                var isPublisher = _fp.PublisherKey is { } pub && PathUtil.Squash(name) == pub;
                if (!isPublisher) continue;
                using var vendor = root.OpenSubKey(name);
                if (vendor is null) continue;
                var subs = vendor.GetSubKeyNames();
                var matched = 0;
                foreach (var sub in subs)
                {
                    var cs = ConfidenceScorer.ScoreName(sub, _fp);
                    if (cs.Value < 55) continue;
                    AddKey($@"{keyPath}\{sub}", "Registry: software settings", Math.Min(100, cs.Value + 10), cs.Reason + " (under the publisher's key)");
                    matched++;
                }
                if (subs.Length > 0 && matched == subs.Length && vendor.ValueCount == 0)
                    AddKey(keyPath, "Registry: software settings", 70, "Publisher key that holds only this program's settings");
                else if (subs.Length == 0 && vendor.ValueCount == 0)
                    AddKey(keyPath, "Registry: software settings", 65, "Empty publisher key");
            }
        }
    }

    // ------------------------------------------------------------------ associations, App Paths, ProgIDs

    private void ScanAssociations()
    {
        foreach (var appPaths in new[]
                 {
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                     @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
                     @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths",
                 })
        {
            using var root = RegistryUtil.Open(appPaths);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                var target = k is null ? null : RegistryUtil.GetString(k, "");
                if (ConfidenceScorer.ReferencesInstallDir(target, _fp))
                    AddKey($@"{appPaths}\{name}", "Registry: app registration", 95, "App Paths entry for the program");
                else if (_fp.ExeNames.Contains(name) && (target is null || !File.Exists(PathUtil.Clean(target) ?? "")))
                    AddKey($@"{appPaths}\{name}", "Registry: app registration", 85, "App Paths entry for a missing executable");
            }
        }

        foreach (var classes in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes", @"HKEY_CURRENT_USER\Software\Classes" })
        {
            foreach (var exe in _fp.ExeNames)
                AddKey($@"{classes}\Applications\{exe}", "Registry: file associations", 85, "\"Open with\" registration for the program");

            using var root = RegistryUtil.Open(classes);
            if (root is null) continue;
            var names = root.GetSubKeyNames();
            // ProgIDs whose open command runs from the install folder.
            foreach (var name in names)
            {
                _ct.ThrowIfCancellationRequested();
                if (name.StartsWith('.') || name.StartsWith('{') || name is "CLSID" or "Interface" or "TypeLib" or "Installer" or "AppID" or "Applications") continue;
                using var cmd = root.OpenSubKey($@"{name}\shell\open\command") ?? root.OpenSubKey($@"{name}\DefaultIcon");
                if (cmd is null) continue;
                var data = RegistryUtil.GetString(cmd, "");
                if (!ConfidenceScorer.ReferencesInstallDir(data, _fp)) continue;
                AddKey($@"{classes}\{name}", "Registry: file associations", 90, "File type handled by the program");
                _removedProgIds.Add(name);
            }
            if (_removedProgIds.Count == 0) continue;
            // Extensions that point to those ProgIDs.
            foreach (var ext in names.Where(n => n.StartsWith('.')))
            {
                using var k = root.OpenSubKey(ext);
                if (k is null) continue;
                var def = RegistryUtil.GetString(k, "");
                if (def is not null && _removedProgIds.Contains(def))
                {
                    var onlyThis = k.SubKeyCount == 0 || (k.SubKeyCount == 1 && k.GetSubKeyNames()[0].Equals("OpenWithProgids", StringComparison.OrdinalIgnoreCase));
                    if (onlyThis && k.ValueCount <= 2) AddKey($@"{classes}\{ext}", "Registry: file associations", 75, $"Extension used only by {def}");
                    else AddValue($@"{classes}\{ext}", "", "Registry: file associations", 70, $"Default handler {def} belongs to the program");
                }
                using var owp = k.OpenSubKey("OpenWithProgids");
                if (owp is null) continue;
                foreach (var v in owp.GetValueNames().Where(_removedProgIds.Contains))
                    AddValue($@"{classes}\{ext}\OpenWithProgids", v, "Registry: file associations", 90, "\"Open with\" entry for the program");
            }
        }

        // RegisteredApplications → capabilities key in the install's own software key.
        foreach (var reg in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\RegisteredApplications", @"HKEY_CURRENT_USER\Software\RegisteredApplications" })
        {
            using var k = RegistryUtil.Open(reg);
            if (k is null) continue;
            foreach (var v in k.GetValueNames())
            {
                var score = ConfidenceScorer.ScoreName(v, _fp);
                if (score.Value >= 70) AddValue(reg, v, "Registry: app registration", score.Value, "Default-apps registration: " + score.Reason);
            }
        }
    }

    // ------------------------------------------------------------------ usage traces, PATH, event log, SharedDLLs

    private void ScanTraces()
    {
        var valueStores = new (string Key, string Category, bool NameHoldsPath)[]
        {
            (@"HKEY_CURRENT_USER\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "Usage traces", true),
            (@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", "Usage traces", true),
            (@"HKEY_CURRENT_USER\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "Usage traces", true),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "Usage traces", true),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", "Registry: shared DLL counters", true),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs", "Registry: shared DLL counters", true),
            (@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\bam\State\UserSettings", "Usage traces", true),
        };
        foreach (var (keyPath, category, nameHoldsPath) in valueStores)
        {
            using var k = RegistryUtil.Open(keyPath);
            if (k is null) continue;
            ScanValues(k, keyPath, category, nameHoldsPath, depth: keyPath.EndsWith("UserSettings") ? 1 : 0);
        }

        // Event log sources registered by the program.
        foreach (var log in new[] { "Application", "System" })
        {
            var root = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\{log}";
            using var k = RegistryUtil.Open(root);
            if (k is null) continue;
            foreach (var src in k.GetSubKeyNames())
            {
                using var sk = k.OpenSubKey(src);
                var msg = sk is null ? null : RegistryUtil.GetString(sk, "EventMessageFile");
                if (ConfidenceScorer.ReferencesInstallDir(msg, _fp))
                    AddKey($@"{root}\{src}", "Registry: event log sources", 90, "Event log source whose messages live in the program folder");
                else
                {
                    var score = ConfidenceScorer.ScoreName(src, _fp);
                    if (score.Value >= 78) AddKey($@"{root}\{src}", "Registry: event log sources", score.Value - 10, "Event log source: " + score.Reason);
                }
            }
        }

        // PATH entries.
        foreach (var (keyPath, scope) in new[]
                 {
                     (@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "machine"),
                     (@"HKEY_CURRENT_USER\Environment", "user"),
                 })
        {
            using var k = RegistryUtil.Open(keyPath);
            var path = k?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (path is null) continue;
            foreach (var segment in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var expanded = Environment.ExpandEnvironmentVariables(segment);
                if (ConfidenceScorer.ReferencesInstallDir(expanded, _fp))
                    AddObject(LeftoverKind.EnvironmentPath, keyPath, "Environment", 90, $"{scope} PATH entry points into the program folder", segment);
                else if (_fp.InstallDir is null && !Directory.Exists(expanded) && ConfidenceScorer.ScoreName(Path.GetFileName(expanded.TrimEnd('\\')), _fp).Value >= 78)
                    AddObject(LeftoverKind.EnvironmentPath, keyPath, "Environment", 70, $"{scope} PATH entry for a missing folder named after the program", segment);
            }
        }
    }

    private void ScanValues(RegistryKey key, string keyPath, string category, bool nameHoldsPath, int depth)
    {
        foreach (var v in key.GetValueNames())
        {
            var hit = nameHoldsPath ? ConfidenceScorer.ReferencesInstallDir(v, _fp) : false;
            if (!hit) hit = ConfidenceScorer.ReferencesInstallDir(RegistryUtil.GetString(key, v), _fp);
            if (!hit && _fp.InstallDir is null)
            {
                var file = Path.GetFileName(v.Split(',')[0]);
                hit = _fp.ExeNames.Contains(file) && !File.Exists(PathUtil.Clean(v) ?? "");
            }
            if (hit) AddValue(keyPath, v, category, 88, "Record about the program's files");
        }
        if (depth <= 0) return;
        foreach (var sub in key.GetSubKeyNames())
        {
            using var sk = key.OpenSubKey(sub);
            if (sk is not null) ScanValues(sk, $@"{keyPath}\{sub}", category, nameHoldsPath, depth - 1);
        }
    }
}
