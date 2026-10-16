using System.Text.Json;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;
using Reclaim.Core.SystemTools;

namespace Reclaim.Core.Backup;

public enum BackupEntryKind { RegistryKey, RegistryValue, QuarantinedPath, RecycledPath, DeletedPath, ScheduledTask, Service, FirewallRule, EnvironmentPath }

public sealed class BackupEntry
{
    public BackupEntryKind Kind { get; set; }
    public string OriginalPath { get; set; } = "";
    public string? ValueName { get; set; }
    public string? BackupFile { get; set; }
    public string? Note { get; set; }
    public long SizeBytes { get; set; }
}

public sealed class BackupManifest
{
    public string Title { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public bool Restored { get; set; }
    public List<BackupEntry> Entries { get; set; } = [];
}

/// <summary>
/// One undoable operation. Everything removed during an uninstall or cleanup is recorded here first,
/// so it can be put back from the Backups page.
/// </summary>
public sealed class BackupSession
{
    public string Folder { get; }
    public BackupManifest Manifest { get; }
    private int _counter;

    private BackupSession(string folder, BackupManifest manifest)
    {
        Folder = folder;
        Manifest = manifest;
        _counter = manifest.Entries.Count;
    }

    public static BackupSession Create(string title)
    {
        var safe = string.Concat(title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
        if (safe.Length > 40) safe = safe[..40];
        var folder = Path.Combine(AppPaths.Backups, $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe}");
        Directory.CreateDirectory(folder);
        var session = new BackupSession(folder, new BackupManifest { Title = title });
        session.Save();
        return session;
    }

    public static BackupSession? Open(string folder)
    {
        var file = Path.Combine(folder, "manifest.json");
        if (!File.Exists(file)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file), SettingsStore.JsonOptions);
            return m is null ? null : new BackupSession(folder, m);
        }
        catch { return null; }
    }

    public long TotalSize => Manifest.Entries.Sum(e => e.SizeBytes);

    private string NextFile(string ext) => Path.Combine(Folder, $"{Interlocked.Increment(ref _counter):0000}{ext}");

    private void Add(BackupEntry e)
    {
        lock (Manifest) Manifest.Entries.Add(e);
        Save();
    }

    public void Save()
    {
        lock (Manifest)
            File.WriteAllText(Path.Combine(Folder, "manifest.json"), JsonSerializer.Serialize(Manifest, SettingsStore.JsonOptions));
    }

    // ------------------------------------------------------------------ removal with backup

    /// <summary>Removes one leftover after recording how to restore it. Returns true when it is gone.</summary>
    public bool Remove(LeftoverItem item, DeleteMode mode)
    {
        switch (item.Kind)
        {
            case LeftoverKind.Directory or LeftoverKind.File:
                return RemovePath(item.Path, mode, item.SizeBytes);

            case LeftoverKind.RegistryKey:
            {
                var reg = NextFile(".reg");
                if (!RegistryUtil.Export(item.Path, reg)) return !RegistryUtil.KeyExists(item.Path);
                Add(new BackupEntry { Kind = BackupEntryKind.RegistryKey, OriginalPath = item.Path, BackupFile = reg });
                RegistryUtil.DeleteKeyTree(item.Path);
                return !RegistryUtil.KeyExists(item.Path);
            }

            case LeftoverKind.RegistryValue:
            {
                var reg = NextFile(".reg");
                if (RegistryUtil.ExportValue(item.Path, item.ValueName ?? "", reg))
                    Add(new BackupEntry { Kind = BackupEntryKind.RegistryValue, OriginalPath = item.Path, ValueName = item.ValueName, BackupFile = reg });
                RegistryUtil.DeleteValue(item.Path, item.ValueName ?? "");
                return !RegistryUtil.ValueExists(item.Path, item.ValueName ?? "");
            }

            case LeftoverKind.Service:
            {
                var reg = NextFile(".reg");
                RegistryUtil.Export($@"{Services.ServicesKey}\{item.Path}", reg);
                Add(new BackupEntry { Kind = BackupEntryKind.Service, OriginalPath = item.Path, BackupFile = reg, Note = item.ValueName });
                return Services.StopAndDelete(item.Path);
            }

            case LeftoverKind.ScheduledTask:
            {
                var xml = ScheduledTasks.ExportXml(item.Path);
                string? file = null;
                if (xml is not null)
                {
                    file = NextFile(".xml");
                    File.WriteAllText(file, xml);
                }
                Add(new BackupEntry { Kind = BackupEntryKind.ScheduledTask, OriginalPath = item.Path, BackupFile = file });
                return ScheduledTasks.Delete(item.Path);
            }

            case LeftoverKind.FirewallRule:
            {
                var rules = FirewallRules.List().Where(r => r.Name == item.Path && PathUtil.SamePath(r.ApplicationPath, item.ValueName)).ToList();
                foreach (var r in rules)
                    Add(new BackupEntry { Kind = BackupEntryKind.FirewallRule, OriginalPath = r.Name, ValueName = r.ApplicationPath, Note = r.Direction.ToString() });
                return FirewallRules.Remove(item.Path, item.ValueName ?? "") > 0 || rules.Count == 0;
            }

            case LeftoverKind.EnvironmentPath:
            {
                var reg = NextFile(".reg");
                RegistryUtil.ExportValue(item.Path, "Path", reg);
                Add(new BackupEntry { Kind = BackupEntryKind.EnvironmentPath, OriginalPath = item.Path, ValueName = item.ValueName, BackupFile = reg });
                return EnvironmentPaths.RemoveSegment(item.Path, item.ValueName ?? "");
            }
        }
        return false;
    }

