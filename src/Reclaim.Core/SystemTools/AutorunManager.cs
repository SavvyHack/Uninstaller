using System.Diagnostics;
using Microsoft.Win32;
using Reclaim.Core.Backup;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Leftovers;

namespace Reclaim.Core.SystemTools;

public enum AutorunKind { RegistryRun, RegistryRunOnce, StartupFolder, ScheduledTask, Service }

public sealed class AutorunEntry
{
    public required string Name { get; init; }
    public required AutorunKind Kind { get; init; }
    public required string Location { get; init; }
    public string? Command { get; init; }
    public string? ExePath { get; init; }
    public bool AllUsers { get; init; }
    public bool Enabled { get; set; }
    public string? Publisher { get; set; }
    public string? Description { get; set; }
    public bool IsRunning { get; set; }
    public bool IsValid => ExePath is null || File.Exists(ExePath);
    public SignatureInfo? Signature { get; set; }

    // Kind-specific handles
    public string? RegistryKey { get; init; }
    public string? FilePath { get; init; }
    public string? ApprovedKey { get; init; }

    public string KindText => Kind switch
    {
        AutorunKind.RegistryRun => "Registry (Run)",
        AutorunKind.RegistryRunOnce => "Registry (RunOnce)",
        AutorunKind.StartupFolder => "Startup folder",
        AutorunKind.ScheduledTask => "Scheduled task",
        _ => "Service",
    };
}

