using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;
using Windows.Management.Deployment;

namespace Reclaim.Core.Uninstall;

public static class StoreUninstaller
{
    public static async Task<UninstallOutcome> RemoveAsync(InstalledApp app, bool allUsers, IProgress<string>? log, CancellationToken ct)
    {
        if (!app.Extra.TryGetValue("PackageFullName", out var fullName))
            return new(UninstallStatus.Failed, null, "Missing package name.");
        if (app.Has(AppFlags.NonRemovable))
            return new(UninstallStatus.Failed, null, "This is a protected system app and Windows does not allow removing it.");

        if (allUsers)
        {
            // All-users removal and de-provisioning (so new accounts don't get the app) go through the Appx module.
            var family = app.Extra.GetValueOrDefault("PackageFamilyName") ?? "";
            var name = family.Contains('_') ? family[..family.LastIndexOf('_')] : family;
            var script =
                $"Get-AppxPackage -AllUsers -Name '{name}' | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue; " +
                $"Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq '{name}' | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue";
            log?.Report("Removing for all users and de-provisioning…");
            var r = await ProcessRunner.RunAsync("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"", TimeSpan.FromMinutes(10));
            return r.ExitCode == 0
                ? new(UninstallStatus.Succeeded, 0, "Removed for all users.")
                : new(UninstallStatus.Failed, r.ExitCode, r.StdErr.Trim());
        }

        try
        {
            log?.Report($"Removing package {fullName}…");
            var pm = new PackageManager();
            var result = await pm.RemovePackageAsync(fullName, RemovalOptions.None).AsTask(ct);
            if (result.ExtendedErrorCode is not null)
                return new(UninstallStatus.Failed, result.ExtendedErrorCode.HResult, result.ErrorText);
            return new(UninstallStatus.Succeeded, 0, "The app was removed.");
        }
        catch (OperationCanceledException)
        {
            return new(UninstallStatus.Cancelled, null, "Cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error($"Store removal failed for {fullName}", ex);
            return new(UninstallStatus.Failed, ex.HResult, ex.Message);
        }
    }
}
