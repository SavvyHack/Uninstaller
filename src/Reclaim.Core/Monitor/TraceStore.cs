using System.Text.Json;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;
using Reclaim.Core.Models;

namespace Reclaim.Core.Monitor;

public static class TraceStore
{
    private static string FileFor(string id) => Path.Combine(AppPaths.Traces, id + ".json");

    public static void Save(TraceLog log) =>
        File.WriteAllText(FileFor(log.Id), JsonSerializer.Serialize(log, SettingsStore.JsonOptions));

    public static List<TraceLog> List()
    {
        var list = new List<TraceLog>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.Traces, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<TraceLog>(File.ReadAllText(f), SettingsStore.JsonOptions) is { } log) list.Add(log);
            }
            catch { }
        }
        return list.OrderByDescending(l => l.Started).ToList();
    }

    public static void Delete(string id)
    {
        try { File.Delete(FileFor(id)); } catch { }
    }

    public static void Export(TraceLog log, string destination) =>
        File.WriteAllText(destination, JsonSerializer.Serialize(log, SettingsStore.JsonOptions));

    /// <summary>Imports a trace exported on another PC; paths are kept as-is.</summary>
    public static TraceLog Import(string file)
    {
        var log = JsonSerializer.Deserialize<TraceLog>(File.ReadAllText(file), SettingsStore.JsonOptions)
                  ?? throw new InvalidDataException("Not a Reclaim trace file.");
        log.Id = Guid.NewGuid().ToString("N");
        log.Imported = true;
        Save(log);
        return log;
    }

    public static TraceLog? FindFor(InstalledApp app)
    {
        var traces = List();
        if (app.Extra.TryGetValue("TraceId", out var id)) return traces.FirstOrDefault(t => t.Id == id);
        return traces.FirstOrDefault(t => app.RegistryKeyPath is not null && t.LinkedUninstallKeys.Contains(app.RegistryKeyPath, StringComparer.OrdinalIgnoreCase))
               ?? traces.FirstOrDefault(t => t.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static void MarkTracedApps(IEnumerable<InstalledApp> apps)
    {
        List<TraceLog> traces;
        try { traces = List(); } catch { return; }
        if (traces.Count == 0) return;
        foreach (var app in apps.Where(a => a.Source == AppSource.Win32))
        {
            var t = traces.FirstOrDefault(t => app.RegistryKeyPath is not null && t.LinkedUninstallKeys.Contains(app.RegistryKeyPath, StringComparer.OrdinalIgnoreCase));
            if (t is null) continue;
            app.Flags |= AppFlags.Traced;
            app.Extra["TraceId"] = t.Id;
        }
    }

    /// <summary>A trace becomes a list of Certain leftovers (everything the installer created).</summary>
    public static List<LeftoverItem> ToLeftovers(TraceLog log, ProtectionGuard guard)
    {
        var items = new List<LeftoverItem>();
        void Add(LeftoverItem i, string reason)
        {
            i.Raise(95, reason);
            items.Add(i);
        }

        foreach (var d in log.Directories.Where(Directory.Exists).Where(d => guard.WhyProtectedPath(d) is null))
            Add(new LeftoverItem { Kind = LeftoverKind.Directory, Path = d, Category = "Traced: folders", SizeBytes = FileOps.DirectorySize(d) }, "Created during installation");
        foreach (var f in log.Files.Where(File.Exists).Where(f => guard.WhyProtectedPath(f) is null || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            Add(new LeftoverItem { Kind = LeftoverKind.File, Path = f, Category = "Traced: files", SizeBytes = FileOps.SizeOf(f) }, "Created during installation");
        foreach (var k in log.RegistryKeys.Where(RegistryUtil.KeyExists).Where(k => guard.WhyProtectedKey(k) is null))
            Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = k, Category = "Traced: registry keys" }, "Created during installation");
        foreach (var v in log.RegistryValues.Where(v => RegistryUtil.ValueExists(v.Key, v.Name)))
            Add(new LeftoverItem { Kind = LeftoverKind.RegistryValue, Path = v.Key, ValueName = v.Name, Category = "Traced: registry values" }, "Added during installation");
        foreach (var s in log.Services)
            Add(new LeftoverItem { Kind = LeftoverKind.Service, Path = s, Category = "Traced: services" }, "Service created during installation");
        foreach (var t in log.ScheduledTasks)
            Add(new LeftoverItem { Kind = LeftoverKind.ScheduledTask, Path = t, Category = "Traced: scheduled tasks" }, "Task created during installation");
        foreach (var r in log.FirewallRules)
        {
            var parts = r.Split('|', 2);
            Add(new LeftoverItem { Kind = LeftoverKind.FirewallRule, Path = parts[0], ValueName = parts.ElementAtOrDefault(1), Category = "Traced: firewall rules" }, "Firewall rule created during installation");
        }
        return items;
    }
}