/// <summary>Lists and controls everything that starts with Windows. Uses the same StartupApproved data as Task Manager.</summary>
public static class AutorunManager
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<AutorunEntry> List(bool includeServices = true)
    {
        var running = RunningExecutables();
        var list = new List<AutorunEntry>();

        var runKeys = new (string Key, AutorunKind Kind, bool AllUsers, string? ApprovedKey)[]
        {
            (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", AutorunKind.RegistryRun, false, $@"HKEY_CURRENT_USER\{Approved}\Run"),
            (@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\RunOnce", AutorunKind.RegistryRunOnce, false, null),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", AutorunKind.RegistryRun, true, $@"HKEY_LOCAL_MACHINE\{Approved}\Run"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", AutorunKind.RegistryRunOnce, true, null),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", AutorunKind.RegistryRun, true, $@"HKEY_LOCAL_MACHINE\{Approved}\Run32"),
            (@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", AutorunKind.RegistryRunOnce, true, null),
        };
        foreach (var (keyPath, kind, allUsers, approved) in runKeys)
        {
            using var k = RegistryUtil.Open(keyPath);
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                var cmd = RegistryUtil.GetString(k, name);
                var exe = PathUtil.ExecutableFromCommand(cmd);
                list.Add(Describe(new AutorunEntry
                {
                    Name = string.IsNullOrEmpty(name) ? "(Default)" : name,
                    Kind = kind,
                    Location = keyPath,
                    Command = cmd,
                    ExePath = exe,
                    AllUsers = allUsers,
                    RegistryKey = keyPath,
                    ApprovedKey = approved,
                    Enabled = approved is null || IsApproved(approved, name),
                }, running));
            }
        }

        foreach (var (folder, allUsers) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), false),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), true),
                 })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.GetTarget(file) : file;
                var approved = $@"{(allUsers ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER")}\{Approved}\StartupFolder";
                list.Add(Describe(new AutorunEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Kind = AutorunKind.StartupFolder,
                    Location = folder,
                    Command = target,
                    ExePath = target,
                    AllUsers = allUsers,
                    FilePath = file,
                    ApprovedKey = approved,
                    Enabled = IsApproved(approved, Path.GetFileName(file)),
                }, running));
            }
        }

        foreach (var t in ScheduledTasks.List().Where(t => t.RunsAtLogon))
        {
            list.Add(Describe(new AutorunEntry
            {
                Name = t.Name,
                Kind = AutorunKind.ScheduledTask,
                Location = "Task Scheduler" + t.Path[..Math.Max(1, t.Path.LastIndexOf('\\'))],
                Command = $"{t.Command} {t.Arguments}".Trim(),
                ExePath = t.Command,
                AllUsers = true,
                FilePath = t.Path,
                Enabled = t.Enabled,
            }, running));
        }

        if (includeServices)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (var s in Services.List().Where(s => s.Type is 0x10 or 0x20 && s.StartType is 2 or 4))
            {
                var exe = PathUtil.ExecutableFromCommand(s.ImagePath);
                if (exe is null || PathUtil.IsUnder(exe, windows)) continue; // Windows' own services are not listed
                list.Add(Describe(new AutorunEntry
                {
                    Name = s.DisplayName,
                    Kind = AutorunKind.Service,
                    Location = "Services",
                    Command = s.ImagePath,
                    ExePath = exe,
                    AllUsers = true,
                    RegistryKey = s.KeyPath,
                    FilePath = s.Name,
                    Enabled = s.StartType == 2,
                }, running));
            }
        }
        return list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static AutorunEntry Describe(AutorunEntry e, HashSet<string> running)
    {
        if (e.ExePath is not null && File.Exists(e.ExePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(e.ExePath);
                e.Publisher = info.CompanyName;
                e.Description = info.FileDescription;
            }
            catch { }
            e.IsRunning = running.Contains(e.ExePath);
        }
        return e;
    }

    public static void LoadSignature(AutorunEntry e) => e.Signature ??= SignatureVerifier.Verify(e.ExePath);

    private static HashSet<string> RunningExecutables()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (Native.ProcessImagePath((uint)p.Id) is { } path) set.Add(path);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return set;
    }

    // ------------------------------------------------------------------ StartupApproved (Task Manager compatible)

    private static bool IsApproved(string approvedKey, string name)
    {
        using var k = RegistryUtil.Open(approvedKey);
        if (k?.GetValue(name) is not byte[] data || data.Length == 0) return true;
        return (data[0] & 1) == 0; // 02/06 enabled, 03/07 disabled
    }

    public static void SetEnabled(AutorunEntry e, bool enabled)
    {
        switch (e.Kind)
        {
            case AutorunKind.RegistryRun or AutorunKind.StartupFolder when e.ApprovedKey is not null:
            {
                var name = e.Kind == AutorunKind.StartupFolder ? Path.GetFileName(e.FilePath!) : (e.Name == "(Default)" ? "" : e.Name);
                var (hive, sub) = RegistryUtil.ParseHive(e.ApprovedKey);
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = root.CreateSubKey(sub);
                var data = new byte[12];
                data[0] = enabled ? (byte)2 : (byte)3;
                if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4);
                k.SetValue(name, data, RegistryValueKind.Binary);
                break;
            }
            case AutorunKind.ScheduledTask:
                ScheduledTasks.SetEnabled(e.FilePath!, enabled);
                break;
            case AutorunKind.Service:
                Services.SetStartType(e.FilePath!, enabled ? "auto" : "demand");
                break;
            default:
                throw new NotSupportedException("RunOnce entries can only be removed.");
        }
        e.Enabled = enabled;
    }

    /// <summary>Removes the startup entry (backed up so it can be restored from Backups).</summary>
    public static bool Delete(AutorunEntry e)
    {
        var session = BackupSession.Create($"Startup entry {e.Name}");
        var item = e.Kind switch
        {
            AutorunKind.RegistryRun or AutorunKind.RegistryRunOnce => new LeftoverItem
                { Kind = LeftoverKind.RegistryValue, Path = e.RegistryKey!, ValueName = e.Name == "(Default)" ? "" : e.Name, Category = "Startup" },
            AutorunKind.StartupFolder => new LeftoverItem { Kind = LeftoverKind.File, Path = e.FilePath!, Category = "Startup" },
            AutorunKind.ScheduledTask => new LeftoverItem { Kind = LeftoverKind.ScheduledTask, Path = e.FilePath!, Category = "Startup" },
            _ => null,
        };
        if (item is null) return false; // services are disabled, never deleted from here
        return session.Remove(item, DeleteMode.Quarantine);
    }

    public static void Add(string name, string command, AutorunKind kind, bool allUsers)
    {
        switch (kind)
        {
            case AutorunKind.RegistryRun or AutorunKind.RegistryRunOnce:
            {
                var sub = @"Software\Microsoft\Windows\CurrentVersion\" + (kind == AutorunKind.RegistryRun ? "Run" : "RunOnce");
                using var k = (allUsers ? Registry.LocalMachine : Registry.CurrentUser).CreateSubKey(sub);
                k.SetValue(name, command);
                break;
            }
            case AutorunKind.StartupFolder:
            {
                var folder = Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);
                var (exe, args) = CommandLine.Split(command);
                ShortcutResolver.Create(Path.Combine(folder, name + ".lnk"), exe, args);
                break;
            }
            default:
                throw new NotSupportedException();
        }
    }

    public static void Kill(AutorunEntry e)
    {
        if (e.ExePath is null) return;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e.ExePath)))
        {
            try
            {
                if (PathUtil.SamePath(Native.ProcessImagePath((uint)p.Id), e.ExePath)) p.Kill(true);
            }
            catch { }
        }
        e.IsRunning = false;
    }

    public static void Run(AutorunEntry e)
    {
        if (e.Command is null) return;
        var (exe, args) = CommandLine.Split(e.Command);
        ProcessRunner.OpenShell(exe, args);
    }
}