    public bool RemovePath(string path, DeleteMode mode, long size = 0)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return true;
        if (size == 0) size = FileOps.SizeOf(path);
        switch (mode)
        {
            case DeleteMode.Quarantine:
                var dest = Path.Combine(Folder, "files", Interlocked.Increment(ref _counter).ToString("0000"), Path.GetFileName(path.TrimEnd('\\')));
                try
                {
                    FileOps.Move(path, dest);
                    Add(new BackupEntry { Kind = BackupEntryKind.QuarantinedPath, OriginalPath = path, BackupFile = dest, SizeBytes = size });
                    return true;
                }
                catch (Exception ex)
                {
                    // In-use files: copy what we can for the record, then schedule removal at reboot.
                    Log.Warn($"Quarantine move failed for {path}: {ex.Message}");
                    if (Directory.Exists(dest) || File.Exists(dest))
                        Add(new BackupEntry { Kind = BackupEntryKind.QuarantinedPath, OriginalPath = path, BackupFile = dest, SizeBytes = size, Note = "partial" });
                    FileOps.DeletePermanently(path);
                    return !File.Exists(path) && !Directory.Exists(path);
                }
            case DeleteMode.RecycleBin:
                Add(new BackupEntry { Kind = BackupEntryKind.RecycledPath, OriginalPath = path, SizeBytes = size, Note = "Restore from the Recycle Bin" });
                return FileOps.SendToRecycleBin(path) || FileOps.DeletePermanently(path);
            default:
                Add(new BackupEntry { Kind = BackupEntryKind.DeletedPath, OriginalPath = path, SizeBytes = size, Note = "Permanently deleted" });
                return FileOps.DeletePermanently(path);
        }
    }

    // ------------------------------------------------------------------ restore

    public (int Restored, int Failed) Restore(IProgress<string>? log = null)
    {
        int ok = 0, failed = 0;
        foreach (var e in Manifest.Entries.AsEnumerable().Reverse())
        {
            try
            {
                var done = e.Kind switch
                {
                    BackupEntryKind.RegistryKey or BackupEntryKind.RegistryValue or BackupEntryKind.EnvironmentPath or BackupEntryKind.Service
                        => e.BackupFile is not null && RegistryUtil.Import(e.BackupFile),
                    BackupEntryKind.QuarantinedPath => RestoreFile(e),
                    BackupEntryKind.ScheduledTask => e.BackupFile is not null && ScheduledTasks.ImportXml(e.OriginalPath, e.BackupFile),
                    BackupEntryKind.FirewallRule => FirewallRules.Add(e.OriginalPath, e.ValueName ?? "", int.TryParse(e.Note, out var d) ? d : 1),
                    _ => false,
                };
                if (done) ok++;
                else if (e.Kind is not (BackupEntryKind.RecycledPath or BackupEntryKind.DeletedPath)) failed++;
                log?.Report($"{(done ? "Restored" : "Skipped")}: {e.OriginalPath}");
            }
            catch (Exception ex)
            {
                failed++;
                log?.Report($"Failed: {e.OriginalPath} ({ex.Message})");
            }
        }
        Manifest.Restored = true;
        Save();
        return (ok, failed);
    }

    private static bool RestoreFile(BackupEntry e)
    {
        if (e.BackupFile is null || (!File.Exists(e.BackupFile) && !Directory.Exists(e.BackupFile))) return false;
        if (File.Exists(e.OriginalPath) || Directory.Exists(e.OriginalPath)) return false;
        FileOps.Move(e.BackupFile, e.OriginalPath);
        return true;
    }
}
