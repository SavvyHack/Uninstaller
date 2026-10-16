using Reclaim.Core.Infrastructure;
using Reclaim.Core.Inventory;
using Reclaim.Core.Models;

namespace Reclaim.Core.Monitor;

/// <summary>
/// Notices programs uninstalled outside Reclaim (e.g. from Windows Settings) while the app is open,
/// so their leftovers can still be scanned. The last known app details are kept for the scan.
/// </summary>
public sealed class UninstallWatcher : IDisposable
{
    private readonly Dictionary<string, InstalledApp> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expected = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    public event Action<InstalledApp>? ExternalUninstallDetected;

    public void UpdateKnown(IEnumerable<InstalledApp> apps)
    {
        lock (_gate)
        {
            _known.Clear();
            foreach (var a in apps.Where(a => a.Source == AppSource.Win32 && a.RegistryKeyPath is not null))
                _known[a.RegistryKeyPath!] = a;
        }
    }

    /// <summary>Call before Reclaim itself removes an app so it is not reported as external.</summary>
    public void Expect(InstalledApp app)
    {
        if (app.RegistryKeyPath is not null) lock (_gate) _expected.Add(app.RegistryKeyPath);
    }

    public void Start()
    {
        foreach (var (root, _, _) in RegistryUninstallProvider.UninstallRoots().Take(4))
        {
            var path = root;
            _ = Task.Run(() => WatchLoop(path, _cts.Token));
        }
    }

    private void WatchLoop(string keyPath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var key = RegistryUtil.Open(keyPath);
                if (key is null) return;
                using var evt = new AutoResetEvent(false);
                var rc = Native.RegNotifyChangeKeyValue(key.Handle, false, Native.REG_NOTIFY_CHANGE_NAME, evt.SafeWaitHandle, true);
                if (rc != 0) return;
                WaitHandle.WaitAny([evt, ct.WaitHandle]);
                if (ct.IsCancellationRequested) return;
                Thread.Sleep(1500); // let the uninstaller finish removing its entry
                CheckRemoved(keyPath, key.GetSubKeyNames());
            }
            catch (Exception ex)
            {
                Log.Warn($"Uninstall watcher error: {ex.Message}");
                Thread.Sleep(5000);
            }
        }
    }

    private void CheckRemoved(string rootPath, string[] currentNames)
    {
        var current = new HashSet<string>(currentNames.Select(n => $@"{rootPath}\{n}"), StringComparer.OrdinalIgnoreCase);
        List<InstalledApp> removed;
        lock (_gate)
        {
            removed = _known.Where(kv => kv.Key.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase) && !current.Contains(kv.Key))
                .Select(kv => kv.Value).ToList();
            foreach (var r in removed) _known.Remove(r.RegistryKeyPath!);
            removed = removed.Where(r => !_expected.Remove(r.RegistryKeyPath!)).ToList();
        }
        foreach (var app in removed)
        {
            Log.Info($"External uninstall detected: {app.Name}");
            ExternalUninstallDetected?.Invoke(app);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
