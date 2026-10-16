using System.Globalization;
using System.Management;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Inventory;

/// <summary>Installed Windows updates (KBs). Removal goes through wusa.exe.</summary>
public sealed class WindowsUpdateProvider : IAppProvider
{
    public string Name => "Windows updates";

    public Task<IReadOnlyList<InstalledApp>> GetAppsAsync(CancellationToken ct) => Task.Run(() =>
    {
        var list = new List<InstalledApp>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT HotFixID, Description, InstalledOn, InstalledBy, Caption FROM Win32_QuickFixEngineering");
            foreach (ManagementObject mo in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                var kb = mo["HotFixID"]?.ToString();
                if (string.IsNullOrWhiteSpace(kb)) continue;
                DateTime? date = null;
                if (mo["InstalledOn"]?.ToString() is { } raw &&
                    DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
                var app = new InstalledApp
                {
                    Id = "kb:" + kb,
                    Name = $"{mo["Description"]} ({kb})",
                    Publisher = "Microsoft Corporation",
                    InstallDate = date,
                    HelpLink = mo["Caption"]?.ToString(),
                    UninstallString = $"wusa.exe /uninstall /kb:{kb.Replace("KB", "", StringComparison.OrdinalIgnoreCase)}",
                    QuietUninstallString = $"wusa.exe /uninstall /kb:{kb.Replace("KB", "", StringComparison.OrdinalIgnoreCase)} /quiet /norestart",
                    Source = AppSource.WindowsUpdate,
                    Flags = AppFlags.Update | AppFlags.HasQuietUninstall,
                };
                app.Extra["KB"] = kb;
                app.Extra["InstalledBy"] = mo["InstalledBy"]?.ToString() ?? "";
                list.Add(app);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Windows update enumeration failed", ex);
        }
        return (IReadOnlyList<InstalledApp>)list;
    }, ct);
}
