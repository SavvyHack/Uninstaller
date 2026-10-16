using System.Text.Json;
using Microsoft.Win32;
using Reclaim.Core.Infrastructure;
using Reclaim.Core.Models;

namespace Reclaim.Core.Uninstall;

public enum BatchItemState { Pending, Running, Done, Failed, Skipped, Cancelled }

public sealed class BatchItem
{
    public required InstalledApp App { get; init; }
    public BatchItemState State { get; set; }
    public UninstallOutcome? Outcome { get; set; }
    public bool Skip { get; set; }
}

/// <summary>
/// Removes several apps in a row. Quiet uninstallers run in parallel (up to the configured concurrency),
/// but MSI packages always run one at a time because Windows Installer allows a single transaction.
/// Interactive uninstallers run one at a time so their windows don't pile up.
/// </summary>
public sealed class BatchUninstallQueue
{
    private readonly UninstallRunner _runner = new();
    private readonly SemaphoreSlim _msiGate = new(1, 1);
    private readonly SemaphoreSlim _interactiveGate = new(1, 1);
    private readonly ManualResetEventSlim _resume = new(true);

    public IReadOnlyList<BatchItem> Items { get; }
    public bool Quiet { get; }

    public event Action<BatchItem>? ItemChanged;

    public BatchUninstallQueue(IEnumerable<InstalledApp> apps, bool quiet)
    {
        Items = apps.Select(a => new BatchItem { App = a }).ToList();
        Quiet = quiet;
    }

    public bool IsPaused => !_resume.IsSet;
    public void Pause() => _resume.Reset();
    public void Resume() => _resume.Set();

    public async Task RunAsync(IProgress<string>? log, CancellationToken ct)
    {
        var concurrency = Math.Max(1, Quiet ? SettingsStore.Current.BatchConcurrency : 1);
        using var gate = new SemaphoreSlim(concurrency, concurrency);
        SavePending();

        var tasks = Items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await Task.Run(() => _resume.Wait(ct), ct);
                if (item.Skip)
                {
                    Update(item, BatchItemState.Skipped);
                    return;
                }
                var type = UninstallerTypeDetector.Detect(item.App);
                var quietCmd = Quiet ? SilentArgs.Build(item.App) : null;
                var serial = type == UninstallerType.Msi ? _msiGate : quietCmd is null ? _interactiveGate : null;
                if (serial is not null) await serial.WaitAsync(ct);
                try
                {
                    Update(item, BatchItemState.Running);
                    var prefix = new Progress<string>(m => log?.Report($"[{item.App.Name}] {m}"));
                    item.Outcome = await _runner.RunAsync(new UninstallRequest
                    {
                        App = item.App,
                        Quiet = Quiet,
                        Timeout = TimeSpan.FromMinutes(SettingsStore.Current.UninstallTimeoutMinutes),
                    }, prefix, ct);
                    Update(item, item.Outcome.IsSuccess ? BatchItemState.Done
                        : item.Outcome.Status == UninstallStatus.Cancelled ? BatchItemState.Cancelled : BatchItemState.Failed);
                }
                finally
                {
                    serial?.Release();
                }
            }
            catch (OperationCanceledException)
            {
                Update(item, BatchItemState.Cancelled);
            }
            finally
            {
                gate.Release();
                SavePending();
            }
        });
        await Task.WhenAll(tasks);

        if (Items.Any(i => i.Outcome?.Status == UninstallStatus.RebootRequired) && Items.Any(i => i.State == BatchItemState.Pending))
            RegisterResumeAfterReboot();
        else
            ClearPending();
    }

    private void Update(BatchItem item, BatchItemState state)
    {
        item.State = state;
        ItemChanged?.Invoke(item);
    }

    // ---------- reboot resume (keeps going with the remaining apps after a restart) ----------

    private void SavePending()
    {
        var pending = Items.Where(i => i.State is BatchItemState.Pending or BatchItemState.Running).Select(i => i.App.Id).ToList();
        try
        {
            File.WriteAllText(AppPaths.BatchStateFile, JsonSerializer.Serialize(new PendingBatch(pending, Quiet)));
        }
        catch { }
    }

    public static void ClearPending()
    {
        try { File.Delete(AppPaths.BatchStateFile); } catch { }
        using var k = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
        k?.DeleteValue("ReclaimResumeBatch", throwOnMissingValue: false);
    }

    public static PendingBatch? LoadPending()
    {
        try
        {
            return File.Exists(AppPaths.BatchStateFile)
                ? JsonSerializer.Deserialize<PendingBatch>(File.ReadAllText(AppPaths.BatchStateFile))
                : null;
        }
        catch { return null; }
    }

    private static void RegisterResumeAfterReboot()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        using var k = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
        k.SetValue("ReclaimResumeBatch", $"\"{exe}\" --resume-batch");
        Log.Info("Batch will resume after restart");
    }
}

public sealed record PendingBatch(List<string> AppIds, bool Quiet);
