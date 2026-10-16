using System.Diagnostics;
using System.Text.Json;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.SystemTools;

public sealed class RelocationRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AppName { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string NewPath { get; set; } = "";
    public DateTime Moved { get; set; } = DateTime.Now;
    public List<string> StoppedServices { get; set; } = [];
}

/// <summary>
/// Moves an installed program to another drive and leaves a directory junction at the old location,
/// so registry entries, shortcuts and services keep working. Fully reversible.
/// </summary>
public static class SoftwareRelocator
{
    public static string? CanRelocate(InstalledApp app)
    {
        if (app.Source is not (AppSource.Win32 or AppSource.Steam or AppSource.Scoop)) return "Only desktop programs can be moved.";
        var loc = app.InstallLocation;
        if (string.IsNullOrEmpty(loc) || !Directory.Exists(loc)) return "The program's install folder is unknown.";
        if (Leftovers.ProtectionGuard.IsProtectedPath(loc)) return "This folder is shared or part of Windows.";
        if (new DirectoryInfo(loc).Attributes.HasFlag(FileAttributes.ReparsePoint)) return "This program has already been moved.";
        return null;
    }

    public static async Task<RelocationRecord> MoveAsync(InstalledApp app, string targetRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var reason = CanRelocate(app);
        if (reason is not null) throw new InvalidOperationException(reason);
        var source = app.InstallLocation!.TrimEnd('\\');
        var target = Path.Combine(targetRoot, Path.GetFileName(source));
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different drive.");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new InvalidOperationException($"{target} already exists and is not empty.");

        var size = FileOps.DirectorySize(source, ct);
        if (FileOps.FreeSpace(targetRoot) < (ulong)(size * 1.1))
            throw new InvalidOperationException($"Not enough free space on {Path.GetPathRoot(targetRoot)} ({Format.Bytes(size)} needed).");

        var record = new RelocationRecord { AppName = app.Name, OriginalPath = source, NewPath = target };
        await Task.Run(() =>
        {
            progress?.Report("Closing the program…");
            Uninstall.UninstallRunner.StopRunningProcesses(app, progress);
            record.StoppedServices = StopServicesIn(source, progress);

            progress?.Report($"Copying {Format.Bytes(size)} to {target}…");
            FileOps.CopyDirectory(source, target, new Progress<string>(f => progress?.Report($"Copying {Path.GetFileName(f)}")), ct);

            progress?.Report("Verifying the copy…");
            var copied = FileOps.DirectorySize(target, ct);
            if (copied < size) throw new IOException($"Copy incomplete ({Format.Bytes(copied)} of {Format.Bytes(size)}). The original was left untouched.");

            progress?.Report("Linking the old location to the new one…");
            // Rename first: it is atomic and fails cleanly if anything is still locked.
            var parked = source + ".reclaim-moving";
            try
            {
                Directory.Move(source, parked);
            }
            catch (Exception ex)
            {
                FileOps.DeletePermanently(target);
                throw new IOException($"Some files are still in use, so nothing was moved ({ex.Message}).");
            }
            try
            {
                CreateJunction(source, target);
            }
            catch
            {
                Directory.Move(parked, source);
                FileOps.DeletePermanently(target);
                throw;
            }
            FileOps.DeletePermanently(parked);

            Save(record);
            foreach (var s in record.StoppedServices) ProcessRunner.Run("sc.exe", $"start \"{s}\"", TimeSpan.FromSeconds(30));
        }, ct);
        Log.Info($"Relocated {app.Name}: {source} → {target}");
        return record;
    }

    public static async Task UndoAsync(RelocationRecord record, IProgress<string>? progress, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(record.NewPath)) throw new DirectoryNotFoundException(record.NewPath);
            var stopped = StopServicesIn(record.OriginalPath, progress);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = Native.ProcessImagePath((uint)p.Id);
                    if (path is not null && (PathUtil.IsUnder(path, record.NewPath) || PathUtil.IsUnder(path, record.OriginalPath))) p.Kill(true);
                }
                catch { }
            }
            progress?.Report("Removing the link…");
            if (Directory.Exists(record.OriginalPath)) Directory.Delete(record.OriginalPath); // junction only
            progress?.Report("Moving files back…");
            FileOps.CopyDirectory(record.NewPath, record.OriginalPath, null, ct);
            Directory.Delete(record.NewPath, recursive: true);
            File.Delete(Path.Combine(AppPaths.Relocations, record.Id + ".json"));
            foreach (var s in stopped) ProcessRunner.Run("sc.exe", $"start \"{s}\"", TimeSpan.FromSeconds(30));
        }, ct);
    }

    public static List<RelocationRecord> List()
    {
        var list = new List<RelocationRecord>();
        foreach (var f in Directory.EnumerateFiles(AppPaths.Relocations, "*.json"))
        {
            try { if (JsonSerializer.Deserialize<RelocationRecord>(File.ReadAllText(f)) is { } r) list.Add(r); } catch { }
        }
        return list;
    }

    public static void MarkRelocated(IEnumerable<InstalledApp> apps)
    {
        List<RelocationRecord> records;
        try { records = List(); } catch { return; }
        foreach (var app in apps)
        {
            var r = records.FirstOrDefault(r => PathUtil.SamePath(r.OriginalPath, app.InstallLocation));
            if (r is null) continue;
            app.Flags |= AppFlags.Relocated;
            app.Extra["RelocatedTo"] = r.NewPath;
            app.Extra["RelocationId"] = r.Id;
        }
    }

    private static void Save(RelocationRecord r) =>
        File.WriteAllText(Path.Combine(AppPaths.Relocations, r.Id + ".json"), JsonSerializer.Serialize(r));

    internal static void CreateJunction(string link, string target)
    {
        var r = ProcessRunner.Run("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"", TimeSpan.FromSeconds(30));
        if (r.ExitCode != 0) throw new IOException($"Could not create the link: {r.StdErr}{r.StdOut}");
    }

    private static List<string> StopServicesIn(string folder, IProgress<string>? progress)
    {
        var stopped = new List<string>();
        foreach (var svc in Services.List().Where(s => s.ImagePath is not null && s.ImagePath.Contains(folder, StringComparison.OrdinalIgnoreCase)))
        {
            progress?.Report($"Stopping service {svc.DisplayName}…");
            var r = ProcessRunner.Run("sc.exe", $"stop \"{svc.Name}\"", TimeSpan.FromSeconds(30));
            if (r.ExitCode == 0) stopped.Add(svc.Name);
        }
        if (stopped.Count > 0) Thread.Sleep(2000);
        return stopped;
    }
}
