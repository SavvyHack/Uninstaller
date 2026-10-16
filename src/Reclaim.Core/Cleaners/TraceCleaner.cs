using System.Diagnostics;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Cleaners;

/// <summary>Measures and cleans a set of <see cref="CleanerItem"/>s. Shared by the Windows, browser and Office cleaners.</summary>
public static class TraceCleaner
{
    public static Task AnalyzeAsync(IEnumerable<CleanerItem> items, CancellationToken ct) => Task.Run(() =>
    {
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            long size = 0;
            var count = 0;
            foreach (var t in item.Targets)
            {
                var (s, c) = Measure(t);
                size += s;
                count += c;
            }
            item.SizeBytes = size;
            item.Count = count;
            if (count == 0 && size == 0) item.Selected = false;
        }
    }, ct);

    public static Task<CleanerRunResult> CleanAsync(IEnumerable<CleanerItem> items, IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var cleaned = 0;
        long freed = 0;
        var skipped = new List<string>();
        foreach (var item in items.Where(i => i.Selected))
        {
            ct.ThrowIfCancellationRequested();
            if (item.ProcessName is not null && Process.GetProcessesByName(item.ProcessName).Length > 0)
            {
                skipped.Add($"{item.Group} · {item.Name}: close {item.Group.Split(" · ")[0]} first");
                continue;
            }
            progress?.Report($"Cleaning {item.Group} · {item.Name}");
            if (item.StopServiceFirst && item.ServiceName is not null)
                ProcessRunner.Run("net.exe", $"stop {item.ServiceName} /y", TimeSpan.FromMinutes(1));
            foreach (var t in item.Targets)
            {
                try { freed += Clean(t); }
                catch (Exception ex) { Log.Warn($"Clean failed for {t.Location}: {ex.Message}"); }
            }
            if (item.StopServiceFirst && item.ServiceName is not null)
                ProcessRunner.Run("net.exe", $"start {item.ServiceName}", TimeSpan.FromMinutes(1));
            cleaned++;
        }
        return new CleanerRunResult(cleaned, freed, skipped);
    }, ct);

    // ------------------------------------------------------------------

    private static (long Size, int Count) Measure(CleanTarget t)
    {
        switch (t.Kind)
        {
            case TargetKind.FolderContents:
                return Directory.Exists(t.Location) ? (FileOps.DirectorySize(t.Location), Directory.EnumerateFileSystemEntries(t.Location).Count()) : (0, 0);
            case TargetKind.FilePattern:
                var files = Files(t).ToList();
                return (files.Sum(f => SafeLen(f)), files.Count);
            case TargetKind.Path:
                return File.Exists(t.Location) || Directory.Exists(t.Location) ? (FileOps.SizeOf(t.Location), 1) : (0, 0);
            case TargetKind.RegistryValues:
            {
                using var k = RegistryUtil.Open(t.Location);
                return (0, k?.GetValueNames().Count(n => t.Pattern is null || Wildcard.IsMatch(n, t.Pattern)) ?? 0);
            }
            case TargetKind.RegistrySubKeys:
            {
                using var k = RegistryUtil.Open(t.Location);
                return (0, k?.SubKeyCount ?? 0);
            }
            case TargetKind.RegistryValue:
                return (0, RegistryUtil.ValueExists(t.Location, t.Pattern ?? "") ? 1 : 0);
            case TargetKind.RecycleBin:
            {
                var info = new Native.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHQUERYRBINFO>() };
                return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, (int)info.i64NumItems) : (0, 0);
            }
            case TargetKind.Clipboard:
                return (0, 1);
        }
        return (0, 0);
    }

    private static long Clean(CleanTarget t)
    {
        switch (t.Kind)
        {
            case TargetKind.FolderContents:
            {
                if (!Directory.Exists(t.Location)) return 0;
                long freed = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(t.Location).ToList())
                {
                    var size = FileOps.SizeOf(entry);
                    if (TryDelete(entry)) freed += size;
                }
                return freed;
            }
            case TargetKind.FilePattern:
            {
                long freed = 0;
                foreach (var f in Files(t).ToList())
                {
                    var size = SafeLen(f);
                    if (TryDelete(f)) freed += size;
                }
                return freed;
            }
            case TargetKind.Path:
            {
                var size = FileOps.SizeOf(t.Location);
                return TryDelete(t.Location) ? size : 0;
            }
            case TargetKind.RegistryValues:
            {
                using var k = RegistryUtil.Open(t.Location, writable: true);
                if (k is null) return 0;
                foreach (var n in k.GetValueNames().Where(n => t.Pattern is null || Wildcard.IsMatch(n, t.Pattern)))
                    k.DeleteValue(n, false);
                return 0;
            }
            case TargetKind.RegistrySubKeys:
            {
                using var k = RegistryUtil.Open(t.Location, writable: true);
                if (k is null) return 0;
                foreach (var s in k.GetSubKeyNames()) k.DeleteSubKeyTree(s, false);
                return 0;
            }
            case TargetKind.RegistryValue:
                RegistryUtil.DeleteValue(t.Location, t.Pattern ?? "");
                return 0;
            case TargetKind.RecycleBin:
            {
                var (size, _) = Measure(t);
                Native.SHEmptyRecycleBin(IntPtr.Zero, null, Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
                return size;
            }
            case TargetKind.Clipboard:
                if (Native.OpenClipboard(IntPtr.Zero))
                {
                    Native.EmptyClipboard();
                    Native.CloseClipboard();
                }
                return 0;
        }
        return 0;
    }

    private static IEnumerable<string> Files(CleanTarget t)
    {
        if (!Directory.Exists(t.Location)) return [];
        try
        {
            return Directory.EnumerateFiles(t.Location, t.Pattern ?? "*", new EnumerationOptions
            {
                RecurseSubdirectories = t.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch { return []; }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            return true;
        }
        catch
        {
            return false; // in-use files are simply skipped by cleaners
        }
    }

    private static long SafeLen(string f)
    {
        try { return new FileInfo(f).Length; } catch { return 0; }
    }
}
