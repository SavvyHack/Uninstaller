using System.Diagnostics;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Uninstall;

public enum UninstallStatus { Succeeded, RebootRequired, Cancelled, Failed, TimedOut, NothingToRun, StillInstalled }

public sealed record UninstallOutcome(UninstallStatus Status, int? ExitCode, string Message)
{
    public bool CanScanLeftovers => Status is not UninstallStatus.Cancelled;
    public bool IsSuccess => Status is UninstallStatus.Succeeded or UninstallStatus.RebootRequired or UninstallStatus.NothingToRun;
}

public sealed class UninstallRequest
{
    public required InstalledApp App { get; init; }
    public bool Quiet { get; init; }
    public bool ForcedMsi { get; init; }
    public bool StoreAllUsers { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Runs the right removal mechanism for any kind of app and reports a normalized outcome.</summary>
public sealed class UninstallRunner
{
    public async Task<UninstallOutcome> RunAsync(UninstallRequest req, IProgress<string>? log, CancellationToken ct)
    {
        var app = req.App;
        var type = UninstallerTypeDetector.Detect(app);
        log?.Report($"Uninstaller type: {type}");

        switch (type)
        {
            case UninstallerType.Store:
                return await StoreUninstaller.RemoveAsync(app, req.StoreAllUsers, log, ct);
            case UninstallerType.BrowserExtension:
                return await Task.Run(() => BrowserExtensionRemover.Remove(app, log), ct);
            case UninstallerType.None:
                return new(UninstallStatus.NothingToRun, null, "No uninstaller registered. Reclaim will remove what it finds in the leftover scan.");
            case UninstallerType.Steam:
                ProcessRunner.OpenShell($"steam://uninstall/{app.Extra.GetValueOrDefault("SteamAppId")}");
                return new(UninstallStatus.Succeeded, null, "Steam is removing the game. Finish in Steam, then scan for leftovers.");
        }

        string? command = req.ForcedMsi ? SilentArgs.ForcedMsi(app) : null;
        if (command is null && req.Quiet)
        {
            command = SilentArgs.Build(app);
            if (command is null) log?.Report("No silent switch known for this uninstaller; running it interactively.");
        }
        command ??= app.UninstallString;
        if (string.IsNullOrWhiteSpace(command))
            return new(UninstallStatus.NothingToRun, null, "No uninstall command found.");

        // Interactive MSI uninstall strings are often "/I{code}" (modify). Removal needs /X.
        if (type == UninstallerType.Msi && !req.Quiet && command.Contains("/I{", StringComparison.OrdinalIgnoreCase))
            command = command.Replace("/I{", "/X{", StringComparison.OrdinalIgnoreCase);

        if (SettingsStore.Current.ForceStopRunningExecutables) StopRunningProcesses(app, log);

        command = Environment.ExpandEnvironmentVariables(command);
        log?.Report($"Running: {command}");
        Log.Info($"Uninstall {app.Name}: {command}");

        var (exe, _) = CommandLine.Split(command);
        var workDir = Path.GetDirectoryName(PathUtil.Clean(exe) ?? "") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

        JobProcess job;
        try
        {
            job = JobProcess.Start(command, workDir);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start uninstaller for {app.Name}", ex);
            return new(UninstallStatus.Failed, null, ex.Message);
        }

        using (job)
        {
            int exit;
            try
            {
                exit = await job.WaitForTreeAsync(req.Timeout, ct);
            }
            catch (TimeoutException)
            {
                job.Kill();
                return new(UninstallStatus.TimedOut, null, $"The uninstaller did not finish within {req.Timeout.TotalMinutes:0} minutes and was stopped.");
            }
            catch (OperationCanceledException)
            {
                job.Kill();
                return new(UninstallStatus.Cancelled, null, "Cancelled.");
            }

            log?.Report($"Uninstaller exited with code {exit}.");
            var stillThere = app.RegistryKeyPath is not null && RegistryUtil.KeyExists(app.RegistryKeyPath);
            return exit switch
            {
                3010 or 1641 => new(UninstallStatus.RebootRequired, exit, "Uninstalled. Windows needs a restart to finish."),
                1602 or 1223 => new(UninstallStatus.Cancelled, exit, "The uninstall was cancelled."),
                1605 => new(UninstallStatus.Succeeded, exit, "The product was already removed."),
                0 when stillThere => new(UninstallStatus.StillInstalled, exit,
                    "The uninstaller closed but the program is still registered. It may have been cancelled, or it needs a restart."),
                0 => new(UninstallStatus.Succeeded, exit, "The built-in uninstaller finished."),
                _ when !stillThere => new(UninstallStatus.Succeeded, exit, $"Uninstaller returned {exit} but the program is gone."),
                _ => new(UninstallStatus.Failed, exit, $"The uninstaller failed with exit code {exit}."),
            };
        }
    }

    /// <summary>Stops processes running from the app's folder so files are not left locked.</summary>
    public static void StopRunningProcesses(InstalledApp app, IProgress<string>? log)
    {
        var loc = app.InstallLocation;
        if (string.IsNullOrEmpty(loc) || loc.Length <= 3 || !Directory.Exists(loc)) return;
        var uninstaller = PathUtil.ExecutableFromCommand(app.UninstallString);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = Native.ProcessImagePath((uint)p.Id);
                if (path is null || !PathUtil.IsUnder(path, loc) || PathUtil.SamePath(path, uninstaller)) continue;
                log?.Report($"Stopping {Path.GetFileName(path)} (PID {p.Id})");
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5000);
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }
}
