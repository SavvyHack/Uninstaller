using System.Management;
using Reclaim.Core.Infrastructure;

namespace Reclaim.Core.Backup;

public sealed record BackupSummary(string Folder, string Title, DateTime Created, int Items, long SizeBytes, bool Restored);

public sealed record RestorePointInfo(int SequenceNumber, string Description, DateTime Created);

public static class BackupManager
{
    public static List<BackupSummary> List()
    {
        var list = new List<BackupSummary>();
        foreach (var dir in Directory.EnumerateDirectories(AppPaths.Backups))
        {
            var s = BackupSession.Open(dir);
            if (s is null)
            {
                if (Directory.EnumerateFiles(dir, "*.reg").Any())
                    list.Add(new BackupSummary(dir, "Full registry backup", Directory.GetCreationTime(dir), Directory.EnumerateFiles(dir).Count(), FileOps.DirectorySize(dir), false));
                continue;
            }
            list.Add(new BackupSummary(dir, s.Manifest.Title, s.Manifest.Created, s.Manifest.Entries.Count, FileOps.DirectorySize(dir), s.Manifest.Restored));
        }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    public static void Delete(string folder)
    {
        if (PathUtil.IsUnder(folder, AppPaths.Backups)) FileOps.DeletePermanently(folder);
    }

    // ------------------------------------------------------------------ System Restore

    /// <summary>Asks Windows to create a restore point. Windows may skip it if one was created in the last 24 hours.</summary>
    public static async Task<(bool Ok, string Message)> CreateRestorePointAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
                using var inParams = cls.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description.Length > 60 ? description[..60] : description;
                inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                inParams["EventType"] = 100; // BEGIN_SYSTEM_CHANGE
                using var result = cls.InvokeMethod("CreateRestorePoint", inParams, null);
                var rc = Convert.ToInt32(result["ReturnValue"]);
                return rc == 0
                    ? (true, "Restore point created.")
                    : (false, rc == 1058 ? "System Restore is turned off on this PC." : $"Windows did not create a restore point (code {rc}).");
            }
            catch (Exception ex)
            {
                return (false, $"System Restore unavailable: {ex.Message}");
            }
        });
    }

    public static List<RestorePointInfo> ListRestorePoints()
    {
        var list = new List<RestorePointInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT * FROM SystemRestore");
            foreach (ManagementObject mo in searcher.Get())
            {
                var created = ManagementDateTimeConverter.ToDateTime(mo["CreationTime"]?.ToString() ?? "");
                list.Add(new RestorePointInfo(Convert.ToInt32(mo["SequenceNumber"]), mo["Description"]?.ToString() ?? "", created));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot list restore points: {ex.Message}");
        }
        return list.OrderByDescending(r => r.Created).ToList();
    }

    public static void OpenSystemRestore() => ProcessRunner.OpenShell("rstrui.exe");

    // ------------------------------------------------------------------ full registry backup

    public static async Task<(bool Ok, string Folder)> FullRegistryBackupAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(AppPaths.Backups, $"{DateTime.Now:yyyyMMdd-HHmmss}_Full registry");
        Directory.CreateDirectory(folder);
        var hives = new[] { ("HKLM\\SOFTWARE", "HKLM-SOFTWARE.reg"), ("HKLM\\SYSTEM", "HKLM-SYSTEM.reg"), ("HKCU", "HKCU.reg"), ("HKCR", "HKCR.reg") };
        var ok = true;
        foreach (var (hive, file) in hives)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Exporting {hive}…");
            var r = await ProcessRunner.RunAsync("reg.exe", $"export {hive} \"{Path.Combine(folder, file)}\" /y", TimeSpan.FromMinutes(15));
            ok &= r.ExitCode == 0;
        }
        return (ok, folder);
    }
}
