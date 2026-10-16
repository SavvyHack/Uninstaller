using Reclaim.Core.Backup;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Leftovers;

public sealed record CleanResult(int Removed, int Failed, long BytesFreed, string BackupFolder, List<string> Errors);

/// <summary>Removes selected leftovers in a safe order, backing each one up first.</summary>
public static class LeftoverCleaner
{
    public static Task<CleanResult> CleanAsync(IEnumerable<LeftoverItem> selected, string title, IProgress<string>? progress, CancellationToken ct)
        => Task.Run(() => Clean(selected, title, progress, ct), ct);

    public static CleanResult Clean(IEnumerable<LeftoverItem> selected, string title, IProgress<string>? progress, CancellationToken ct)
    {
        var items = selected.ToList();
        var session = BackupSession.Create(title);
        var mode = SettingsStore.Current.DeleteMode;
        int removed = 0, failed = 0;
        long freed = 0;
        var errors = new List<string>();

        // Services and tasks first (they may hold files open), then registry values, keys, and files deepest-first.
        var ordered = items
            .OrderBy(i => i.Kind switch
            {
                LeftoverKind.Service => 0,
                LeftoverKind.ScheduledTask => 1,
                LeftoverKind.FirewallRule => 2,
                LeftoverKind.EnvironmentPath => 3,
                LeftoverKind.RegistryValue => 4,
                LeftoverKind.RegistryKey => 5,
                LeftoverKind.File => 6,
                _ => 7,
            })
            .ThenByDescending(i => i.Path.Length)
            .ToList();

        // Re-validate against the guard's static lists right before deleting (defence in depth).
        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind is LeftoverKind.Directory or LeftoverKind.File && ProtectionGuard.IsProtectedPath(item.Path)
                && !item.Path.Contains(@"\Prefetch\", StringComparison.OrdinalIgnoreCase) && !item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Refused protected path {item.Path}");
                failed++;
                continue;
            }
            if (item.Kind == LeftoverKind.RegistryKey && ProtectionGuard.IsProtectedKey(item.Path))
            {
                errors.Add($"Refused protected key {item.Path}");
                failed++;
                continue;
            }

            progress?.Report($"Removing {item.Display}");
            try
            {
                if (session.Remove(item, mode))
                {
                    removed++;
                    freed += item.SizeBytes;
                }
                else
                {
                    failed++;
                    errors.Add($"Could not fully remove {item.Display} (it may be in use; it was scheduled for removal at restart)");
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{item.Display}: {ex.Message}");
                Log.Error($"Leftover removal failed: {item.Display}", ex);
            }
        }

        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        Log.Info($"Cleaned {removed} leftovers for '{title}', {failed} failed, backup at {session.Folder}");
        return new CleanResult(removed, failed, freed, session.Folder, errors);
    }
}
